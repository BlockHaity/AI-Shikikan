using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/*
 * 远程 Worker 的 WebSocket 服务端: 监听 TcpListener, 每条连接升级成 WebSocket 后,
 * 把同一条连接的两端当作 WorkerServer 的 input/output 跑完整的 NDJSON 协议循环。
 *
 * ── 为什么一条连接 = 一个 Worker 实例 ──
 *   stdio 模式下「一个子进程 = 一个 (工作目录 × 会话)」; 远程模式没有进程边界可用,
 *   连接本身就是实例边界。断开即收尾: WorkerServer.RunAsync 读到 EOF/close 后退出,
 *   在飞的 tools/call 已随 ct 取消(取消最终落到子代理的进程树)。
 *
 * ── 为什么每条连接 Task.Run 而不是在 accept 循环里 await ──
 *   一次工具调用合法跑 30 分钟(子代理上限), 在 accept 循环里 await 等于这段时间内
 *   再也接不上下一条连接 —— 症状是"第一个会话能用, 第二个会话永远连不上"。
 *
 * ── 为什么任何连接级异常都不能冒到 RunAsync ──
 *   服务端是长驻进程: 一条坏连接(半截握手 / 宿主编排失败 / 协议帧损坏)只该关掉它自己。
 *   冒到 accept 循环会让整个服务退出, 其余会话一起断。
 */

/// <summary>
/// 远程 Worker 的 WebSocket 服务端: 绑定监听、升级握手、为每条连接装配并运行一个
/// <see cref="WorkerServer"/>。
/// </summary>
/// <remarks>
/// <para><b>配置错误必须在 <see cref="Start"/> 里抛出</b>: Worker 入口会把启动失败映射成退出码 2
/// (参数错)。若把校验推迟到 <see cref="RunAsync"/> 里, 症状就变成"进程起来了、握手全部失败",
/// 排障方向完全不同。</para>
/// <para><b>关闭顺序</b>: <see cref="RunAsync"/> 返回只代表"不再接受新连接"; 已建立的连接由各自的
/// 取消令牌收尾。调用方应在 RunAsync 之后调用 <see cref="DisposeAsync"/>, 它会停监听并给连接
/// 一个很短的收尾预算。</para>
/// </remarks>
public sealed class WorkerWebSocketServer : IAsyncDisposable
{
    private const string LogCategory = "Worker";

    /// <summary>Listen 里没写端口时使用的默认端口(与 WorkerConfig 的默认值一致)。</summary>
    private const int DefaultPort = 8765;

    /// <summary>Dispose 时等待连接收尾的预算。超时就不再等 —— 进程正在退出, 不该被一条卡住的连接拖住。</summary>
    private static readonly TimeSpan ConnectionDrainBudget = TimeSpan.FromSeconds(2);

    private readonly TcpListener _listener;
    private readonly Func<WorkerHostOptions, IWorkerToolHost> _hostFactory;
    private readonly string _root;
    private readonly string _path;
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>在处理的连接任务。只用于 Dispose 时有界等待, 不参与任何路由。</summary>
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    private int _runStarted;
    private int _disposed;

    private WorkerWebSocketServer(
        TcpListener listener,
        Func<WorkerHostOptions, IWorkerToolHost> hostFactory,
        string root,
        string path,
        string token,
        IPEndPoint endPoint)
    {
        _listener = listener;
        _hostFactory = hostFactory;
        _root = root;
        _path = path;
        _token = token;
        EndPoint = endPoint;
    }

    /// <summary>实际监听的地址(端口为 0 时这里给的是系统分配的端口)。</summary>
    public System.Net.IPEndPoint EndPoint { get; }

    /// <summary>
    /// 校验配置并绑定监听。任何配置/绑定错误都抛异常(文案面向使用者, 供入口转成退出码 2)。
    /// </summary>
    /// <param name="config">worker.toml 配置(需已 Normalize; 这里也会容忍未规范化的 Listen/Path)。</param>
    /// <param name="hostFactory">按连接身份构造工具宿主; 与 stdio 入口的装配逻辑共用同一份。</param>
    /// <param name="defaultWorkDir">未配置 <see cref="WorkerConfig.Root"/> 时使用的项目根目录。</param>
    public static WorkerWebSocketServer Start(
        WorkerConfig config,
        Func<WorkerHostOptions, IWorkerToolHost> hostFactory,
        string defaultWorkDir)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(hostFactory);

