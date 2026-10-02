using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// 一个已连接 Worker 子进程的会话句柄: 持有 <see cref="IToolTransport"/> 与连接元数据,
/// 把"按 callId 路由实时输出"的簿记与故障可见性收在一处, 供 <see cref="WorkerProxyTool"/> 逐个代理工具复用。
/// </summary>
/// <remarks>
/// <para><b>为什么不把 transport 直接塞进 <see cref="WorkerProxyTool"/></b>:
/// 同一 Worker 的所有工具必须共享**一份**输出路由表与一份故障记录。
/// 若每个代理工具各自订阅 <c>IToolTransport.ToolOutputReceived</c> 再自行过滤,
/// 一条通知的代价是 O(工具数) 次遍历, 且"这个 Worker 上次为什么降级"无处安放。</para>
///
/// <para><b>为什么不替 transport 兜底在飞调用</b>: 进程崩溃/管道 EOF 之后,
/// <see cref="IToolTransport"/> 有能力看到底层异常并把自己的在飞请求全部完结(抛或返回错误);
/// 本类<b>刻意不维护任何在飞调用的 TaskCompletionSource</b>。理由是双重的:
/// 一, 底层能读循环才是唯一知道"这条连接彻底没了"的地方, 由它兜底才不会出现漏网的悬挂任务;
/// 二, 这里若自己造一套在飞表, 就会与 transport 的完结路径产生竞态 ——
/// 两条路都去 SetResult/TrySetCanceled, 后到的那条要么被忽略(运气), 要么抛 InvalidOperationException
/// 在读循环线程上炸掉连接。</para>
///
/// <para><b>故障可见性是硬需求</b>: Worker 崩溃/降级必须能被 <c>doctor</c> 与状态栏看到,
/// 否则表现只是"某些工具突然一直报失败", 用户无从判断是 Worker 挂了还是 Prompt 写错了。
/// <see cref="LastFault"/> 因此是"故障史"而非"当前状态": 成功路径<b>不清空</b>它,
/// 当前健康度请看 <see cref="IsConnected"/>。</para>
/// </remarks>
public sealed class WorkerClient : IAsyncDisposable
{
    private const string LogCategory = "Worker";

    private readonly IToolTransport _transport;

    /// <summary>callId → 输出行回调。只在真有订阅者时非空(未开实时输出的调用不进表)。</summary>
    private readonly Dictionary<string, Action<string>> _outputSinks = new(StringComparer.Ordinal);
    private readonly object _sinkGate = new();

    private readonly object _faultGate = new();
    private string? _lastFault;

    private int _disposed;

    /// <param name="transport">底层传输。**所有权随之转移**: 本类即它的宿主, 由 <see cref="DisposeAsync"/> 释放。</param>
    /// <param name="workerKey">该 Worker 的稳定标识(协议层的 worker key), 用于诊断与 UI 归属。</param>
    /// <param name="workDir">Worker 启动时的工作目录; Worker 与主进程可能不在同一工作区。</param>
    /// <param name="sessionId">归属会话; Worker 是会话级子进程时会话之间互不共享。</param>
    /// <param name="processId">Worker 进程 pid; 诊断"是它真挂了还是连接先断了"时很关键。</param>
    public WorkerClient(
        IToolTransport transport,
        string workerKey,
        string? workDir = null,
        string? sessionId = null,
        int? processId = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        WorkerKey = workerKey ?? string.Empty;
        WorkDir = workDir;
        SessionId = sessionId;
        ProcessId = processId;

        // 订阅放在构造末尾: 之前若有事件到达, 转发用的字段还没就位。
        _transport.ToolOutputReceived += OnTransportToolOutput;
        _transport.AssignmentReceived += OnTransportAssignment;
        _transport.Faulted += OnTransportFaulted;
    }

    /// <summary>Worker 稳定标识(协议层 worker key)。</summary>
    public string WorkerKey { get; }

    /// <summary>Worker 启动工作目录。</summary>
    public string? WorkDir { get; }

    /// <summary>归属会话 Id(会话级 Worker 才有值)。</summary>
    public string? SessionId { get; }

    /// <summary>Worker 进程 pid(未知时为 null)。</summary>
    public int? ProcessId { get; }

    /// <summary>传输层当前是否可用(已握手且未断)。<b>注意这是瞬时值</b>, 不能替代 <see cref="LastFault"/> 做归因。</summary>
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _transport.IsConnected;

