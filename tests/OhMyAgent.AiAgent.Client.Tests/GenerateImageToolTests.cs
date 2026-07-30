using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using OhMyAgent.AiAgent.Client.Services.Tools;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// generate_image 도구의 계약. 서버는 절대 호출하지 않는다(가짜 API 클라이언트 / 가짜 HttpMessageHandler).
///
/// 지키려는 것:
///  - 쿼터를 태우기 전에 인자·경로를 검증한다(잘못된 호출에서 API 호출 횟수 0).
///  - 워크스페이스를 벗어나지 못한다.
///  - 기존 파일을 덮어쓰지 않는다.
///  - PNG 가 아닌 응답을 .png 로 저장하지 않는다.
///  - 모델에게 base64 를 돌려주지 않는다.
/// </summary>
public sealed class GenerateImageToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;
    private readonly ToolContext _ctx;

    public GenerateImageToolTests()
    {
        _tempRoot  = Path.Combine(Path.GetTempPath(), "omg-image-tests", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_workspace);

        var workspace = new WorkspaceContext(new FakeSettingsService());
        workspace.SetRoot(_workspace);
        _ctx = new ToolContext(workspace, PermissionMode.FullAuto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* 임시 폴더 — 잔존 무시 */ }
    }

    // ── 테스트 지원 ────────────────────────────────────────────────

    /// <summary>요청을 기록하고 미리 정한 이미지를 돌려주는 가짜 서버 클라이언트.</summary>
    private sealed class FakeImageApi(params string[] base64Images) : StubAgentApi
    {
        public readonly List<ImageGenerationRequest> Requests = [];

        /// <summary>null 이 아니면 이 예외를 던진다(서버 실패 경로 재현).</summary>
        public AgentException? Failure { get; set; }

        public string? RevisedPrompt { get; set; }

        public override Task<ImageGenerationResponse> GenerateImagesAsync(
            ImageGenerationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Failure is not null) throw Failure;

            var images = base64Images
                .Take(request.Count)
                .Select(b64 => new GeneratedImage(b64, RevisedPrompt))
                .ToList();
            return Task.FromResult(new ImageGenerationResponse(images));
        }
    }

    /// <summary>유효한 최소 PNG 바이트(시그니처 + 더미 본문).</summary>
    private static byte[] PngBytes(int payload = 32)
    {
        var bytes = new byte[8 + payload];
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(bytes, 0);
        for (var i = 8; i < bytes.Length; i++) bytes[i] = (byte)i;
        return bytes;
    }

    private static string Png(int payload = 32) => Convert.ToBase64String(PngBytes(payload));

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonDocument Parse(ToolResult result) => JsonDocument.Parse(result.Content);

    // ── 인자 검증(쿼터를 쓰기 전에 막는다) ──────────────────────────

    [Fact]
    public async Task EmptyPrompt_fails_without_calling_server()
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"   "}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("prompt", result.Content);
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("1024")]          // 구분자 없음
    [InlineData("1024x")]         // 한쪽 없음
    [InlineData("100x100")]       // 하한 미달
    [InlineData("8192x8192")]     // 상한 초과
    [InlineData("-1024x1024")]    // 음수
    public async Task InvalidSize_fails_without_calling_server(string size)
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args($$"""{"prompt":"cat","size":"{{size}}"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("size", result.Content);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task DefaultSizeAndFormat_are_sent()
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        await tool.ExecuteAsync(Args("""{"prompt":"cat"}"""), _ctx);

        var req = Assert.Single(api.Requests);
        Assert.Equal("1024x1024", req.Size);
        Assert.Equal("png", req.Format);
        Assert.Equal(1, req.Count);
    }

    [Fact]
    public async Task Count_is_clamped_to_quota_cap()
    {
        // 상한(4)을 넘는 요청은 실패가 아니라 클램프 — 다만 실제 장수를 결과에 알려야 한다.
        var api = new FakeImageApi(Png(), Png(), Png(), Png(), Png(), Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","count":10}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.Equal(4, api.Requests[0].Count);

        using var doc = Parse(result);
        Assert.Equal(4, doc.RootElement.GetProperty("images").GetArrayLength());
        Assert.Contains("상한", doc.RootElement.GetProperty("note").GetString());
    }

    // ── 샌드박스 ──────────────────────────────────────────────────

    [Fact]
    public async Task PathEscape_is_blocked_before_the_server_is_called()
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        await Assert.ThrowsAsync<AgentException>(() =>
            tool.ExecuteAsync(Args("""{"prompt":"cat","path":"../escaped.png"}"""), _ctx));

        // 거부될 경로에 쿼터를 쓰지 않아야 한다.
        Assert.Empty(api.Requests);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "escaped.png")));
    }

    [Fact]
    public async Task AbsolutePathOutsideWorkspace_is_blocked()
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);
        var outside = Path.Combine(_tempRoot, "outside.png").Replace("\\", "\\\\");

        await Assert.ThrowsAsync<AgentException>(() =>
            tool.ExecuteAsync(Args($$"""{"prompt":"cat","path":"{{outside}}"}"""), _ctx));

        Assert.Empty(api.Requests);
    }

    // ── 저장 동작 ─────────────────────────────────────────────────

    [Fact]
    public async Task SavesPngAndReturnsMetadataWithoutBase64()
    {
        var api = new FakeImageApi(Png(64)) { RevisedPrompt = "a cat, watercolor" };
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","path":"art/cat.png"}"""), _ctx);

        Assert.False(result.IsError, result.Content);

        var expected = Path.Combine(_workspace, "art", "cat.png");
        Assert.True(File.Exists(expected));
        Assert.Equal(PngBytes(64), File.ReadAllBytes(expected));

        using var doc = Parse(result);
        Assert.Equal(expected, doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(72, doc.RootElement.GetProperty("bytes").GetInt32());
        Assert.Equal("1024x1024", doc.RootElement.GetProperty("size").GetString());
        Assert.Equal("a cat, watercolor", doc.RootElement.GetProperty("revised_prompt").GetString());

        // 컴팩션을 말리는 base64 는 모델 결과에 절대 들어가지 않는다.
        Assert.DoesNotContain("b64", result.Content);
        Assert.DoesNotContain(Png(64), result.Content);
    }

    [Fact]
    public async Task DefaultPath_lands_in_workspace_images_folder()
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        using var doc = Parse(result);
        var saved = doc.RootElement.GetProperty("path").GetString()!;

        Assert.StartsWith(Path.Combine(_workspace, "images"), saved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".png", saved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(saved));
    }

    [Fact]
    public async Task ExistingFile_is_never_overwritten()
    {
        var target = Path.Combine(_workspace, "cat.png");
        File.WriteAllText(target, "PRECIOUS");

        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","path":"cat.png"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        Assert.Equal("PRECIOUS", File.ReadAllText(target));   // 원본 그대로

        using var doc = Parse(result);
        var saved = doc.RootElement.GetProperty("path").GetString()!;
        Assert.NotEqual(target, saved);
        Assert.Equal(Path.Combine(_workspace, "cat-2.png"), saved);
    }

    [Fact]
    public async Task NonPngExtension_is_corrected_to_png()
    {
        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","path":"cat.jpg"}"""), _ctx);

        using var doc = Parse(result);
        Assert.Equal(Path.Combine(_workspace, "cat.jpg.png"), doc.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task DirectoryPath_gets_a_generated_filename_inside_it()
    {
        var dir = Path.Combine(_workspace, "out");
        Directory.CreateDirectory(dir);

        var api = new FakeImageApi(Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","path":"out"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        using var doc = Parse(result);
        var saved = doc.RootElement.GetProperty("path").GetString()!;
        Assert.Equal(dir, Path.GetDirectoryName(saved));
    }

    [Fact]
    public async Task MultipleImages_get_distinct_paths()
    {
        var api = new FakeImageApi(Png(), Png(48));
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","path":"cat.png","count":2}"""), _ctx);

        Assert.False(result.IsError, result.Content);
        using var doc = Parse(result);
        var paths = doc.RootElement.GetProperty("images")
            .EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();

        Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    // ── 응답 검증 ─────────────────────────────────────────────────

    [Fact]
    public async Task NonPngResponse_is_rejected_and_no_file_is_written()
    {
        // 서버가 PNG 가 아닌 것(여기서는 JPEG 시그니처)을 보냈다 — .png 로 저장해 두면 안 된다.
        var jpeg = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5 });
        var api = new FakeImageApi(jpeg);
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat","path":"cat.png"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("PNG", result.Content);
        Assert.False(File.Exists(Path.Combine(_workspace, "cat.png")));
    }

    [Fact]
    public async Task InvalidBase64_is_rejected()
    {
        var api = new FakeImageApi("not-base64!!!");
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat"}"""), _ctx);

        Assert.True(result.IsError);
        Assert.Contains("base64", result.Content);
    }

    [Fact]
    public async Task DataUriPrefix_is_accepted()
    {
        var api = new FakeImageApi("data:image/png;base64," + Png());
        var tool = new GenerateImageTool(api);

        var result = await tool.ExecuteAsync(Args("""{"prompt":"cat"}"""), _ctx);

        Assert.False(result.IsError, result.Content);
    }

    [Fact]
    public async Task ServerFailure_message_reaches_the_model_verbatim()
    {
        var api = new FakeImageApi(Png())
        {
            Failure = new AgentException("서버에 이미지 생성 엔드포인트가 없습니다 — 재시도해도 동일합니다."),
        };
        var tool = new GenerateImageTool(api);

        // 도구는 문구를 감추거나 바꾸지 않는다(오케스트레이터가 그대로 도구 실패로 전달한다).
        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            tool.ExecuteAsync(Args("""{"prompt":"cat"}"""), _ctx));
        Assert.Contains("재시도해도 동일", ex.Message);
    }

    // ── 도구 메타데이터 ────────────────────────────────────────────

    [Fact]
    public void Tool_is_a_write_tool_so_it_gets_an_approval_card()
    {
        var tool = new GenerateImageTool(new FakeImageApi());

        Assert.Equal("generate_image", tool.Name);
        Assert.Equal(ToolRisk.Write, tool.Risk);
        // 승인 카드는 파일이 만들어진다는 사실이 읽혀야 한다.
        Assert.Contains("이미지 파일 생성", tool.Description);
    }
}

