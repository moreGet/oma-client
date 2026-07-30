using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

/// <summary>
/// generate_image (Risk=Write) — 프롬프트로 이미지를 만들어 <b>워크스페이스 안에 PNG 파일로 저장</b>한다.
/// 승인 카드에서 읽어야 하는 문구는 "이미지 파일 생성"이다(파일을 만드므로 쓰기 위험도).
///
/// 이미지 모델을 직접 호출하지 않는다 — 사내 서버(POST /api/v1/images/generations)가 대리 호출한다.
/// 그래서 이 도구에는 provider/model/api key 인자가 없고, 앱에 이미지 모델 키를 두지 않는다.
///
/// <b>모델에게 base64 를 돌려주지 않는다</b>: 이미지 한 장의 base64 는 수천~수만 토큰이라 그대로 대화에
/// 들어가면 이력이 즉시 예산을 넘고 컴팩션이 요약할 수 없는 쓰레기로 채워진다. 파일로 떨어뜨리고
/// 경로·크기만 돌려주면 이후 턴에서 필요할 때 그 파일을 다시 다룰 수 있다.
///
/// 샌드박스: 저장 경로는 전부 <see cref="IWorkspaceContext.ResolvePath"/> 를 지난다(쓰기 도구 공통 관례).
/// 검증은 <b>서버 호출 전에</b> 한다 — 경로가 거부될 요청에 쿼터를 태우지 않기 위해서다.
/// </summary>
public sealed class GenerateImageTool(IAgentApiClient api) : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{
          "prompt":{"type":"string","description":"What to draw. English usually gives better results."},
          "path":{"type":"string","description":"Destination .png path inside the workspace. A directory is also accepted (a filename is generated inside it). Omit for 'images/image-<timestamp>.png'."},
          "size":{"type":"string","description":"WIDTHxHEIGHT, e.g. '1024x1024' (default). Each side 256..4096."},
          "count":{"type":"number","description":"How many images (1..4, default 1). Each one costs quota."}
        },"required":["prompt"]}
        """);

    /// <summary>
    /// 한 번에 만들 수 있는 장수 상한.
    ///
    /// 왜 상한이 필요한가: 이미지 1장은 텍스트 응답과 비교할 수 없는 단가이고, 모델이 "여러 시안"을
    /// 원해 count=20 을 넣는 것은 아주 자연스러운 판단이다. 그 한 번의 호출로 조직 쿼터가 날아간다.
    /// 4로 잡은 근거: 시안 비교는 보통 2~4장이면 충분하고, 더 필요하면 도구를 다시 부르면 된다
    /// (다시 부르는 비용은 턴 하나뿐이지만, 상한 없는 폭주는 되돌릴 수 없다).
    /// </summary>
    private const int MaxCount = 4;

    /// <summary>프롬프트 길이 상한 — 모델이 대화 전문을 프롬프트에 붓는 사고를 막고, 서버 400 왕복도 아낀다.</summary>
    private const int MaxPromptChars = 4_000;

    /// <summary>변 길이 허용 범위. 실제 지원 목록의 진실 원천은 서버/이미지 모델이므로 여기서는 형태와 범위만 본다.</summary>
    private const int MinDimension = 256;
    private const int MaxDimension = 4096;

    private const string DefaultSize = "1024x1024";

    /// <summary>서버에 요청하는 포맷은 png 로 고정한다 — 저장 확장자·매직바이트 검증이 모두 png 기준이다.</summary>
    private const string Format = "png";

    /// <summary>
    /// 이미지 1장 디코드 후 바이트 상한(16MB). 응답 전체 상한(AgentApiClient)이 메모리를 막는 1차 방어선이고,
    /// 이 상한은 "한 장이 비정상적으로 크다"를 개별로 걸러 디스크에 쏟아붓지 않게 하는 2차 방어선이다.
    /// </summary>
    private const int MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>디코드 전에 base64 문자열 길이로 먼저 거른다 — 디코드 자체가 메모리를 쓰기 때문이다(4/3 팽창).</summary>
    private const int MaxImageBase64Chars = (MaxImageBytes / 3 + 1) * 4;

    /// <summary>충돌 회피 접미사 탐색 상한. 여기까지 다 찼으면 경로 선택 자체가 잘못된 상황이다.</summary>
    private const int MaxUniqueAttempts = 999;

    /// <summary>PNG 시그니처. 서버가 실제로 png 를 줬는지 확인한다(에러 JSON·jpeg·빈 바이트를 .png 로 저장하지 않기 위해).</summary>
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public string Name => "generate_image";

    public string Description =>
        "Generate an image from a text prompt and save it as a PNG file in the workspace (이미지 파일 생성). " +
        "The image is produced by the company server on your behalf — you never handle model credentials. " +
        "Returns only {path, bytes, size, revised_prompt}: the base64 is deliberately NOT returned, so read or " +
        "attach the file if the image itself is needed. Existing files are never overwritten — a numbered suffix " +
        "is added instead. Each image costs quota, so keep 'count' as low as the task allows.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>파일을 만들므로 Write — Manual 모드에서 "이미지 파일 생성" 승인 카드를 띄운다.</summary>
    public ToolRisk Risk => ToolRisk.Write;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var prompt = ToolSchemas.GetString(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("prompt 가 비어 있습니다. 무엇을 그릴지 서술하세요.");
        if (prompt.Length > MaxPromptChars)
            return ToolResult.Fail($"prompt 가 너무 깁니다({prompt.Length}자 > 상한 {MaxPromptChars}자).");

        var sizeArg = ToolSchemas.GetString(args, "size");
        if (!TryNormalizeSize(sizeArg, out var size))
            return ToolResult.Fail(
                $"size 형식이 올바르지 않습니다: '{sizeArg}'. " +
                $"'너비x높이' 형태여야 하고 각 변은 {MinDimension}~{MaxDimension} 입니다(예: {DefaultSize}).");

        // 상한 초과는 실패가 아니라 클램프로 처리한다 — 실패로 돌리면 모델이 같은 호출을 count 만 줄여
        // 다시 보내며 턴을 낭비한다. 대신 실제 생성 장수를 결과에 담아 착각을 막는다.
        var requested = ToolSchemas.GetInt(args, "count") ?? 1;
        var count = Math.Clamp(requested, 1, MaxCount);

        // 1) 저장 경로를 서버 호출 전에 확정한다 — 샌드박스 위반이면 쿼터를 쓰지 않고 여기서 끝난다.
        //    ResolvePath 의 AgentException(경로 탈출/확인 불가)은 그대로 전파한다(쓰기 도구 공통 관례 —
        //    오케스트레이터가 같은 문구로 도구 실패로 바꿔 모델에게 전달한다).
        List<string> targets;
        try
        {
            targets = ResolveTargets(ToolSchemas.GetString(args, "path"), count, ctx);
        }
        catch (AgentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"저장 경로를 준비할 수 없습니다: {ex.Message}");
        }

        // 2) 생성 — 서버가 이미지 모델을 대리 호출한다. 실패(404 미구현/401/429/5xx)는 AgentException 으로
        //    사유가 담겨 올라오며, 오케스트레이터가 그 문구를 그대로 모델에게 전달한다.
        var response = await api
            .GenerateImagesAsync(new ImageGenerationRequest(prompt, size, count, Format), ct)
            .ConfigureAwait(false);

        // 3) 저장 — 서버가 요청보다 적게 줄 수도 있으므로 실제 받은 만큼만 처리한다.
        var saved = new List<object>();
        var images = response.Images ?? [];
        for (var i = 0; i < images.Count && i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryDecodePng(images[i].Base64, out var bytes, out var reason))
                return ToolResult.Fail($"{i + 1}번째 이미지를 저장하지 못했습니다: {reason}");

            var target = targets[i];
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);

            saved.Add(new
            {
                path = target,
                bytes = bytes.Length,
                size,
                revised_prompt = images[i].RevisedPrompt,
            });
        }

        if (saved.Count == 0)
            return ToolResult.Fail("서버가 이미지를 하나도 반환하지 않았습니다.");

        // 1장이면 평면 객체(합의된 결과 형태), 여러 장이면 images 배열로 감싼다.
        if (saved.Count == 1 && requested <= 1)
            return ToolResult.Json(saved[0]);

        return ToolResult.Json(new
        {
            images = saved,
            // 클램프됐거나 서버가 덜 준 경우를 알려준다 — 모델이 요청 장수를 받았다고 착각하면 다음 판단이 어긋난다.
            note = saved.Count == requested
                ? null
                : $"요청 {requested}장 중 {saved.Count}장을 저장했습니다(1회 상한 {MaxCount}장).",
        });
    }

    /// <summary>
    /// 저장 대상 경로 목록을 만든다. 규칙:
    ///  - path 미지정(또는 디렉터리 지정) → 워크스페이스 하위 images/ 에 타임스탬프 파일명.
    ///  - 확장자가 .png 가 아니면 .png 를 붙인다(항상 png 만 저장하므로 확장자가 어긋나면 뷰어가 못 연다).
    ///  - 기존 파일은 절대 덮어쓰지 않는다 — 접미사(-2, -3 …)를 붙인 새 경로를 쓴다.
    /// </summary>
    private static List<string> ResolveTargets(string? pathArg, int count, ToolContext ctx)
    {
        // 상대/절대 모두 ResolvePath 가 워크스페이스 안으로 강제한다. 비어 있으면 Root(디렉터리)가 나온다.
        var resolved = ctx.Workspace.ResolvePath(string.IsNullOrWhiteSpace(pathArg) ? DefaultRelativePath() : pathArg);

        // 디렉터리를 지목했으면 그 안에 파일명을 만들어 준다 — 실패로 돌리면 턴만 낭비된다.
        var baseFull = Directory.Exists(resolved)
            ? Path.Combine(resolved, DefaultFileName())
            : resolved;

        if (!string.Equals(Path.GetExtension(baseFull), ".png", StringComparison.OrdinalIgnoreCase))
            baseFull += ".png";

        var dir  = Path.GetDirectoryName(baseFull) ?? "";
        var stem = Path.GetFileNameWithoutExtension(baseFull);

        // 같은 호출 안에서 만든 경로끼리도 겹치면 안 된다(파일이 아직 없어 File.Exists 로는 못 걸린다).
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var name = count == 1 ? $"{stem}.png" : $"{stem}-{i + 1}.png";
            targets.Add(UniquePath(Path.Combine(dir, name), reserved));
        }

        return targets;
    }

    /// <summary>기본 저장 경로(워크스페이스 상대). 루트에 파일을 흩뿌리지 않고 images/ 한곳에 모은다.</summary>
    private static string DefaultRelativePath() => Path.Combine("images", DefaultFileName());

    /// <summary>기본 파일명 — 사용자가 파일 탐색기에서 시간순으로 알아볼 수 있게 로컬 시각을 쓴다.</summary>
    private static string DefaultFileName()
        => $"image-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.png";

    /// <summary>기존 파일을 건드리지 않는 경로를 고른다(덮어쓰기 금지).</summary>
    private static string UniquePath(string full, HashSet<string> reserved)
    {
        if (!File.Exists(full) && reserved.Add(full))
            return full;

        var dir  = Path.GetDirectoryName(full) ?? "";
        var stem = Path.GetFileNameWithoutExtension(full);
        var ext  = Path.GetExtension(full);

        for (var i = 2; i <= MaxUniqueAttempts; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(candidate) && reserved.Add(candidate))
                return candidate;
        }

        throw new AgentException($"저장할 파일명을 찾지 못했습니다(같은 이름이 이미 {MaxUniqueAttempts}개): {full}");
    }

    /// <summary>'너비x높이' 검증 후 정규화. 미지정이면 기본값.</summary>
    private static bool TryNormalizeSize(string? value, out string normalized)
    {
        normalized = DefaultSize;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var parts = value.Trim().Split(['x', 'X'], StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w < MinDimension || w > MaxDimension
            || h < MinDimension || h > MaxDimension)
            return false;

        normalized = $"{w}x{h}";
        return true;
    }

    /// <summary>
    /// base64 → PNG 바이트. 매직바이트까지 확인한다 — 서버가 에러 JSON 이나 다른 포맷을 보냈을 때
    /// 그것을 .png 로 저장해 두면 나중에 열리지 않는 파일의 원인을 추적하기 어렵다.
    /// </summary>
    private static bool TryDecodePng(string? base64, out byte[] bytes, out string reason)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(base64))
        {
            reason = "응답에 b64(base64 이미지)가 없습니다.";
            return false;
        }

        // 데이터 URI 로 오는 서버도 있어 접두사는 허용하되 잘라낸다("data:image/png;base64,…").
        var payload = base64;
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',');
            if (comma < 0)
            {
                reason = "b64 가 data URI 형태인데 본문이 없습니다.";
                return false;
            }
            payload = payload[(comma + 1)..];
        }

        if (payload.Length > MaxImageBase64Chars)
        {
            reason = $"이미지가 너무 큽니다(base64 {payload.Length:N0}자 > 상한 {MaxImageBase64Chars:N0}자). size 를 줄이세요.";
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            reason = "b64 가 올바른 base64 가 아닙니다.";
            return false;
        }

        if (decoded.Length > MaxImageBytes)
        {
            reason = $"이미지가 너무 큽니다({decoded.Length:N0} 바이트 > 상한 {MaxImageBytes:N0} 바이트). size 를 줄이세요.";
            return false;
        }

        if (!IsPng(decoded))
        {
            reason = "응답이 PNG 가 아닙니다(PNG 시그니처 불일치).";
            return false;
        }

        bytes = decoded;
        reason = "";
        return true;
    }

    private static bool IsPng(byte[] data)
    {
        if (data.Length < PngMagic.Length) return false;
        for (var i = 0; i < PngMagic.Length; i++)
            if (data[i] != PngMagic[i]) return false;
        return true;
    }
}
