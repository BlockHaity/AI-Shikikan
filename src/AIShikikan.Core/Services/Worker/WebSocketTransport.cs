/*
 * WebSocketTransport: 父进程侧的 Worker 传输实现 —— 连一个独立运行的远程 Worker(ClientWebSocket), 帧仍是 NDJSON。
 *
 * ── 与 PipeTransport 的关系 ──
 *   生命周期、超时预算、故障语义与 PipeTransport **逐字同构**(见 PipeTransport 的类注释), 唯一替换的
 *   是底层载体: 匿名管道 → WebSocket 文本消息。因此这里的取舍理由不再重复, 只写两者的差异:
 *
 *   ① 没有子进程: 也就没有 stderr 泵、没有 ParentPid 兜底、没有 Kill(entireProcessTree)。
 *      进程归谁所有是**远端**的部署问题; 本类只拥有"这条连接", 关闭动作止于 close 帧 + Abort。
 *   ② 写失败与读失败是同一条连接的两种表现: 管道写失败时对端可能还活着(我们只是关了 stdin),
 *      而 WebSocket 一旦写失败, 读侧通常也立刻结束 —— 但**仍按 PipeTransport 的口径处理**:
 *      写失败只报告故障(走 SignalFaultOnce), 不在这里抢着 Abort, 让读循环自己收尾, 避免两条路径
 *      各自宣布死讯(虽然 Faulted 有一次性闸门, 但收尾动作重复会让日志误导排查)。
 *   ③ Dispose 的等待对象从"进程退出"变成"读循环结束": 发 close 帧 → 有界等待对端回 close
 *      (读循环的 ReceiveAsync 收到 Close 后自行退出) → 窗口内没结束就直接 Abort。
 *      必须用 CloseOutputAsync 而不是 CloseAsync: 后者内部自带一次接收, 与仍在 ReceiveAsync 的
 *      读循环会撞成"并发接收"而抛 InvalidOperationException —— 那个异常与真正的故障毫无关系。
 *
 * ── 两个后台循环 ──
 *   读循环(收帧 + 分派)、心跳看门狗(HeartbeatTimeout 内没有任何帧 → 僵死)。管道版的 stderr 泵在
 *   WebSocket 下不存在(没有 stderr), 这是与 PipeTransport 最主要的循环数量差异。
 *
 * ── ⚠️ Faulted 至多一次、全入口覆盖 ──
 *   理由与 PipeTransport 类注释完全相同: WorkerProtocol.ToolCallTimeout 刻意无限, 漏掉任何一条
 *   故障入口(读循环 EOF / IO 异常 / 写失败 / 心跳僵死)都会让在飞调用永久挂起。
 */

using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;

namespace AIShikikan.Core.Services.Worker;

/// <summary>WebSocket 传输: 通过 <see cref="ClientWebSocket"/> 连到独立运行的远程 Worker,
/// 与 <see cref="PipeTransport"/> 共享同一套协议(NDJSON 帧 + JSON-RPC 2.0 子集)与同一套故障语义。
/// 只把 stdio 换成 WebSocket 文本消息, 不改变 <see cref="IToolTransport"/> 的任何对外行为。</summary>
/// <remarks>
/// <para><b>为什么帧仍是 NDJSON, 而不是"一条消息一帧"</b>: 远端 Worker 侧同样是
/// <c>WorkerFrameWriter</c> / <c>WorkerFrameReader</c> 的复用者(经 <see cref="WebSocketFrameStream"/>),
/// 帧边界由 '\n' 定义、消息边界只是字节容器 —— 两侧共用同一段编解码代码, 就不会出现"一边按行、
/// 一边按消息"的静默错位(那种错的症状是"帧偶尔被劈开", 且只在载荷跨消息时发生)。</para>
///
/// <para><b>连接与握手分离</b>: <see cref="StartAsync"/> 只建立连接(与 <see cref="PipeTransport.Start"/> 同一条契约,
/// 见 AGENTS.md「Worker 新增铁律」第 7 条), 握手由 <see cref="HandshakeAsync"/> 单独做。所以它返回后
/// <see cref="IsConnected"/> 仍为 false, 直到握手成功 —— 上层据此判断"这个句柄能不能立刻发请求"。</para>
/// </remarks>
public sealed class WebSocketTransport : IToolTransport
{
    /// <summary>日志 category(与 Worker 其余部分一致)。</summary>
    private const string Category = "Worker";

    private readonly ClientWebSocket _socket;

    /// <summary>WebSocket 连接通道(逐条消息收发)。所有权归本类, 但只用来兜底释放。</summary>
    private readonly IWebSocketChannel _channel;

    /// <summary>把消息字节重新连成流的适配层; 读循环与 <see cref="WorkerFrameWriter"/> 都只认它。</summary>
    private readonly Stream _stream;

    /// <summary>日志前缀(默认 <c>[WsWorker:{key}]</c>)。</summary>
    private readonly string _logPrefix;

    private readonly WorkerFrameWriter _writer;
    private readonly WorkerRpcCore _core;
    private readonly CancellationTokenSource _stopCts = new();

    /// <summary>读循环任务句柄。Dispose 需要"有界等待它结束", 否则无法判断对端是否真的回了 close。</summary>
    private readonly Task _readLoopTask;

    /// <summary>最后收到<b>任何</b>一帧的时刻(<see cref="Environment.TickCount64"/>, 单调)。心跳看门狗的判据。</summary>
    private long _lastFrameTick;

    /// <summary>握手完成且未断线。语义是"现在能不能立刻发请求", 故障后必须翻 false。</summary>
    private volatile bool _connected;

    private int _faultRaised;
    private int _disposed;
    private int _shutdownSent;