    /// <summary>
    /// 最近一次故障原因(含来自代理侧的观察, 如"调用前已断开"), 无故障时为 null。
    /// <b>成功路径不清空</b> —— 它是故障史, 供 doctor 与状态栏回溯; 判断当前是否健康用 <see cref="IsConnected"/>。
    /// </summary>
    public string? LastFault
    {
        get { lock (_faultGate) { return _lastFault; } }
    }

    /// <summary>
    /// 记录一条故障原因。<b>刻意不触发 <see cref="Faulted"/></b>:
    /// <c>Faulted</c> 是 transport 级别的生命周期信号(连接已死, 应当拆掉这个 Worker),
    /// 代理侧观察到的"这次调用失败了"不等同于连接已死 —— 混发会让连接管理方误拆健康连接。
    /// </summary>
    public void ReportFault(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        lock (_faultGate)
        {
            _lastFault = reason;
        }
    }

    /// <summary>原始实时输出通知(不做 callId 路由)。供 doctor / 调试旁挂使用; 工具执行走 <see cref="SubscribeToolOutput"/>。</summary>
    public event Action<WorkerToolOutputNotification>? ToolOutputReceived;

    /// <summary>Worker 侧主动上报的分派/任务状态(不与具体工具调用绑定)。</summary>
    public event Action<WorkerAssignmentNotification>? AssignmentReceived;

    /// <summary>连接级故障(transport 判定连接已不可用)。转发自 <c>IToolTransport.Faulted</c>。</summary>
    public event Action<string>? Faulted;

    /// <summary>握手: 协商协议版本并交换身份信息。</summary>
    public Task<WorkerHelloResponse> HandshakeAsync(WorkerHelloRequest request, CancellationToken ct) =>
        _transport.HandshakeAsync(request, ct);

    /// <summary>拉取/同步该 Worker 暴露的工具清单。</summary>
    public Task<WorkerToolsResponse> SyncToolsAsync(WorkerToolsSyncRequest request, CancellationToken ct) =>
        _transport.SyncToolsAsync(request, ct);

