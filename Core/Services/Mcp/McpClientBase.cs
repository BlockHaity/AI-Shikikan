using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP 客户端公共基类: JSON-RPC 2.0 请求/响应配对、initialize 握手、工具枚举与结果文本化。
/// 传输层(stdio 行协议 / Streamable HTTP / SSE)由子类实现 TransmitAsync, 收到消息后回调 DispatchMessage。
/// 手写实现(零第三方依赖), Native AOT 兼容(JsonNode/JsonElement 由 STJ 内置转换器处理)。</summary>
public abstract class McpClientBase : IAsyncDisposable
{
    /// <summary>握手时上报的 MCP 协议版本。
    /// 刻意放在客户端而非配置文件: 客户端并不与服务器协商版本(initialize 用固定值),
    /// 做成用户可配置项只会多一个"改坏了就连不上"的旋钮。
    /// 用 static readonly 而非 const, 避免协议升级时改动散落到编译期常量引用上。</summary>
    public static readonly string ProtocolVersion = "2025-06-18";

    /// <summary>工具调用等长耗时请求的默认超时(秒)。</summary>
    protected static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(300);

    /// <summary>握手与枚举类请求的默认超时(秒)。首次 npx 下载可能较慢, 留足余量。</summary>
    protected static readonly TimeSpan DefaultSetupTimeout = TimeSpan.FromSeconds(30);

    protected readonly Dictionary<string, TaskCompletionSource<JsonNode?>> Pending = [];
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

    /// <summary>发送请求并等待响应(使用 <see cref="DefaultSetupTimeout"/> 默认超时)。</summary>
    protected Task<JsonNode?> RequestAsync(string method, JsonObject? param, CancellationToken ct)
        => RequestAsync(method, param, ct, DefaultSetupTimeout);