    private WebSocketTransport(
        ClientWebSocket socket,
        IWebSocketChannel channel,
        Stream stream,
        string logPrefix,
        string workerKey)
    {
        _socket = socket;
        _channel = channel;
        _stream = stream;
        _logPrefix = logPrefix;
        WorkerKey = workerKey;

        // 写侧与 PipeTransport 同一个形状: 串行化落在 WorkerFrameWriter 里, 本类不重复加锁。
        _writer = new WorkerFrameWriter(stream);
        _core = new WorkerRpcCore((frame, ct) => _writer.WriteAsync(frame, ct));

        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);

        // ⚠️ 读循环必须早于握手启动: worker/hello 的响应只能靠它派发。
        // 两个任务的异常都在各自内部吞掉, 这里的 Task 未观察是有意为之(照 PipeTransport)。
        _readLoopTask = Task.Run(ReadLoopAsync);
        _ = Task.Run(WatchLivenessAsync);
    }

    /// <summary>该 Worker 的稳定标识(由 <see cref="WorkerProtocol.MakeWorkerKey"/> 从启动入参算出),
    /// 仅用于日志与诊断。生命周期侧若另有一份, <b>必须同源</b>(见该方法的跨进程一致性约束)。</summary>
    public string WorkerKey { get; }

    /// <summary>是否可用: <b>握手成功且此后没有断线</b>。已释放同样视为不可用。</summary>
    public bool IsConnected => _connected;

    /// <summary>连接远端 Worker, 启动读循环与心跳看门狗。<b>不含握手</b>(见 <see cref="HandshakeAsync"/>)。</summary>
    /// <remarks>
    /// <para><b>连接超时</b>: 调用方的 <paramref name="ct"/> 与内部
    /// <see cref="WorkerProtocol.HandshakeTimeout"/>(30s)取 linked token。判定遵循仓库的统一规则 ——
    /// 外部取消优先(原样抛 <see cref="OperationCanceledException"/>), 内部超时才是
    /// <see cref="TimeoutException"/>; 两者分开报, 否则用户点了取消却看到"超时"日志。</para>
    ///
    /// <para><b>token 的传法</b>: 走 Authorization 请求头而不是查询串 —— 查询串会随 URL 进远端访问日志与
    /// 本地诊断日志, 而这里的 token 是连接凭证。空 token 表示远端不要求认证。</para>
    ///
    /// <para>连接失败时本类不"回收任何东西"(没有进程可杀), 直接把 socket Abort + Dispose 后抛。</para>
    /// </remarks>
    /// <param name="url">远端 Worker 的地址(<c>ws://</c> / <c>wss://</c>)。</param>
    /// <param name="token">可选的 Bearer token; 空白视为不认证。</param>
    /// <param name="hello">会话身份, 仅用于算 <see cref="WorkerKey"/> 与日志前缀(与 PipeTransport 一致,
    /// 真正的握手载荷以 <see cref="HandshakeAsync"/> 的入参为准)。</param>
    /// <param name="logPrefix">日志前缀; 传 null 时用 <c>[WsWorker:{key}]</c>。</param>
    /// <exception cref="ArgumentException">url 为空或不是 ws/wss 绝对地址。</exception>
    /// <exception cref="TimeoutException">连接在 <see cref="WorkerProtocol.HandshakeTimeout"/> 内没有完成。</exception>
    /// <exception cref="InvalidOperationException">连接被拒绝/握手失败等底层错误。</exception>
    public static async Task<WebSocketTransport> StartAsync(
        string url, string? token, WorkerHelloRequest hello, string? logPrefix, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(hello);

        var key = SafeWorkerKey(hello);
        var prefix = string.IsNullOrWhiteSpace(logPrefix) ? $"[WsWorker:{key}]" : logPrefix;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss)
        {
            throw new ArgumentException($"{prefix} 非法的 Worker WebSocket 地址: {url}", nameof(url));
        }

        var socket = new ClientWebSocket();

        // KeepAliveInterval 对齐协议心跳间隔: WebSocket 层的 Ping 与协议的 notify/heartbeat 是两回事,
        // 但两侧都设成同一节拍能让"连接其实早就断了"更早被发现(协议心跳负责判僵死, 这层负责判断链)。
        socket.Options.KeepAliveInterval = WorkerProtocol.HeartbeatInterval;

        if (!string.IsNullOrWhiteSpace(token))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(WorkerProtocol.HandshakeTimeout);

        try
        {
            await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 只有"我们自己的超时"会走到这里; 外部取消走下一支
            socket.Abort();
            socket.Dispose();
            throw new TimeoutException(
                $"{prefix} 连接 Worker WebSocket 超时({WorkerProtocol.HandshakeTimeout.TotalSeconds:0.#}s): {url}");
        }
        catch (OperationCanceledException)
        {
            socket.Abort();
            socket.Dispose();
            throw; // 铁律: 调用方自己的取消原样上抛
        }
        catch (Exception ex)
        {
            socket.Abort();
            socket.Dispose();
            throw new InvalidOperationException(
                $"{prefix} 连接 Worker WebSocket 失败({url}): {ex.GetType().Name}: {ex.Message}", ex);
        }

        try
        {
            var channel = new ClientWebSocketChannel(socket);
            var stream = new WebSocketFrameStream(channel);
            var transport = new WebSocketTransport(socket, channel, stream, prefix, key);
            Log.Debug(Category, $"{prefix} 已连接 Worker WebSocket({url})");
            return transport;
        }
        catch
        {
            // 后台循环已经启动之后再出问题, 也不能把连接留成半开状态
            AbortQuietly(socket, "启动阶段失败");
            socket.Dispose();
            throw;
        }
    }

    /// <summary>握手: 下发 <c>worker/hello</c> 并校验协议版本。</summary>
    /// <remarks>
    /// <para><b>只补齐 <c>ProtocolVersion</c></b>(&lt;=0 时填当前版本)。<c>ParentPid</c> 刻意<b>不</b>补:
    /// 远端与本机不在同一个 pid 空间, 填一个本机 pid 只会让远端实现误以为那是它能探测的父进程。
    /// 池侧对远程模式固定传 0(= 不提供), 语义与 <see cref="WorkerHelloRequest.ParentPid"/> 的"0 = 未知"一致。</para>
    /// <para><b>⚠️ 版本不符 / 回 Ok=false 时只翻 IsConnected 并广播一次 Faulted, 不自行断连</b>:
    /// "换一个远端 Worker 还是降级内联"是上层策略, 收尸由上层调 <see cref="DisposeAsync"/>。
    /// 这与 PipeTransport 的处理逐字一致。</para>
    /// </remarks>
    public async Task<WorkerHelloResponse> HandshakeAsync(WorkerHelloRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtocolVersion <= 0)
        {
            request.ProtocolVersion = WorkerProtocol.ProtocolVersion;
        }

        // ParentPid 不补(见方法注释): 远程连接没有可供探测的父进程 pid。

        // 管道版在这里查"子进程是否已经退出"; WebSocket 下等价的信息是 socket 状态。
        if (_socket.State != WebSocketState.Open)
        {
            var disconnected = $"WebSocket 连接已断开(状态 {_socket.State}), 握手未完成";
            SignalFaultOnce(disconnected);
            throw new InvalidOperationException($"{_logPrefix} {disconnected}");
        }

        var result = await ExchangeAsync(
            WorkerProtocol.MethodHello,
            ToParams(request, AppJsonContext.Default.WorkerHelloRequest, "hello 请求"),
            WorkerProtocol.HandshakeTimeout,
            ct,
            WorkerProtocol.MethodHello).ConfigureAwait(false);

        if (result is null)
        {
            // 原因串不带前缀: 它会被原样传给 Faulted, 由上层加抬头
            const string empty = "握手失败: Worker 没有返回 result";
            SignalFaultOnce(empty);
            throw new InvalidOperationException($"{_logPrefix} {empty}");
        }

        var hello = ReadPayload<WorkerHelloResponse>(result, AppJsonContext.Default.WorkerHelloResponse, "hello 响应");

        if (!hello.Ok)
        {
            var rejected = $"握手被 Worker 拒绝: {hello.Error ?? "(未给原因)"}";
            SignalFaultOnce(rejected);
            throw new InvalidOperationException($"{_logPrefix} {rejected}");
        }

        // ⚠️ 版本校验是硬闸: Assignment.Status 以数字跨进程传递, 版本错位是静默的(不抛异常,
        // 只是 UI 上状态跳变)。宁可当场断开。
        if (hello.ProtocolVersion != WorkerProtocol.ProtocolVersion)
        {
            var mismatch =
                $"协议版本不符: Worker={hello.ProtocolVersion}, 本进程={WorkerProtocol.ProtocolVersion}";
            SignalFaultOnce(mismatch);
            throw new InvalidOperationException($"{_logPrefix} {mismatch}");
        }

        _connected = true;
        Log.Debug(Category, $"{_logPrefix} 握手完成(pid={hello.WorkerPid}, 协议版本 {hello.ProtocolVersion})");
        return hello;
    }

    /// <summary>全量重配置 Worker 侧工具集(<c>worker/tools/sync</c>), 返回该配置下的权威工具清单。</summary>
    /// <remarks>超时用 <see cref="WorkerProtocol.ToolsListTimeout"/>: 同步是纯内存操作, 这道闸只兜"Worker 侧内部卡死"。</remarks>
    public async Task<WorkerToolsResponse> SyncToolsAsync(WorkerToolsSyncRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUsable("工具集同步");

        var result = await ExchangeAsync(
            WorkerProtocol.MethodToolsSync,
            ToParams(request, AppJsonContext.Default.WorkerToolsSyncRequest, "tools/sync 请求"),
            WorkerProtocol.ToolsListTimeout,
            ct,
            WorkerProtocol.MethodToolsSync).ConfigureAwait(false);

        return ReadPayload<WorkerToolsResponse>(result, AppJsonContext.Default.WorkerToolsResponse, "tools/sync 响应");
    }

    /// <summary>执行一次工具调用(<c>worker/tools/call</c>)。</summary>
    /// <remarks>
    /// 超时用 <see cref="WorkerProtocol.ToolCallTimeout"/>(<b>无限</b>)—— 子代理合法跑 30 分钟,
    /// 传输层不能把"正常的慢"判成失败。兜底三件事与管道版一致: 外部取消直通 /
    /// <see cref="SendCancelAsync"/> 把取消送进远端 / <see cref="SignalFaultOnce"/> 在连接死亡时完结在飞调用。
    /// <para><b>⚠️ 传输层故障抛异常, 不转成 <c>IsError=true</c> 的响应</b>: <c>IsError</c> 的语义是
    /// "工具执行失败"(LLM 可以重试); "连接断了"是<b>连接级故障</b>, 必须经 <see cref="Faulted"/>
    /// 让上层换一条路(另一个远端 Worker 或内联降级), 而不是让 LLM 在同一个死连接上反复重试。</para>
    /// </remarks>
    public async Task<WorkerToolCallResponse> CallToolAsync(WorkerToolCallRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUsable($"工具调用 {request.Name}");

        var result = await ExchangeAsync(
            WorkerProtocol.MethodToolsCall,
            ToParams(request, AppJsonContext.Default.WorkerToolCallRequest, "tools/call 请求"),
            WorkerProtocol.ToolCallTimeout,
            ct,
            WorkerProtocol.MethodToolsCall).ConfigureAwait(false);

        return ReadPayload<WorkerToolCallResponse>(
            result, AppJsonContext.Default.WorkerToolCallResponse, "tools/call 响应");
    }

    /// <summary>把"父进程放弃等待"翻译成"远端那个调用中止"(<c>notify/cancel</c>)。</summary>
    /// <remarks>
    /// <b>⚠️ 发送失败绝不抛</b>: 连接已断时抛异常会破坏调用方的取消语义 —— 调用方正是在取消注册里调它,
    /// 它一抛就变成"取消本身失败"。这里只记一条 Warn。唯一会传播的是调用方自己的 ct 取消。
    /// 写帧用 <see cref="WorkerProtocol.CancelTimeout"/> 兜底, 免得写侧被一个不响应 Close 的对端堵死。
    /// </remarks>
    public async Task SendCancelAsync(string callId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            Log.Debug(Category, $"{_logPrefix} 传输已释放, 跳过取消通知(callId={callId})");
            return;
        }

        // 载荷走源生成的 DTO 而不是手搓 JsonObject: 帧形状只有一处定义, 两侧契约就不会漂移。
        var payload = new WorkerCancelNotification { CallId = callId };
        var frame = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["method"] = WorkerProtocol.NotifyCancel,
            ["params"] = ToParams(payload, AppJsonContext.Default.WorkerCancelNotification, "取消通知")
        };

        try
        {
            await ToolTransportContract.SendAsync(
                token => _writer.WriteAsync(frame, token),
                WorkerProtocol.CancelTimeout,
                ct,
                "取消通知").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 铁律: 调用方自己的取消原样上抛
        }
        catch (Exception ex)
        {
            // 竞态很常见(调用已经完成 / 连接刚好断了): 记 Warn 但不抛, 取消语义保持成立
            Log.Warn(Category, $"{_logPrefix} 取消通知发送失败(callId={callId}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>请求远端 Worker 退出(<c>worker/shutdown</c>)。<b>幂等</b>, 且到点没应答就不等了。</summary>
    /// <remarks>
    /// 是请求不是命令: 超时后<b>不在这里等</b>、也<b>不抛</b>。真正的连接收尾一律在
    /// <see cref="DisposeAsync"/>(close → 有界等待 → Abort), 因为释放路径必须能在应用退出时被调到,
    /// 不能被一个不肯应答的远端拖住。
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _shutdownSent, 1) != 0)
        {
            return; // 幂等: 调用方很可能在"正常关闭"与"异常兜底清理"两条路径上各调一次
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var result = await ExchangeAsync(
                WorkerProtocol.MethodShutdown,
                null,
                WorkerProtocol.ShutdownTimeout,
                ct,
                WorkerProtocol.MethodShutdown).ConfigureAwait(false);

            var ok = ReadPayload<WorkerOkResponse>(result, AppJsonContext.Default.WorkerOkResponse, "shutdown 响应");
            Log.Debug(Category,
                $"{_logPrefix} shutdown 已应答: Ok={ok.Ok}{(ok.Ok ? string.Empty : $", 原因={ok.Error}")}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 连接已断/超时: 属正常收尾路径, 不抛(见方法注释)
            Log.Debug(Category, $"{_logPrefix} shutdown 未成功({ex.GetType().Name}: {ex.Message}); 交由 DisposeAsync 收尾");
        }
    }

    /// <summary>Worker 推送的工具实时输出(子代理 stdout 逐行)。</summary>
    public event Action<WorkerToolOutputNotification>? ToolOutputReceived;

    /// <summary>Worker 推送的分派状态变更。</summary>
    public event Action<WorkerAssignmentNotification>? AssignmentReceived;

    /// <summary>连接彻底不可用(读循环结束 / IO 异常 / 写失败 / 心跳僵死)。<b>至多一次。</b></summary>
    public event Action<string>? Faulted;

    /// <summary>
    /// 后台循环: 逐帧读取(消息字节经 <see cref="WebSocketFrameStream"/> 连成 NDJSON 流), 按帧的形状分派。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ finally 里的收尾必须同时做三件事</b>: 完结全部在飞请求、翻 <see cref="IsConnected"/>、
    /// 广播一次 <see cref="Faulted"/> —— 少任何一件都会留下"永远等不到结果"的调用。</para>
    /// <para>读循环自己捕获所有异常并在 finally 收尾, 因此本任务永不 fault。</para>
    /// </remarks>
    private async Task ReadLoopAsync()
    {
        // 原因串刻意**不带**前缀: 它会被原样传给 Faulted, 上层会自己加 "Worker {key} 连接故障: " 之类的抬头
        var reason = "WebSocket 读取结束(对端关闭了连接)";

        try
        {
            while (true)
            {
                var frame = await WorkerFrameReader.ReadAsync(_stream, _stopCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break; // 正常 EOF(对端发 Close / 连接断开)
                }

                // ⚠️ 心跳判据是「收到**任何**一帧」, 不是「收到心跳」: 长耗时工具执行期间 Worker 仍可能发别的通知,
                // 把心跳当唯一判据会让"只在响应时才说话"的实现错位被误判成僵死。单调时钟, 不受墙上时钟跳变影响。
                Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);

                DispatchFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "WebSocket 读取被取消(传输已释放)";
        }
        catch (Exception ex)
        {
            // 对端失控关闭 / 连接被 Abort / 帧损坏都在这里; 完全静默会让"读循环早就死了"无从排查
            reason = $"WebSocket 读取失败: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            SignalFaultOnce(reason);
        }
    }

    /// <summary>
    /// 帧分派表(父侧视角), 与 <see cref="PipeTransport"/> 逐条一致。
    /// <list type="bullet">
    /// <item>有 id、无 method → 响应 → <see cref="WorkerRpcCore.DispatchResponse"/> 配对。</item>
    /// <item>有 id、有 method → 子→父<b>请求</b> → 记 Warn 并回一个 error 响应。</item>
    /// <item>无 id、<c>notify/toolOutput</c> → 抬 <see cref="ToolOutputReceived"/>。</item>
    /// <item>无 id、<c>notify/assignment</c> → 抬 <see cref="AssignmentReceived"/>。</item>
    /// <item>无 id、<c>notify/heartbeat</c> → 只留 Trace(时间戳已在读循环刷新)。</item>
    /// <item>无 id、其它 method → 记 Debug: 协议向前兼容, 未知通知必须忽略而不是断连。</item>
    /// </list>
    /// </summary>
    private void DispatchFrame(JsonObject frame)
    {
        var hasId = frame.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = ReadMethod(frame);

        if (method is null)
        {
            if (hasId)
            {
                _core.DispatchResponse(frame);
                return;
            }

            Log.Debug(Category, $"{_logPrefix} 收到既无 id 也无 method 的帧, 已忽略");
            return;
        }

        if (hasId)
        {
            HandlePeerRequest(idNode, method);
            return;
        }

        switch (method)
        {
            case WorkerProtocol.NotifyToolOutput:
                var output = TryRead(frame, AppJsonContext.Default.WorkerToolOutputNotification, "toolOutput 通知");
                if (output is not null)
                {
                    RaiseSafe(ToolOutputReceived, output);
                }

                return;

            case WorkerProtocol.NotifyAssignment:
                var assignment = TryRead(frame, AppJsonContext.Default.WorkerAssignmentNotification, "assignment 通知");
                if (assignment is not null)
                {
                    RaiseSafe(AssignmentReceived, assignment);
                }

                return;

            case WorkerProtocol.NotifyHeartbeat:
                // 刻意不在这里刷新时间戳: 读循环已经对**任何**帧刷新过。
                // 单列这一支只是把"心跳是协议位、不是未知通知"这件事写进代码。
                Log.Trace(Category, $"{_logPrefix} 收到 Worker 心跳");
                return;

            case WorkerProtocol.NotifyCancel:
                // 方向反了: notify/cancel 是父 → 子。记 Warn 而非 Debug, 因为它通常是"两侧实现接反了"的真 bug 信号。
                Log.Warn(Category, $"{_logPrefix} 收到方向错误的 notify/cancel(应为父→子), 已忽略");
                return;

            default:
                // 协议向前兼容: 两侧版本不同步时, 新版本的 Worker 可能多发几种通知, 忽略即可
                // (真正的不兼容由握手时的版本校验解决, 而不是靠断连)。
                Log.Debug(Category, $"{_logPrefix} 收到未识别的通知({method}), 已忽略");
                return;
        }
    }

    /// <summary>
    /// 子→父请求(当前协议里只有保留位 <c>request/askUser</c>)。
    /// ⚠️ 必须回一个 error 响应: 这个请求是在某个<b>在途</b>调用期间发出的, 而 <c>tools/call</c>
    /// 的超时是无限的 —— 静默丢弃会让对端挂到它自己的超时(实际是永远)。
    /// </summary>
    private void HandlePeerRequest(JsonNode? idNode, string method)
    {
        Log.Warn(Category,
            $"{_logPrefix} 收到 Worker 侧请求({method}, id={idNode?.ToJsonString() ?? "(无)"}), " +
            "但当前方案不启用子→父请求通道(ask_user 留在主进程); 已回 error 让对端立即失败而不是挂到超时");

        // 异步回执, 不占住读循环: 写帧可能要等写锁(此刻另一个请求正在写)
        _ = ReplyErrorAsync(idNode, method);
    }

    private async Task ReplyErrorAsync(JsonNode? idNode, string method)
    {
        var reply = new JsonObject
        {
            ["jsonrpc"] = WorkerProtocol.JsonRpcVersion,
            ["error"] = new JsonObject
            {
                ["code"] = -32601, // Method not found
                ["message"] = $"父进程不处理 Worker 侧的请求: {method}"
            }
        };

        // ⚠️ 必须回**原样**的 id: JSON-RPC 的 id 允许是数字或字符串, 归一化后再发可能让对端配不上
        // (那会把"回了一个错 id"变成"对端永远等不到响应", 比不回更糟)。
        if (idNode is not null)
        {
            reply["id"] = idNode.DeepClone();
        }

        try
        {
            await ToolTransportContract.SendAsync(
                token => _writer.WriteAsync(reply, token),
                WorkerProtocol.CancelTimeout,
                CancellationToken.None,
                "错误回执").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 铁律: OCE 排在 catch(Exception) 前面。这里用 ct=None, 实际上到不了这一支;
            // 也不重抛 —— 它是 fire-and-forget 任务, 重抛会变成没人观察的任务异常
            Log.Debug(Category, $"{_logPrefix} 回 error 响应时被取消({method})");
        }
        catch (Exception ex)
        {
            // 回不回得出去不是我们能补救的(对端要么已放弃, 要么连接已断); 不抛, 不惊动读循环
            Log.Debug(Category, $"{_logPrefix} 回 error 响应失败({method}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 心跳看门狗: <see cref="WorkerProtocol.HeartbeatTimeout"/>(60s)内没收到<b>任何</b>帧 →
    /// 判僵死 → Abort 连接 + 广播一次 Faulted。
    /// </summary>
    /// <remarks>
    /// <b>为什么"空闲时误判"可以接受, "在飞调用挂起"不可接受</b>(同 PipeTransport 的取舍):
    /// 误判的代价是上层重连一次远端 Worker; 不判的代价是一次 <c>tools/call</c> 永久挂起,
    /// 因为 <see cref="WorkerProtocol.ToolCallTimeout"/> 是无限超时, 而读循环还在傻等 WebSocket 数据。
    /// 先 Abort 再广播: 广播之后上层会立刻 DisposeAsync, 此时若连接还半开着就要白等一个 close 窗口。
    /// </remarks>
    private async Task WatchLivenessAsync()
    {
        try
        {
            while (!_stopCts.IsCancellationRequested)
            {
                await Task.Delay(WorkerProtocol.HeartbeatInterval, _stopCts.Token).ConfigureAwait(false);

                var idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastFrameTick);
                // HeartbeatTimeout 是 TimeSpan, 与 long 毫秒比较前统一换算(不要用隐式转换)。
                if (idleMs < (long)WorkerProtocol.HeartbeatTimeout.TotalMilliseconds)
                {
                    continue;
                }

                var seconds = idleMs / 1000;
                var pending = _core.PendingCount;
                Log.Warn(Category, $"{_logPrefix} Worker 心跳僵死: {seconds}s 内未收到任何帧(在飞请求 {pending} 个)");

                AbortQuietly(_socket, "心跳僵死");
                SignalFaultOnce($"Worker 心跳僵死: {seconds}s 内未收到任何帧(在飞请求 {pending} 个)");
                return; // 判死一次即可; 后续由 Faulted → DisposeAsync 收尾
            }
        }
        catch (OperationCanceledException)
        {
            // 释放路径
        }
        catch (Exception ex)
        {
            // 看门狗自己出错不该让传输变得不可用; 但必须留痕, 否则表现为"看门狗其实没在跑"
            Log.Debug(Category, $"{_logPrefix} 心跳看门狗异常退出: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 唯一的故障出口: 翻 <see cref="IsConnected"/> + 完结全部在飞请求 + <b>至多一次</b>广播 <see cref="Faulted"/>。
    /// </summary>
    /// <remarks>
    /// 全部故障入口(读循环 finally / 心跳看门狗 / 写失败 / 握手校验失败)都汇到这里 ——
    /// 少一条入口就是给"永久挂起"留一个后门。
    /// <para><b>已释放时不再广播</b>: <see cref="DisposeAsync"/> 也会让读循环走到这里。
    /// 此刻连接是<b>我们自己</b>关掉的, 广播出去会让上层把一次干净收尾记成"连接崩溃", 甚至在退出路径上
    /// 触发一次无意义的重连。</para>
    /// </remarks>
    private void SignalFaultOnce(string reason)
    {
        if (Interlocked.Exchange(ref _faultRaised, 1) != 0)
        {
            return;
        }

        _connected = false;

        // 内核侧同样有一次性闸门(它可能被本类之外的路径 Fault 过); 两道闸互不替代:
        // 内核那道保证"在飞请求只被完结一次", 本类这道保证"上层只被通知一次"
        _core.Fault(reason);

        if (Volatile.Read(ref _disposed) != 0)
        {
            Log.Debug(Category, $"{_logPrefix} 传输故障({reason}); 已释放, 不再广播");
            return;
        }

        Log.Warn(Category, $"{_logPrefix} 传输故障: {reason}");
        RaiseSafe(Faulted, reason);
    }

    /// <summary>
    /// 统一出口: 发请求 + 按超时/取消规则等响应 + 把传输层的故障语义补齐。
    /// </summary>
    /// <remarks>
    /// 超时/取消判定完全委托 <see cref="ToolTransportContract.AwaitAsync"/>(它内部再用
    /// <see cref="WorkerRpcCore.RequestAsync"/>):「外部取消优先」「无限时长不建 Task.Delay」这些规则
    /// 写在共享辅助里才谈得上"与 PipeTransport 逐字一致"。这里只额外负责两件共享辅助管不到的事:
    /// ① <b>写失败即视为故障</b>(WebSocket 不会自己恢复, 不广播 Faulted 的话在飞调用只能等超时);
    /// ② <b>超时/对端 error 不视为故障</b>(前者是远端只是慢, 后者是它明确回了一个失败 —— 连接都还活着)。
    /// </remarks>
    private async Task<JsonNode?> ExchangeAsync(string method, JsonObject? parameters,
        TimeSpan timeout, CancellationToken ct, string displayName)
    {
        try
        {
            return await _core.RequestAsync(method, parameters, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // 铁律: 取消原样上抛, 绝不能降级成"工具执行失败"
        }
        catch (TimeoutException)
        {
            throw; // 不是故障: Worker 可能只是在跑一个很长的子代理
        }
        catch (WorkerRpcException)
        {
            throw; // 对端明确回了 error: 工具级失败, 由上层转成 ToolResult.Error
        }
        catch (ObjectDisposedException)
        {
            // 传输在等待期间被释放: 在飞请求已由 DisposeAsync 完结, 不是 Worker 的故障
            throw;
        }
        catch (Exception ex)
        {
            // 其余几乎都是写侧失败(连接已断 / 对端停止接收)
            SignalFaultOnce($"请求 {displayName} 发送失败: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 请求入口的统一闸门: 已释放抛 <see cref="ObjectDisposedException"/>,
    /// 未握手/已断线抛 <see cref="InvalidOperationException"/>(上层据此决定重连或降级)。
    /// </summary>
    private void EnsureUsable(string operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_connected)
        {
            throw new InvalidOperationException($"{_logPrefix} Worker 连接不可用({operation}): 尚未完成握手或已断开");
        }
    }

    /// <summary>
    /// 释放: 完结在飞请求 → 发 close 帧 → 有界等待读循环结束 → 必要时 Abort → 释放全部资源。
    /// <b>幂等, 且绝不抛异常</b>(僵死/崩溃路径上也会走到这里, 释放动作本身不能失败)。
    /// </summary>
    /// <remarks>
    /// <para><b>顺序为什么不能改</b>:
    /// <list type="number">
    /// <item><b>先完结在飞请求</b>: 从这一刻起任何等待者都不可能再收到真实响应, 必须立刻给它们一个确定答案,
    /// 否则引擎会挂在这里等一个永远不会来的结果。</item>
    /// <item><b>再发 close(<c>CloseOutputAsync</c>)</b>: 只发 close 帧、不自己发起接收。
    /// ⚠️ <b>不能换 <c>CloseAsync</c></b> —— 它内部要等对端回 close, 会和仍在 ReceiveAsync 的读循环
    /// 撞成"并发接收"而抛 InvalidOperationException, 那与真正的连接故障毫无关系。</item>
    /// <item><b>等待期间<b>不</b>取消 _stopCts</b>: 停了读循环就收不到对端的 close 应答, 只能每次释放都白等到超时。</item>
    /// <item><b>窗口内没结束就 Abort</b>: 远端不配合时不能无限等; Abort 让 ReceiveAsync 立刻抛,
    /// 读循环收尾, 之后的 Dispose 才能干净。</item>
    /// </list></para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // 幂等: 断连与正常退出两条路径都会走到这里
        }

        _connected = false;

        // 1) 完结在飞请求(不广播 Faulted: 这是我们主动关的, 不是远端崩了)
        try
        {
            _core.FailAllPending("Worker 传输已释放");
            _core.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 完结在飞请求时异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 2) 发 close 帧(只发不等, 不抢读循环的接收通道)
        try
        {
            using var closeCts = new CancellationTokenSource(WorkerProtocol.ShutdownTimeout);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "父进程关闭", closeCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 连接已断 / 超时: 属正常收尾路径, 不抛
            Log.Debug(Category, $"{_logPrefix} 发送 WebSocket close 失败: {ex.GetType().Name}: {ex.Message}");
        }

        // 3) 有界等待读循环结束(对端回 close 后, 挂起的 ReceiveAsync 会返回 Close → 读循环自行退出)
        try
        {
            await Task.WhenAny(_readLoopTask, Task.Delay(WorkerProtocol.ShutdownTimeout)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 等待读循环结束时异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 4) 仍未结束: 直接 Abort, 不能再等
        if (!_readLoopTask.IsCompleted)
        {
            AbortQuietly(_socket, "关闭握手超时");
        }

        // 5) 停两个后台循环。⚠️ 刻意**不**释放 _stopCts: 后台任务仍可能持有它的 token
        // (Task.Delay(…, token) 在 CTS 已释放时会抛), 随实例被 GC 回收即可 —— 与 PipeTransport 同一条理由。
        try
        {
            _stopCts.Cancel();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 取消后台循环时异常: {ex.GetType().Name}: {ex.Message}");
        }

        // 6) 释放写侧与连接。顺序: 写侧闸门 → 流/通道 → socket。
        // 每个 Dispose 都单独 try/catch: 任何一步失败都不该阻止后面的清理。
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 释放帧写入器失败: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 释放 WebSocket 流失败: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _channel.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 释放 WebSocket 通道失败: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"{_logPrefix} 释放 WebSocket 连接失败: {ex.GetType().Name}: {ex.Message}");
        }

        Log.Debug(Category, $"{_logPrefix} 传输已释放");
    }

    // ── 载荷编解码(AOT: 只用源生成上下文), 与 PipeTransport 逐字一致 ────────────────

    /// <summary>DTO → <c>params</c> 节点。</summary>
    /// <remarks>
    /// 走"先序列化成文本、再解析成节点"而不是 <c>SerializeToNode</c>: 两者结果等价, 但本仓其它地方
    /// 一律用文本形态, 统一形状便于与日志里的原始帧对照。
    /// <para>⚠️ <b>绝不能改用不带 <see cref="JsonTypeInfo"/> 的重载</b>: AOT 下会抛
    /// <see cref="InvalidOperationException"/>, 而本仓的 AOT 兼容性目前没有 CI 门禁, 只在真实发布时才暴露。</para>
    /// </remarks>
    private static JsonObject? ToParams<T>(T payload, JsonTypeInfo<T> typeInfo, string what) where T : class
    {
        try
        {
            // JsonNode.Parse 返回 JsonNode?, 但本仓所有 DTO 的根都是对象, 故收窄成 JsonObject。
            // 收窄失败说明协议 DTO 定义错了(不该出现), 让它在这里炸比把异常形状拖到对端更早暴露。
            return JsonNode.Parse(JsonSerializer.Serialize(payload, typeInfo)) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Worker {what} 序列化失败: {ex.Message}", ex);
        }
    }

    /// <summary>取 <c>result</c> 节点并反序列化成 DTO; 节点缺失即视为对端违约, 抛异常而不是返回默认值。</summary>
    private static T ReadPayload<T>(JsonNode? result, JsonTypeInfo<T> typeInfo, string what) where T : class
    {
        if (result is null)
        {
            throw new InvalidOperationException($"Worker 对 {what} 没有返回 result");
        }

        try
        {
            // 经文本往返: 形状与写侧完全对称, 且不依赖任何反射式重载
            return JsonSerializer.Deserialize(result.ToJsonString(), typeInfo)
                ?? throw new InvalidOperationException($"Worker 对 {what} 返回了 null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Worker {what} 解析失败: {ex.Message}", ex);
        }
    }

    /// <summary>通知类载荷: 解析失败只记 Warn 并放弃该条通知, 绝不中断读循环。</summary>
    private static T? TryRead<T>(JsonObject frame, JsonTypeInfo<T> typeInfo, string what) where T : class
    {
        if (!frame.TryGetPropertyValue("params", out var paramsNode) || paramsNode is null)
        {
            Log.Warn(Category, $"Worker {what} 缺少 params 字段, 已忽略");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(paramsNode.ToJsonString(), typeInfo);
        }
        catch (JsonException ex)
        {
            Log.Warn(Category, $"Worker {what} 解析失败, 已忽略: {ex.Message}");
            return null;
        }
    }

    // ── 小工具 ────────────────────────────────────────────────────────────────────

    /// <summary>取帧的 method 字段(缺失/非字符串 → null)。</summary>
    private static string? ReadMethod(JsonObject frame)
    {
        if (!frame.TryGetPropertyValue("method", out var node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>逐订阅者投递(单个订阅者异常不影响其余订阅者, 也不影响读循环)。</summary>
    private void RaiseSafe<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(payload);
            }
            catch (Exception ex)
            {
                Log.Warn(Category, ex, $"{_logPrefix} 事件订阅者处理异常");
            }
        }
    }

    /// <summary>Abort 连接; 失败只记 Debug(它已经是补救动作, 失败不该再抛)。</summary>
    private static void AbortQuietly(ClientWebSocket socket, string reason)
    {
        try
        {
            socket.Abort();
        }
        catch (Exception ex)
        {
            Log.Debug(Category, $"中止 Worker WebSocket 连接失败({reason}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>算出 Worker key; 身份字段缺一时退化为可读占位串(绝不因此让连接建立失败)。</summary>
    private static string SafeWorkerKey(WorkerHelloRequest hello)
    {
        if (string.IsNullOrWhiteSpace(hello.SessionId) || string.IsNullOrWhiteSpace(hello.WorkDir))
        {
            return "未标识";
        }

        try
        {
            return WorkerProtocol.MakeWorkerKey(hello.WorkDir, hello.SessionId);
        }
        catch (Exception ex)
        {
            // MakeWorkerKey 内部会 Normalize(路径); 路径异常不值得让整个传输建立失败
            return $"无效({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// WebSocket 传输的字节通道: 把"一条消息(可能由多帧拼成)"翻译成
    /// <see cref="IWebSocketChannel"/> 的"一次 Read 返回一段字节"。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么必须处理分片与"消息比目标缓冲大"</b>: <see cref="WebSocket"/> 的接收结果是
    /// <c>(Count, EndOfMessage, MessageType)</c> 三元组 —— 单条消息可以被切成多帧, 而总长度也不受
    /// 我们传入的缓冲大小限制。上层的 <c>WorkerFrameReader</c> 只会按自己的缓冲(初始 8KB, 按需翻倍)
    /// 反复要数据, 因此这里必须把一条消息的剩余部分留在 <c>_pending</c> 里跨调用交付; 否则大帧
    /// (内含整份文件内容)会被**静默截断**, 表现为 JSON 解析失败。</para>
    /// <para><b>为什么收侧不加锁</b>: 协议约定"同一个流同一时刻只有一个读循环"(见 WorkerFrameReader),
    /// 加锁只会掩盖"两个读循环"这类真 bug。写侧则必须加锁 —— WebSocket 不允许并发 Send。</para>
    /// <para><b>⚠️ 所有权</b>: 本类<b>不拥有</b> <see cref="ClientWebSocket"/>(与 WorkerFrameWriter 不拥有流同理)。
    /// 关闭/释放 socket 是 <see cref="WebSocketTransport.DisposeAsync"/> 的职责, 这里只负责让挂起的
    /// ReceiveAsync 尽早结束。因此 <see cref="Dispose"/> 只 Abort、不 Dispose socket, 也不释放
    /// <c>_sendLock</c>(可能有在途写操作正持着它)。</para>
    /// </remarks>
    internal sealed class ClientWebSocketChannel : IWebSocketChannel
    {
        /// <summary>单次 ReceiveAsync 的缓冲。分片会被拼进 MemoryStream, 这个值只影响系统调用次数。</summary>
        private const int ReceiveChunkSize = 16 * 1024;

        private readonly ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>当前消息中尚未交付给上层的剩余字节。</summary>
        private byte[] _pending = [];

        private int _pendingOffset;
        private int _disposed;

        public ClientWebSocketChannel(ClientWebSocket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        /// <summary>读一段字节; 对端关闭(收到 Close 或连接断开)时返回 0。</summary>
        /// <remarks>
        /// 返回 0 是 <see cref="Stream"/> 的 EOF 语义, 上层(WorkerFrameReader)据此收尾。
        /// 空消息(<c>EndOfMessage</c> 且 <c>Count == 0</c>)不当作 EOF: 它不是连接结束, 只是没有载荷。
        /// </remarks>
        public async Task<int> ReceiveAsync(Memory<byte> destination, CancellationToken ct)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            while (_pendingOffset >= _pending.Length)
            {
                var message = await ReceiveMessageAsync(ct).ConfigureAwait(false);
                if (message is null)
                {
                    return 0; // 对端关闭
                }

                _pending = message;
                _pendingOffset = 0;
            }

            var count = Math.Min(destination.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(destination);
            _pendingOffset += count;
            return count;
        }

        /// <summary>发一条文本消息(<c>EndOfMessage=true</c>)。写侧串行化, 不让两个帧的字节交错。</summary>
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 二次检查: 可能在等锁期间被 Dispose
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                await _socket.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>收下一条完整消息(逐帧累积到 <c>EndOfMessage</c>); 收到 Close 返回 null。</summary>
        private async Task<byte[]?> ReceiveMessageAsync(CancellationToken ct)
        {
            var buffer = new MemoryStream();

            while (true)
            {
                var chunk = new byte[ReceiveChunkSize];
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw; // 铁律: 取消原样上抛
                }
                catch (WebSocketException ex)
                {
                    // Abort / 对端异常断开都在这里; 转成 IOException 让读循环的文案能落到"读取失败"
                    throw new IOException($"WebSocket 接收失败: {ex.Message}", ex);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Close 是独立的控制消息, 不会和前一条数据消息混在一起, 这里直接结束即可
                    return null;
                }

                if (result.Count > 0)
                {
                    buffer.Write(chunk, 0, result.Count);
                }

                if (result.EndOfMessage)
                {
                    return buffer.ToArray();
                }
            }
        }

        /// <summary>让挂起的 ReceiveAsync 立即结束。**不**释放 socket 与写锁, 理由见类注释。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _socket.Abort();
            }
            catch (Exception)
            {
                // 清理路径上不再抛; 真正的故障分类由读循环/写侧负责
            }
        }
    }
}
