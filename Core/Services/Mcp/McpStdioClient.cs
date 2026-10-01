using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Mcp;

/// <summary>MCP stdio 客户端: 以子进程方式启动 MCP 服务器, 经 stdin/stdout 行分隔 JSON-RPC 2.0 通信。
/// 请求/响应配对与工具调用逻辑在 McpClientBase, 本类只负责进程与行协议传输。</summary>
public sealed class McpStdioClient : McpClientBase
{
    private readonly Process? _process;
    private readonly StreamReader _stdout;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposedCts = new();

    private McpStdioClient(Process process, string serverName) : base(serverName)
    {
        _process = process;
        _stdout = process.StandardOutput!;
        _stdin = process.StandardInput!;
        // 读循环必须早于握手启动: initialize 的响应只能靠它派发。
        // 它的异常在循环内部自行捕获(见 ReadLoopAsync), 这里的 Task 未观察是有意为之。
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

    protected override async Task TransmitAsync(JsonObject msg, CancellationToken ct)
        => await WriteLineAsync(msg.ToJsonString(), ct).ConfigureAwait(false);

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
            while (!Disposed &&
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
                catch (Exception ex)
                {
                    // 以 '{' 开头却解析失败: 记 Debug 以便发现服务器的输出格式异常, 但不能因此中断读循环
                    Log.Debug("Mcp", $"MCP 收到无法解析的行({ServerName}): {ex.Message}");
                    continue;
                }

                if (node is JsonObject msg)
                {
                    DispatchMessage(msg);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // DisposeAsync 取消 _disposedCts 后的正常退出路径
        }
        catch (Exception ex)
        {
            // 进程被杀 / 流被关闭时的 IO 异常属预期; 但完全静默会让"读循环早就死了、
            // 之后所有请求都只能等超时"这类问题无从排查, 因此留一条 Debug 痕迹。
            Log.Debug("Mcp", $"MCP stdio 读循环结束({ServerName}): {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            FailAllPending("MCP 服务器连接已断开");
        }
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

        try
        {
            _stdin.Close(); // 关闭 stdin 让服务器自行退出
        }
        catch
        {
        }

        if (_process is not null)
        {
            // 退出等待改为异步: WaitForExit(3000) 会在 DisposeAsync 的调用线程上同步阻塞最多 3 秒
            // (调用方可能是 UI 线程), 与异步释放的语义相悖。
            try
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时未退出: 强杀整棵进程树, 再给一次有界的等待让管道真正收尾
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Debug("Mcp", $"强制结束 MCP 服务器进程失败({ServerName}): {ex.Message}");
                }

                try
                {
                    using var killWaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _process.WaitForExitAsync(killWaitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Debug("Mcp", $"等待 MCP 服务器进程退出失败({ServerName}): {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Mcp", $"等待 MCP 服务器进程退出异常({ServerName}): {ex.Message}");
            }

            try
            {
                _process.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("Mcp", $"释放 MCP 服务器进程句柄失败({ServerName}): {ex.Message}");
            }
        }

        // 刻意不释放 _writeLock(SemaphoreSlim) 与 _disposedCts:
        // DisposeAsync 可能在仍有并发 WriteLineAsync 持有 _writeLock、或读循环仍持有
        // _disposedCts.Token 时被调用, 此时 Dispose 会让那些在途操作抛 ObjectDisposedException。
        // 本类从未取过 SemaphoreSlim.AvailableWaitHandle(不需要), 故不释放是安全的;
        // 这两个对象随实例一并被 GC 回收, 生命周期与客户端本身相同。
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