        // token 是远程模式唯一的鉴权手段; 空 token 等于把整台机器的项目根目录暴露给任何能连上端口的人。
        var token = config.Token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("远程 Worker 必须配置 token");
        }

        var root = config.Root ?? defaultWorkDir;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("远程 Worker 必须指定根目录: config.Root 与默认工作目录都为空");
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"根目录不是合法路径(\"{root}\"): {ex.Message}", ex);
        }

        if (!Directory.Exists(fullRoot))
        {
            throw new ArgumentException($"根目录不存在或不是目录: {fullRoot}");
        }

        var path = NormalizeRequestPath(config.Path);
        var (address, port) = ResolveListenAddress(config.Listen);

        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new IOException($"无法监听 {address}:{port}: {ex.Message}(端口可能已被占用)", ex);
        }

        var endPoint = listener.LocalEndpoint as IPEndPoint ?? new IPEndPoint(address, port);

        Log.Info(LogCategory,
            $"远程 Worker WebSocket 服务已启动: 监听 {endPoint}, 路径 {path}, 根目录 {fullRoot}");

        return new WorkerWebSocketServer(listener, hostFactory, fullRoot, path, token, endPoint);
    }

    /// <summary>接受连接直到 <paramref name="ct"/> 取消或 <see cref="DisposeAsync"/> 被调用。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("WorkerWebSocketServer.RunAsync 只能调用一次");
        }

        // 两路取消汇合: 外部 ct(进程收尾)与 DisposeAsync(调用方主动关闭)。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);

        Log.Info(LogCategory, $"远程 Worker 开始接受连接: 监听 {EndPoint}, 路径 {_path}, 根目录 {_root}");

        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync 已 Stop 监听
                break;
            }
            catch (Exception ex)
            {
                if (linked.IsCancellationRequested)
                {
                    break;
                }

                // 单次 accept 失败(例如瞬时 SocketException)不该让整个服务退出。
                Log.Warn(LogCategory, ex, "接受 WebSocket 连接失败, 服务继续运行");
                continue;
            }

            // 连接令牌刻意用 _stop 而不是 linked: linked 会在 RunAsync 返回时被 Dispose,
            // 而连接任务可能还没真正跑起来, 拿着已释放的令牌去建 linked 源会抛 ObjectDisposedException。
            // _stop 永不释放(见 DisposeAsync 的说明), 因此它的令牌在整个连接生命周期内都有效。
            var task = Task.Run(() => HandleClientAsync(client, _stop.Token), CancellationToken.None);
            _connections[task] = 0;

            // 连接结束时从表里摘除; ExecuteSynchronously 让已完成的任务就地清理, 不留冗余条目。
            _ = task.ContinueWith(
                static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
                _connections,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // 不论 RunAsync 因外部 ct 还是 DisposeAsync 退出, 已建立的连接都要一起收尾。
        CancelStop();

        Log.Info(LogCategory, "远程 Worker 已停止接受连接");
    }

    /// <summary>取消服务级令牌(幂等), 让所有连接与 accept 循环一起收尾。</summary>
    private void CancelStop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 本类刻意不释放 _stop, 这里只是并发收尾时的兜底
        }
    }

    /// <summary>停监听、取消所有连接, 并有界等待它们收尾。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelStop();

        try
        {
            _listener.Stop();
        }
        catch
        {
            // Stop 在已停止的监听上会抛, 忽略即可: 目标状态(不再接受连接)已经达成
        }

        if (!_connections.IsEmpty)
        {
            Task all;
            try
            {
                all = Task.WhenAll(_connections.Keys.ToArray());
            }
            catch (ArgumentException)
            {
                // 快照瞬间恰好全空
                all = Task.CompletedTask;
            }

            await Task.WhenAny(all, Task.Delay(ConnectionDrainBudget, CancellationToken.None)).ConfigureAwait(false);
        }

        // 刻意不 Dispose _stop: RunAsync 可能正拿它的 Token 建 linked 源, 此时释放会让那一侧抛
        // ObjectDisposedException。它没有取过 WaitHandle, 交给 GC 回收是安全的。
        Log.Info(LogCategory, $"远程 Worker WebSocket 服务已关闭: 监听 {EndPoint}");
    }

    /// <summary>处理一条连接: 升级 → 装配宿主 → 跑协议循环 → 收尾。任何异常只关这一条连接。</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = "(未知对端)";
        ServerWebSocketChannel? channel = null;
        WebSocketFrameStream? frameStream = null;

        try
        {
            // 对端地址只用于日志; 取不到不该影响连接处理, 所以放在 try 里且失败与否都能继续。
            remote = client.Client.RemoteEndPoint?.ToString() ?? remote;

            // 小帧 + 交互式工具调用: 关掉 Nagle, 否则小响应会被攒到 40ms 才发出去。
            client.NoDelay = true;

            Log.Info(LogCategory, $"收到 WebSocket 连接: 对端={remote}");

            var stream = client.GetStream();
            if (!await ServerWebSocketChannel.TryUpgradeAsync(stream, _path, _token, ct).ConfigureAwait(false))
            {
                Log.Warn(LogCategory, $"WebSocket 握手未通过, 关闭连接: 对端={remote}");
                return;
            }

            channel = new ServerWebSocketChannel(stream);
            frameStream = new WebSocketFrameStream(channel);

            // 远程模式的启动身份: 会话 Id 为空(真实会话由 hello 下发), 工作目录固定为服务端根目录,
            // RemoteRoot 让宿主知道"这是远程连接, hello 的 WorkDir 必须落在根目录内"。
            var options = new WorkerHostOptions
            {
                SessionId = string.Empty,
                WorkDir = _root,
                WorkspaceRoot = _root,
                ParentPid = 0,
                WorkerKey = WorkerProtocol.MakeWorkerKey(_root, string.Empty),
                RemoteRoot = _root,
            };

            var host = _hostFactory(options);

            // 协议层不认识宿主的具体类型; 实时输出现象通过这个可选接口接上(与 stdio 入口的接线等价)。
            var server = new WorkerServer(host);
            if (host is IWorkerToolHostOutput output)
            {
                output.OutputSink = server.EmitToolOutput;
            }

            Log.Info(LogCategory,
                $"WebSocket 连接已升级: 对端={remote}, WorkerKey={options.WorkerKey}, 开始 Worker 协议循环");

            // 同一条连接既是输入也是输出: 一条消息就是一段字节, NDJSON 的行缓冲由协议层自己维持。
            await server.RunAsync(frameStream, frameStream, ct).ConfigureAwait(false);

            Log.Info(LogCategory, $"WebSocket 连接协议循环结束: 对端={remote}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info(LogCategory, $"远程 Worker 服务停止, 连接随之关闭: 对端={remote}");
        }
        catch (Exception ex)
        {
            // 宿主构造失败 / 协议帧损坏 / 底层 IO 异常: 都只影响这一条连接。
            Log.Warn(LogCategory, ex, $"WebSocket 连接处理失败, 已关闭该连接: 对端={remote}");
        }
        finally
        {
            // 先关流(尽力发 close 帧), 再关 socket; 每一步都单独兜住, 一步失败不该影响其它连接。
            try
            {
                frameStream?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(LogCategory, $"关闭 WebSocket 流失败: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                channel?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(LogCategory, $"关闭 WebSocket 通道失败: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                client.Dispose();
            }
            catch
            {
                // socket 关闭失败无需处理
            }
        }
    }

    /// <summary>解析 <c>host:port</c>。语义与 WorkerConfig.ResolveClientUrl 的拆法保持一致。</summary>
    private static (IPAddress Address, int Port) ResolveListenAddress(string? listen)
    {
        var value = string.IsNullOrWhiteSpace(listen) ? $"127.0.0.1:{DefaultPort}" : listen.Trim();
        var separator = value.LastIndexOf(':');
        if (separator < 0)
        {
            throw new ArgumentException(
                $"监听地址 \"{value}\" 缺少端口: 期望 host:port(默认端口 {DefaultPort})");
        }

        var host = value[..separator].Trim();
        var portText = value[(separator + 1)..].Trim();

        var port = DefaultPort;
        if (portText.Length > 0
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 0 or > 65535))
        {
            throw new ArgumentException($"监听地址 \"{value}\" 的端口不合法: 期望 0-65535");
        }

        IPAddress address;
        if (host is "0.0.0.0" or "*")
        {
            address = IPAddress.Any;
        }
        else if (host.Length == 0)
        {
            address = IPAddress.Loopback;
        }
        else
        {
            try
            {
                address = IPAddress.Parse(host);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"监听地址 \"{value}\" 的主机部分不是合法 IP: {ex.Message}", ex);
            }
        }

        return (address, port);
    }

    /// <summary>期望路径归一成以 <c>/</c> 开头; 空白回退到 <c>/worker</c>。</summary>
    private static string NormalizeRequestPath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/worker" : path.Trim();
        return value.StartsWith('/') ? value : "/" + value;
    }
}