    /// <summary>发送请求并等待响应; 超时(服务器不响应)或外部取消都会终止等待并抛出异常。</summary>
    protected async Task<JsonNode?> RequestAsync(string method, JsonObject? param,
        CancellationToken ct, TimeSpan timeout)
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
            // 键必须与 DispatchMessage 侧用同一个归一化函数, 否则字符串 id 回显就配不上对
            Pending[NormalizeRpcId(msg["id"])!] = tcs;
        }

        // 超时与外部取消合并到同一 token: 既能兜住服务器不响应, 又能响应用户停止;
        // 且 Delay 到点自然完成并释放 CTS 注册, 不会在 token 池里堆积回调。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        try
        {
            await TransmitAsync(msg, token).ConfigureAwait(false);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, token)).ConfigureAwait(false);
            if (completed != tcs.Task && !tcs.Task.IsCompleted)
            {
                // 外部取消优先按取消语义抛出, 只有真正卡死才报超时
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                Log.Warn("Mcp", $"MCP 请求超时({timeout.TotalSeconds:0}s): {ServerName} {method}");
                throw new TimeoutException(
                    $"MCP 服务器 {ServerName} 的 {method} 请求超时({timeout.TotalSeconds:0}s)");
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (Pending)
            {
                Pending.Remove(NormalizeRpcId(msg["id"])!);
            }

            // 超时/取消时把 tcs 也终结掉, 避免任何潜在等待者永久挂起
            tcs.TrySetCanceled();
        }
    }

    /// <summary>JSON-RPC 2.0 的 id 允许是数字或字符串, 且部分实现会用与请求不同的 JSON 类型回显
    /// (请求发数字、响应回字符串)。因此 Pending 的键一律用本函数归一化为字符串, 两侧共用同一个实现。</summary>
    protected static string? NormalizeRpcId(JsonNode? idNode)
    {
        if (idNode is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<long>(out var num))
        {
            return num.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (v.TryGetValue<string>(out var s) && s.Length > 0)
        {
            return s;
        }

        // 兜底: 少数实现会把 id 写成别的标量类型, 用其字面文本当键至少不会漏配对
        var raw = v.ToString();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>传输层收到消息时调用: 按 id 配对响应/错误给等待方。
    /// 通知(无 id)与 server→client 请求(sampling / roots / elicitation)不参与配对:
    /// 本客户端未声明这些能力, 收到只能记日志并放弃, 服务器侧会等到它自己的超时。</summary>
    protected void DispatchMessage(JsonObject msg)
    {
        msg.TryGetPropertyValue("id", out var idNode);
        var rid = NormalizeRpcId(idNode);
        if (rid is null)
        {
            // 通知: 当前不消费任何 notifications/*, 仅 Debug 记录以免静默丢消息无从排查
            if (msg.TryGetPropertyValue("method", out var nNode) && nNode is JsonValue nv &&
                nv.TryGetValue<string>(out var name))
            {
                Log.Debug("Mcp", $"MCP 通知(未处理): {ServerName} {name}");
            }

            return;
        }

        TaskCompletionSource<JsonNode?>? tcs;
        lock (Pending)
        {
            Pending.Remove(rid, out tcs);
        }

        if (tcs is null)
        {
            // 无对应等待者: 要么是对已超时/已取消请求的迟到响应(正常丢弃), 要么是 server→client 请求。
            // 后者(如 sampling/createMessage、roots/list、elicitation)若静默忽略, 依赖 roots 的服务器会
            // 永久挂起到它自己的超时, 且界面上毫无线索 —— 记 Warn 让排障可见是诚实的降级做法。
            if (msg.TryGetPropertyValue("method", out var mNode) && mNode is JsonValue mv &&
                mv.TryGetValue<string>(out var method))
            {
                Log.Warn("Mcp",
                    $"不支持的 server→client 请求, 已忽略(服务器侧将超时): {ServerName} {method} (id={rid})");
            }

            return;
        }

        if (msg.TryGetPropertyValue("result", out var ok))
        {
            // 用 TrySet*: 请求方可能已在超时/取消路径里终结 TCS, 晚到的响应直接丢弃
            tcs.TrySetResult(ok);
        }
        else if (msg.TryGetPropertyValue("error", out var errNode) &&
                 errNode is JsonObject errObj &&
                 errObj.TryGetPropertyValue("message", out var em) &&
                 em is JsonValue emv)
        {
            tcs.TrySetException(new InvalidOperationException($"MCP 错误: {emv.GetValue<string>()}"));
        }
        else
        {
            tcs.TrySetResult(null);
        }
    }

    /// <summary>请求是否仍在等待响应(供流式传输判断何时可停止读取)。
    /// 参数须为 <see cref="NormalizeRpcId"/> 归一化后的键。</summary>
    protected bool IsPending(string id)
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

    /// <summary>initialize 握手 + initialized 通知; 超时由 <see cref="DefaultSetupTimeout"/> 统一约束。</summary>
    protected async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = AppInfo.Name,
                ["version"] = AppInfo.Version
            }
        }, ct, DefaultSetupTimeout).ConfigureAwait(false);

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

            // nextCursor 缺失 / null / 空串都必须视为"没有下一页": 若把空串当有效游标带上,
            // 服务器会反复回同一页, 每轮各耗一次超时 → 分页循环永不退出。
            cursor = root.TryGetPropertyValue("nextCursor", out var nc) && IsUsableCursor(nc)
                ? nc
                : null;
            if (cursor is null)
            {
                break;
            }
        }

        return tools;
    }

    /// <summary>分页游标是否可用: 必须是 JSON 字符串且非空白(MCP 规定 nextCursor 为字符串)。</summary>
    private static bool IsUsableCursor(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return false;
        }

        var s = v.TryGetValue<string>(out var str) ? str : v.ToString();
        return !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>调用工具并把 content 块文本化(文本原样; 图片/链接以占位标注);
    /// 使用 <see cref="DefaultCallTimeout"/> 长超时, 避免卡死的服务器拖垮整个回合。</summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(
        string toolName, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments
        }, ct, DefaultCallTimeout).ConfigureAwait(false);

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