/// <summary>
/// POST /api/v1/images/generations 의 클라이언트 측 계약. 실제 서버 대신 가짜 HttpMessageHandler 를 쓴다.
/// 서버에는 아직 이 엔드포인트가 없으므로, 404 안내 문구가 여기서 못 박히는 유일한 지점이다.
/// </summary>
public sealed class ImageGenerationApiTests
{
    private sealed class FakeHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AgentApiClient MakeClient(FakeHandler handler)
    {
        var settings = new FakeSettingsService();
        settings.Current.ServerBaseUrl = "http://localhost:8080";
        settings.Current.AuthToken = "jwt-abc";
        return new AgentApiClient(new HttpClient(handler), settings);
    }

    private static ImageGenerationRequest Request()
        => new("a cat", "1024x1024", 1, "png");

    [Fact]
    public async Task Posts_snake_case_contract_with_bearer_token()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK,
            """{"images":[{"b64":"AAAA","revised_prompt":"a cat, watercolor"}],"usage":{"images":1}}"""));
        var client = MakeClient(handler);

        var resp = await client.GenerateImagesAsync(Request());

        Assert.Equal("AAAA", resp.Images![0].Base64);
        Assert.Equal("a cat, watercolor", resp.Images[0].RevisedPrompt);

        var (method, path, body) = handler.Requests[0];
        Assert.Equal("POST", method);
        Assert.Equal("/api/v1/images/generations", path);
        // 계약 필드명이 camelCase 로 나가면 서버가 못 읽는다.
        Assert.Contains("\"prompt\"", body);
        Assert.Contains("\"size\"", body);
        Assert.Contains("\"count\"", body);
        Assert.Contains("\"format\"", body);
    }

    [Fact]
    public async Task Missing_endpoint_404_says_retrying_will_not_help()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.NotFound,
            """{"error":{"code":"not_found","message":"no route"}}"""));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GenerateImagesAsync(Request()));

        Assert.Contains("/api/v1/images/generations", ex.Message);
        Assert.Contains("관리자", ex.Message);
        Assert.Contains("재시도해도 동일", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Other_failures_use_the_shared_korean_translation(HttpStatusCode status)
    {
        var handler = new FakeHandler((_, _) => Json(status, """{"error":{"code":"boom","message":"raw server text"}}"""));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GenerateImagesAsync(Request()));

        Assert.StartsWith("이미지 생성 실패:", ex.Message);
        // 서버 원문은 사용자/모델에게 노출하지 않는다(기존 관례).
        Assert.DoesNotContain("raw server text", ex.Message);
    }

    /// <summary>
    /// 401 은 "일시적인 서버 오류" 로 뭉개지면 안 된다.
    ///
    /// 이 경로에는 재로그인 승격 장치가 없다 — 스트리밍 채팅은 <c>ClassifyAgentError</c> 가 401 을
    /// 낚아채 로그인 화면으로 보내지만, 도구 실패는 그 문구가 그대로 모델·사용자에게 간다.
    /// 만료를 "일시 장애"로 말하면 사용자는 로그인해야 하는 줄 모르고, 모델은 같은 호출을 재시도해
    /// 턴만 태운다. 그래서 <c>UserErrorMessages.ForAgentError</c> 가 401 을 별도로 구분해야 한다.
    /// </summary>
    [Theory]
    [InlineData("unauthorized")]
    [InlineData("http_401")]
    public async Task Expired_token_401_tells_the_user_to_log_in_again(string code)
    {
        var handler = new FakeHandler((_, _) => Json(
            HttpStatusCode.Unauthorized, $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"token expired\"}}}}"));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GenerateImagesAsync(Request()));

        Assert.Contains("로그인", ex.Message);
        Assert.DoesNotContain("일시적인 서버 오류", ex.Message);
        Assert.DoesNotContain("token expired", ex.Message);   // 서버 원문 비노출은 그대로 유지
    }

    /// <summary>403 은 401 과 다른 문구여야 한다 — 로그인해도 풀리지 않는 실패다.</summary>
    [Fact]
    public async Task Forbidden_403_does_not_ask_the_user_to_log_in_again()
    {
        var handler = new FakeHandler((_, _) => Json(
            HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"policy"}}"""));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GenerateImagesAsync(Request()));

        Assert.Contains("권한이 없습니다", ex.Message);
        Assert.DoesNotContain("로그인", ex.Message);
    }

    [Fact]
    public async Task Empty_images_array_fails_clearly()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"images":[]}"""));
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GenerateImagesAsync(Request()));
        Assert.Contains("images", ex.Message);
    }

    [Fact]
    public async Task Oversized_response_is_refused_before_it_is_buffered()
    {
        // Content-Length 만 거대하게 광고해도 거부돼야 한다 — 실제로 다 읽으면 그 자체가 사고다.
        var handler = new FakeHandler((_, _) =>
        {
            var resp = Json(HttpStatusCode.OK, """{"images":[{"b64":"AAAA"}]}""");
            resp.Content.Headers.ContentLength = 64L * 1024 * 1024;
            return resp;
        });
        var client = MakeClient(handler);

        var ex = await Assert.ThrowsAsync<AgentException>(() => client.GenerateImagesAsync(Request()));
        Assert.Contains("너무 큽니다", ex.Message);
    }
}
