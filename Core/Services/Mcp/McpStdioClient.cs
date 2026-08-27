using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP stdio 客户端: 以子进程方式启动 MCP 服务器, 经 stdin/stdout 行分隔 JSON-RPC 2.0 通信。
/// 手写实现(零第三方依赖), Native AOT 兼容(JsonNode/JsonElement 由 STJ 内置转换器处理)。
/// 协议版本采用 "2025-06-18"(stdio 生态兼容面最大; 仅认旧版的服务器会按规范回告自身版本)。</summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly Process? _process;
    private readonly StreamReader _stdout;
    private readonly StreamWriter _stdin;
    private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> _pending = [];
    private long _nextId = 1;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposedCts = new();
    private volatile bool _disposed;

    public string ServerName { get; }

    /// <summary>服务器初始化时声明的 instructions(可选)。</summary>
    public string? Instructions { get; private set; }

    private McpStdioClient(Process process, string serverName)
    {
        _process = process;
        ServerName = serverName;
        _stdout = process.StandardOutput!;
        _stdin = process.StandardInput!;
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>启动服务器进程并完成 initialize 握手。</summary>
    public static async Task<McpStdioClient> StartAsync(McpServerDefinition def, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = def.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in def.Args)
        {
            psi.ArgumentList.Add(a);
        }

        // 环境变量与父进程合并(Process.Start 默认继承), 再叠加用户自定义项
        foreach (var kv in def.Env)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"进程启动返回空: {def.Command}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"启动 MCP 服务器失败({def.Id}): {ex.Message} — 请确认 {def.Command} 已安装且可用", ex);
        }

        // stderr 只读防阻塞(不参与协议)
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError!.ReadLineAsync(ct) is not null)
                {
                }
            }
            catch
            {
                // 进程退出导致的流关闭属正常情况
            }
        }, CancellationToken.None);

        var client = new McpStdioClient(proc, def.Name.Length > 0 ? def.Name : def.Id);
        try
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30)); // npx 首次下载可能较慢

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
        await NotifyAsync("notifications/initialized").ConfigureAwait(false);
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

    private async Task<JsonNode?> RequestAsync(string method, JsonObject? param, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long id;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending)
        {
            id = _nextId++;
            _pending[id] = tcs;
        }

        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (param is not null)
        {
            msg["params"] = param;
        }

        try
        {
            await WriteLineAsync(msg.ToJsonString(), ct).ConfigureAwait(false);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                throw new OperationCanceledException(ct);
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_pending)
            {
                _pending.Remove(id);
            }
        }
    }

    private async Task NotifyAsync(string method)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        await WriteLineAsync(msg.ToJsonString(), default).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>后台循环: 逐行读取 stdout, 按 id 派发响应给等待方。</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_disposedCts.IsCancellationRequested &&
                   await _stdout.ReadLineAsync(_disposedCts.Token) is { } line)
            {
                if (line.Length == 0 || !line.StartsWith('{'))
                {
                    continue; // 忽略 banner/非 JSON 行
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch
                {
                    continue;
                }

                if (node is not JsonObject msg ||
                    !msg.TryGetPropertyValue("id", out var idNode) || idNode is not JsonValue iv ||
                    !iv.TryGetValue<long>(out var rid))
                {
                    continue; // 通知或 server→client 请求(sampling 等): 不支持, 忽略
                }

                TaskCompletionSource<JsonNode?>? tcs;
                lock (_pending)
                {
                    _pending.Remove(rid, out tcs);
                }

                if (tcs is null)
                {
                    continue;
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
        }
    }

    private void FailAllPending(string reason)
    {
        lock (_pending)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }

            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposedCts.Cancel();
        FailAllPending("客户端已释放");

        try
        {
            _stdin.Close(); // 关闭 stdin 让服务器自行退出
        }
        catch
        {
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.WaitForExit(3000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            _process.Dispose();
        }

        _writeLock.Dispose();
        _disposedCts.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>MCP 工具描述符(tools/list 结果)。</summary>
public class McpToolDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonElement InputSchema { get; set; }
}