    /// <summary>
    /// 调用一个 Worker 工具。异常原样上抛(<see cref="OperationCanceledException"/> 也一样),
    /// 由 <see cref="WorkerProxyTool"/> 决定如何转成 <c>ToolResult</c>。
    /// </summary>
    public async Task<WorkerToolCallResponse> CallToolAsync(WorkerToolCallRequest request, CancellationToken ct)
    {
        try
        {
            return await _transport.CallToolAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户主动中断: 不是故障, 不记 LastFault(否则每次点"停止"都会留下一条误报的历史)。
            throw;
        }
        catch (Exception ex)
        {
            // 只留痕不吞: 上抛由 WorkerProxyTool 转成 ToolResult.Error(软失败), 保证引擎能继续本回合。
            ReportFault($"工具 {(request is null ? "?" : request.Name)} 调用失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>通知 Worker 放弃某个 callId(取消传播)。</summary>
    public Task SendCancelAsync(string callId, CancellationToken ct) =>
        _transport.SendCancelAsync(callId, ct);

    /// <summary>优雅关闭(发协议级 shutdown, 给 Worker 收尾窗口)。<b>应在 <see cref="DisposeAsync"/> 之前调用</b>。</summary>
    public Task ShutdownAsync(CancellationToken ct) =>
        _transport.ShutdownAsync(ct);

    /// <summary>
    /// 按 callId 订阅实时输出行, 返回的句柄释放时自动退订。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须按 callId 精确路由而不是"全投给所有订阅者"</b>:
    /// 同一 Worker 上可能有多个会话、多个引擎同时在调不同工具(后台会话 + 前台会话并行),
    /// 每条通知只属于其中一个 callId。全投会让 A 工具的输出流进 B 工具的卡片 ——
    /// 而 GUI 侧 <c>toolOutputs[callId]</c> 是按工具调用 id 归档的, 串流后无法事后分辨。
    /// 键用 <c>StringComparer.Ordinal</c>: callId 是协议里的十六进制串, 大小写归一反而可能对不上。</remarks>
    public IDisposable SubscribeToolOutput(string callId, Action<string> handler)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            throw new ArgumentException("callId 不能为空", nameof(callId));
        }

        ArgumentNullException.ThrowIfNull(handler);

        lock (_sinkGate)
        {
            _outputSinks[callId] = handler;
        }

        return new SinkSubscription(this, callId, handler);
    }

    /// <summary>退订。⚠️ 比较用引用相等而非 callId 整体删除: 同一 callId 上"旧的订阅句柄晚释放"
    /// 时不能误删新订阅(仅在 callId 碰撞时发生, 但防御是零成本的)。</summary>
    private void RemoveSink(string callId, Action<string> handler)
    {
        lock (_sinkGate)
        {
            if (_outputSinks.TryGetValue(callId, out var current) && ReferenceEquals(current, handler))
            {
                _outputSinks.Remove(callId);
            }
        }
    }

    private void OnTransportToolOutput(WorkerToolOutputNotification notification)
    {
        if (notification is null) return;

        // 先放行原始事件(诊断旁挂), 再做 callId 路由。
        RaiseSafe(ToolOutputReceived, notification);

        var line = notification.Line;
        if (line is null) return; // 字段缺失即当作空行丢弃: 推空文本没有信息量, 只会往卡片里插空行

        var callId = notification.CallId;
        if (string.IsNullOrEmpty(callId))
        {
            // 无归属输出: 只能靠上面的原始事件看到, 不硬塞给任何在飞调用 ——
            // 塞错一次比丢一行更糟(会把无关文本写进用户的工具卡片)。
            Log.Debug(LogCategory, "收到无 callId 的 toolOutput 通知, 已跳过工具路由");
            return;
        }

        Action<string>? sink;
        lock (_sinkGate)
        {
            _outputSinks.TryGetValue(callId, out sink);
        }

        if (sink is null) return; // 该调用还没订阅(刚发出请求)、或已收尾退订

        try
        {
            sink(line);
        }
        catch (Exception ex)
        {
            // 单个订阅者抛异常不能顺着读循环往上冒 —— 那样一条坏输出行会把整条 Worker 连接带走。
            // 与 EngineEventHub.Publish 的逐订阅者 try/catch 同一纪律。
            Log.Warn(LogCategory, ex, $"Worker 实时输出订阅者处理异常(callId={callId})");
        }
    }

    private void OnTransportAssignment(WorkerAssignmentNotification notification)
    {
        if (notification is null) return;
        RaiseSafe(AssignmentReceived, notification);
    }

    private void OnTransportFaulted(string reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "Worker 连接故障(原因未知)" : reason;
        ReportFault(text);
        Log.Warn(LogCategory, $"Worker {WorkerKey} 连接故障: {text}");
        RaiseSafe(Faulted, text);
    }

    /// <summary>逐订阅者投递(单个订阅者异常不影响其余订阅者与投递方)。</summary>
    private static void RaiseSafe<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null) return;

        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)d)(payload);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, ex, "Worker 事件订阅者处理异常");
            }
        }
    }

    /// <summary>
    /// 释放: 退订 transport 事件 → 清空输出路由表 → 释放 transport。
    /// <b>不调 <see cref="ShutdownAsync"/></b>: 优雅关闭要留超时窗口, 挂在 Dispose 里会在
    /// Worker 半死时把释放动作本身拖住(而 Dispose 常在应用退出路径上被调用)。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // 幂等: 断连/退出两条路径都会走到这里

        _transport.ToolOutputReceived -= OnTransportToolOutput;
        _transport.AssignmentReceived -= OnTransportAssignment;
        _transport.Faulted -= OnTransportFaulted;

        lock (_sinkGate)
        {
            _outputSinks.Clear();
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>callId 订阅句柄: 幂等释放, 且只移除自己注册的那一份。</summary>
    private sealed class SinkSubscription : IDisposable
    {
        private readonly WorkerClient _owner;
        private readonly string _callId;
        private readonly Action<string> _handler;
        private int _removed;

        public SinkSubscription(WorkerClient owner, string callId, Action<string> handler)
        {
            _owner = owner;
            _callId = callId;
            _handler = handler;
        }

        public void Dispose()
        {
            // Interlocked 保幂等: 正常返回 / 取消 / 异常三条路径都会经过 using 的 finally,
            // 重复移除除了白拿一次锁没有别的害处, 但会让"退订后仍有输出在飞"更难推理。
            if (Interlocked.Exchange(ref _removed, 1) != 0) return;
            _owner.RemoveSink(_callId, _handler);
        }
    }
}
