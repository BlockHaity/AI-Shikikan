using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP 客户端公共基类: JSON-RPC 2.0 请求/响应配对、initialize 握手、工具枚举与结果文本化。
/// 传输层(stdio 行协议 / Streamable HTTP / SSE)由子类实现 TransmitAsync, 收到消息后回调 DispatchMessage。
/// 手写实现(零第三方依赖), Native AOT 兼容(JsonNode/JsonElement 由 STJ 内置转换器处理)。</summary>
public abstract class McpClientBase : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    protected readonly Dictionary<long, TaskCompletionSource<JsonNode?>> Pending = [];
    private long _nextId;
    private volatile bool _disposed;

    public string ServerName { get; }

    /// <summary>服务器初始化时声明的 instructions(可选)。</summary>
    public string? Instructions { get; protected set; }

    protected bool Disposed => _disposed;

    protected McpClientBase(string serverName) => ServerName = serverName;

    protected void SetDisposed() => _disposed = true;

    /// <summary>发送一条 JSON-RPC 消息(请求或通知); 请求的响应由传输层接收后经 DispatchMessage 派发。</summary>
    protected abstract Task TransmitAsync(JsonObject msg, CancellationToken ct);

    protected async Task<JsonNode?> RequestAsync(string method, JsonObject? param, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long id;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (param is not null)
        {
            msg["params"] = param;
        }

        lock (Pending)
        {
            id = ++_nextId;
            msg["id"] = id;
            Pending[id] = tcs;
        }

        try
        {
            await TransmitAsync(msg, ct).ConfigureAwait(false);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                throw new OperationCanceledException(ct);
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (Pending)
            {
                Pending.Remove(id);
            }
        }
    }

    /// <summary>传输层收到消息时调用: 按 id 配对响应/错误给等待方; 通知与 server→client 请求(sampling 等)忽略。</summary>
    protected void DispatchMessage(JsonObject msg)
    {
        if (!msg.TryGetPropertyValue("id", out var idNode) || idNode is not JsonValue iv ||
            !iv.TryGetValue<long>(out var rid))
        {
            return;
        }

        TaskCompletionSource<JsonNode?>? tcs;
        lock (Pending)
        {
            Pending.Remove(rid, out tcs);
        }

        if (tcs is null)
        {
            return;
        }

        if (msg.TryGetPropertyValue("result", out var ok))
        {
            tcs.SetResult(ok);
        }
        else if (msg.TryGetPropertyValue("error", out var errNode) &&
                 errNode is JsonObject errObj &&
                 errObj.TryGetPropertyValue("message", out var em) &&
                 em is JsonValue emv)
        {
            tcs.SetException(new InvalidOperationException($"MCP 错误: {emv.GetValue<string>()}"));
        }
        else
        {
            tcs.SetResult(null);
        }
    }

    /// <summary>请求是否仍在等待响应(供流式传输判断何时可停止读取)。</summary>
    protected bool IsPending(long id)
    {
        lock (Pending)
        {
            return Pending.ContainsKey(id);
        }
    }

    protected void FailAllPending(string reason)
    {
        lock (Pending)
        {
            foreach (var tcs in Pending.Values)
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }

            Pending.Clear();
        }
    }

    protected async Task InitializeAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30)); // npx 首次下载/远程服务可能较慢

        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = AppInfo.Name,
                ["version"] = AppInfo.Version
            }
        }, timeoutCts.Token).ConfigureAwait(false);

        if (result is JsonObject obj &&
            obj.TryGetPropertyValue("instructions", out var ins) && ins is JsonValue v &&
            v.TryGetValue<string>(out var s))
        {
            Instructions = s;
        }

        // 已初始化通知: 无 id、无响应
        await TransmitAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>枚举服务器工具列表(自动处理分页)。</summary>
    public async Task<List<McpToolDescriptor>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolDescriptor>();
        JsonNode? cursor = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var param = new JsonObject();
            if (cursor is not null)
            {
                param["cursor"] = cursor.DeepClone();
            }

            var result = await RequestAsync("tools/list", param, ct).ConfigureAwait(false);
            if (result is not JsonObject root ||
                !root.TryGetPropertyValue("tools", out var arrNode) ||
                arrNode is not JsonArray arr)
            {
                break;
            }

            foreach (var item in arr.OfType<JsonObject>())
            {
                var d = new McpToolDescriptor
                {
                    Name = item.TryGetPropertyValue("name", out var n) && n is JsonValue nv
                        ? nv.GetValue<string>() : string.Empty,
                    Description = item.TryGetPropertyValue("description", out var de) && de is JsonValue dv
                        ? dv.GetValue<string>() : string.Empty
                };
                if (item.TryGetPropertyValue("inputSchema", out var schema) && schema is not null)
                {
                    d.InputSchema = JsonSerializer.SerializeToElement(schema);
                }

                if (!string.IsNullOrEmpty(d.Name))
                {
                    tools.Add(d);
                }
            }

            cursor = root.TryGetPropertyValue("nextCursor", out var nc) ? nc : null;
            if (cursor is null)
            {
                break;
            }
        }

        return tools;
    }

    /// <summary>调用工具并把 content 块文本化(文本原样; 图片/链接以占位标注)。</summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(
        string toolName, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        }, ct).ConfigureAwait(false);

        if (result is not JsonObject root)
        {
            return ("工具返回了空结果", false);
        }

        var sb = new StringBuilder();
        if (root.TryGetPropertyValue("content", out var contentNode) &&
            contentNode is JsonArray contents)
        {
            foreach (var block in contents.OfType<JsonObject>())
            {
                if (!block.TryGetPropertyValue("type", out var tNode) || tNode is not JsonValue tv)
                {
                    continue;
                }

                switch (tv.GetValue<string>())
                {
                    case "text" when block.TryGetPropertyValue("text", out var tx) && tx is JsonValue txv:
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append(txv.GetValue<string>());
                        break;
                    case "image":
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append("[图片输出已省略]");
                        break;
                    case "resource_link" when block.TryGetPropertyValue("uri", out var uri) && uri is JsonValue uv:
                        if (sb.Length > 0) sb.AppendLine();
                        sb.Append($"[链接] {uv.GetValue<string>()}");
                        break;
                }
            }
        }

        if (sb.Length == 0)
        {
            sb.Append("(无内容)");
        }

        var isError = root.TryGetPropertyValue("isError", out var err) &&
            err is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
        return (sb.ToString(), isError);
    }

    public abstract ValueTask DisposeAsync();
}

/// <summary>MCP 工具描述符(tools/list 结果)。</summary>
public class McpToolDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonElement InputSchema { get; set; }
}
