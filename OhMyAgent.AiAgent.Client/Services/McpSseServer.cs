using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using OhMyAgent.AiAgent.Client.Models.Mcp;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// HttpListener 기반 SSE 서버. GET /sse 로 SSE 구독, POST /message 로 JSON-RPC 요청을 받는다.
/// 결과는 모든 SSE 구독자에게 push.
/// </summary>
public class McpSseServer : IMcpSseServer
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
    };

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _keepAliveLoop;

    public bool IsListening => _listener?.IsListening == true;
    public int Port { get; private set; }

    public Func<McpRequest, CancellationToken, Task<McpResponse>>? RequestHandler { get; set; }

    public Task StartAsync(int port, CancellationToken ct = default)
    {
        if (_listener != null && _listener.IsListening)
            return Task.CompletedTask;

        Port = port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _acceptLoop = Task.Run(AcceptLoopAsync);
        _keepAliveLoop = Task.Run(KeepAliveLoopAsync);

        Debug.WriteLine($"[McpSseServer] started on http://localhost:{port}/");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { /* ignore */ }

        // 모든 구독자 닫기
        foreach (var kv in _subscribers)
        {
            TryCloseSubscriber(kv.Value);
        }
        _subscribers.Clear();

        try
        {
            if (_listener != null && _listener.IsListening)
                _listener.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpSseServer] Stop failed: {ex.Message}");
        }

        try
        {
            _listener?.Close();
        }
        catch { /* ignore */ }

        _listener = null;

        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
            _acceptLoop = null;
        }

        if (_keepAliveLoop != null)
        {
            try { await _keepAliveLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
            _keepAliveLoop = null;
        }

        _cts?.Dispose();
        _cts = null;

        Debug.WriteLine("[McpSseServer] stopped");
    }

    public async Task BroadcastAsync(McpResponse response, CancellationToken ct = default)
    {
        var json = JsonConvert.SerializeObject(response, JsonSettings);
        var payload = $"event: message\ndata: {json}\n\n";

        await SendToAllAsync(payload, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // --- internals ----------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // listener stopped
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[McpSseServer] GetContext failed: {ex.Message}");
                continue;
            }

            // 라우팅: 비동기로 처리 (handler 가 막지 않도록 fire-and-forget)
            _ = Task.Run(() => DispatchAsync(context, token));
        }
    }

    private async Task DispatchAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod.ToUpperInvariant();

            // CORS preflight
            if (method == "OPTIONS")
            {
                ApplyCorsHeaders(context.Response);
                context.Response.StatusCode = 200;
                context.Response.Close();
                return;
            }

            if (method == "GET" && path.Equals("/sse", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseConnectionAsync(context, ct).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path.Equals("/message", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePostMessageAsync(context, ct).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpSseServer] Dispatch failed: {ex.Message}");
            try { context.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private async Task HandleSseConnectionAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        ApplyCorsHeaders(response);
        response.SendChunked = true;

        var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // endpoint 이벤트 최초 전송
        try
        {
            await writer.WriteAsync("event: endpoint\ndata: /message\n\n").ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpSseServer] initial endpoint write failed: {ex.Message}");
            try { response.Abort(); } catch { /* ignore */ }
            return;
        }

        var id = Guid.NewGuid();
        var subscriber = new Subscriber(id, response, writer, new SemaphoreSlim(1, 1));
        _subscribers[id] = subscriber;
        Debug.WriteLine($"[McpSseServer] subscriber {id} connected (count={_subscribers.Count})");

        // 연결 유지: ct 취소되거나 클라이언트 끊어질 때까지 대기
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 매우 긴 슬립으로 cooperative wait
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                // 연결 종료 감지 (writer 가 throw 하면 keep-alive 루프나 broadcast 에서 제거됨)
                if (!_subscribers.ContainsKey(id))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            TryCloseSubscriber(subscriber);
            Debug.WriteLine($"[McpSseServer] subscriber {id} disconnected (count={_subscribers.Count})");
        }
    }

    private async Task HandlePostMessageAsync(HttpListenerContext context, CancellationToken ct)
    {
        string body;
        var encoding = context.Request.ContentEncoding ?? Encoding.UTF8;
        using (var sr = new StreamReader(context.Request.InputStream, encoding))
        {
            body = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        // 즉시 202 응답
        ApplyCorsHeaders(context.Response);
        context.Response.StatusCode = 202;
        context.Response.ContentLength64 = 0;
        try { context.Response.Close(); } catch { /* ignore */ }

        McpRequest? request = null;
        object? requestId = null;
        try
        {
            request = JsonConvert.DeserializeObject<McpRequest>(body, JsonSettings);
            requestId = request?.Id;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpSseServer] parse failed: {ex.Message}");
            var parseErr = McpResponse.Fail(null, McpError.ParseError, "Parse error", ex.Message);
            await BroadcastAsync(parseErr, ct).ConfigureAwait(false);
            return;
        }

        if (request == null)
        {
            var invalidErr = McpResponse.Fail(null, McpError.InvalidRequest, "Empty request");
            await BroadcastAsync(invalidErr, ct).ConfigureAwait(false);
            return;
        }

        var handler = RequestHandler;
        if (handler == null)
        {
            var noHandler = McpResponse.Fail(requestId, McpError.InternalError, "No request handler registered");
            await BroadcastAsync(noHandler, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var resp = await handler(request, ct).ConfigureAwait(false);
            await BroadcastAsync(resp, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpSseServer] handler failed: {ex.Message}");
            var errResp = McpResponse.Fail(requestId, McpError.InternalError, "Handler error", ex.Message);
            try { await BroadcastAsync(errResp, ct).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task KeepAliveLoopAsync()
    {
        var token = _cts!.Token;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await SendToAllAsync(": keep-alive\n\n", token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[McpSseServer] KeepAlive failed: {ex.Message}");
        }
    }

    private async Task SendToAllAsync(string payload, CancellationToken ct)
    {
        if (_subscribers.IsEmpty) return;

        // snapshot
        var snapshot = _subscribers.ToArray();
        foreach (var kv in snapshot)
        {
            var sub = kv.Value;
            var lockAcquired = false;
            try
            {
                await sub.WriteLock.WaitAsync(ct).ConfigureAwait(false);
                lockAcquired = true;
                await sub.Writer.WriteAsync(payload).ConfigureAwait(false);
                await sub.Writer.FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 셧다운 - 후속 구독자에 보낼 필요 없음
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[McpSseServer] write to {kv.Key} failed: {ex.Message}");
                _subscribers.TryRemove(kv.Key, out _);
                TryCloseSubscriber(sub);
            }
            finally
            {
                if (lockAcquired)
                {
                    try { sub.WriteLock.Release(); } catch { /* ignore */ }
                }
            }
        }
    }

    private static void TryCloseSubscriber(Subscriber sub)
    {
        try { sub.Writer.Dispose(); } catch { /* ignore */ }
        try { sub.Response.OutputStream.Close(); } catch { /* ignore */ }
        try { sub.Response.Close(); } catch { /* ignore */ }
        try { sub.WriteLock.Dispose(); } catch { /* ignore */ }
    }

    private static void ApplyCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
    }

    private sealed record Subscriber(Guid Id, HttpListenerResponse Response, StreamWriter Writer, SemaphoreSlim WriteLock);
}
