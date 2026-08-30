using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

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
                catch
                {
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
        }
        catch
        {
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
