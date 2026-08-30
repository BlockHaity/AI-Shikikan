using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP HTTP 客户端: 支持两种传输 —
/// Streamable HTTP(POST JSON-RPC 到单一端点, 响应为 JSON 或 SSE 流, 以 Mcp-Session-Id 维持会话)与
/// 传统 HTTP+SSE(GET 事件流接收响应, 首个 endpoint 事件给出 POST 提交地址)。
/// 请求/响应配对与工具调用逻辑在 McpClientBase, 本类只负责 HTTP 传输。</summary>
public sealed class McpHttpClient : McpClientBase
{
    private enum Mode
    {
        StreamableHttp,
        LegacySse
    }

    private readonly McpServerDefinition _def;
    private readonly Mode _mode;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _disposedCts = new();
    private readonly object _endpointLock = new();
    private readonly TaskCompletionSource<string> _endpointTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _sessionId;   // Streamable HTTP: Mcp-Session-Id
    private string? _postUrl;     // SSE: endpoint 事件给出的提交地址
    private HttpResponseMessage? _sseResponse;

    private McpHttpClient(McpServerDefinition def, Mode mode) : base(def.Name.Length > 0 ? def.Name : def.Id)
    {
        _def = def;
        _mode = mode;
        // SSE 长流禁用整体超时, 按请求单独控制
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>传输方式是否走 HTTP 客户端(非 stdio)。</summary>
    public static bool IsHttpTransport(string? transport)
    {
        var t = (transport ?? "stdio").Trim().ToLowerInvariant().Replace('_', '-');
        return t is "http" or "streamable-http" or "sse";
    }

    /// <summary>建立连接并完成 initialize 握手。</summary>
    public static async Task<McpHttpClient> StartAsync(McpServerDefinition def, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(def.Url))
        {
            throw new InvalidOperationException($"MCP 服务器 {def.Id} 为 {def.Transport} 传输, 需配置 url");
        }

        var mode = (def.Transport ?? "stdio").Trim().ToLowerInvariant() == "sse"
            ? Mode.LegacySse
            : Mode.StreamableHttp;

        var client = new McpHttpClient(def, mode);
        try
        {
            if (mode == Mode.LegacySse)
            {
                await client.OpenSseStreamAsync(ct).ConfigureAwait(false);
            }

            await client.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    protected override async Task TransmitAsync(JsonObject msg, CancellationToken ct)
    {
        if (_mode == Mode.LegacySse)
        {
            await PostToEndpointAsync(msg, ct).ConfigureAwait(false);
        }
        else
        {
            await PostStreamableAsync(msg, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Streamable HTTP: POST 单一端点; 响应可为 202(通知回执)/JSON 单条/SSE 流。</summary>
    private async Task PostStreamableAsync(JsonObject msg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _def.Url);
        req.Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrEmpty(_sessionId))
        {
            req.Headers.Add("Mcp-Session-Id", _sessionId);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"MCP HTTP 请求失败: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        var session = GetHeader(resp, "Mcp-Session-Id");
        if (!string.IsNullOrEmpty(session))
        {
            _sessionId = session;
        }

        if (resp.StatusCode == HttpStatusCode.Accepted)
        {
            return; // 通知回执, 无响应体
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/json";
        if (contentType == "text/event-stream")
        {
            // 流式响应: 逐事件读取并派发, 本请求的响应到达后即可停止
            var expectId = GetMsgId(msg);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var data = new List<string>();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    DispatchSseData(data);
                    if (expectId is long id && !IsPending(id))
                    {
                        break; // 响应已到达
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Add(line[5..].Trim());
                }
            }
        }
        else
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(body);
            }
            catch
            {
            }

            if (node is JsonObject obj)
            {
                DispatchMessage(obj);
            }
        }
    }

    /// <summary>SSE 传输: POST 提交到 endpoint 地址, 响应经 GET 事件流异步到达。</summary>
    private async Task PostToEndpointAsync(JsonObject msg, CancellationToken ct)
    {
        string? url;
        lock (_endpointLock)
        {
            url = _postUrl;
        }

        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException("MCP SSE 连接尚未就绪(未收到 endpoint 事件)");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"MCP SSE 提交失败: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
    }

    /// <summary>SSE 传输: GET 建立长事件流并等待 endpoint 事件给出提交地址。</summary>
    private async Task OpenSseStreamAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        var req = new HttpRequestMessage(HttpMethod.Get, _def.Url);
        req.Headers.Accept.ParseAdd("text/event-stream");
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            throw new InvalidOperationException(
                $"MCP SSE 连接失败: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        _sseResponse = resp;
        var stream = await resp.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
        _ = Task.Run(() => SseReadLoopAsync(new StreamReader(stream)));

        // 等待 endpoint 事件(服务器告知 POST 提交地址)
        await _endpointTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    /// <summary>后台循环: 逐行读取 SSE 事件流, 按 id 派发响应给等待方。</summary>
    private async Task SseReadLoopAsync(StreamReader reader)
    {
        var data = new List<string>();
        try
        {
            while (!Disposed && await reader.ReadLineAsync(_disposedCts.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    DispatchSseData(data);
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Add(line[5..].Trim());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            FailAllPending("MCP 服务器连接已断开");
            reader.Dispose();
        }
    }

    /// <summary>处理一条 SSE 事件的 data: JSON-RPC 消息则派发; 非 JSON 视为 endpoint 事件给出的提交地址。</summary>
    private void DispatchSseData(List<string> dataLines)
    {
        if (dataLines.Count == 0)
        {
            return;
        }

        var data = string.Join("\n", dataLines);
        dataLines.Clear();

        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(data);
        }
        catch
        {
        }

        if (node is JsonObject obj)
        {
            DispatchMessage(obj);
            return;
        }

        string url;
        try
        {
            url = data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? data
                : new Uri(new Uri(_def.Url), data).ToString();
        }
        catch
        {
            return;
        }

        lock (_endpointLock)
        {
            _postUrl ??= url;
        }

        _endpointTcs.TrySetResult(url);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Disposed)
        {
            return;
        }

        SetDisposed();
        _disposedCts.Cancel();
        FailAllPending("客户端已释放");

        // Streamable HTTP: 显式关闭会话(尽力而为)
        if (_mode == Mode.StreamableHttp && !string.IsNullOrEmpty(_sessionId))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, _def.Url);
                req.Headers.Add("Mcp-Session-Id", _sessionId);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        try
        {
            _sseResponse?.Dispose();
        }
        catch
        {
        }

        _http.Dispose();
        _disposedCts.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string? GetHeader(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static long? GetMsgId(JsonObject msg)
        => msg.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue iv && iv.TryGetValue<long>(out var id)
            ? id
            : null;
}
