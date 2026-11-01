namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// 不可用传输: Worker 起不来时的<b>兜底句柄</b>, 任何请求都立刻以明确原因报错。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它(以及为什么不是 <see cref="InlineTransport"/>)</b>:
/// 生产路径已<b>不再</b>把工具悄悄改回主进程执行 —— 内联会让 <c>grep</c>/子代理重新拖住 UI,
/// 并且把"Worker 为什么起不来"(产物缺失 / 端口不通 / token 不符 / 协议版本不符)
/// 完全掩盖成"工具还能用"。本类反过来: 它什么都不做, 只把原因带到调用方,
/// 由 <see cref="WorkerProxyTool"/> 的前置连通性检查转成一条明确的工具错误。</para>
///
/// <para><b>它没有「对端」</b>: <see cref="IsConnected"/> 恒为 false,
/// <see cref="Faulted"/> 恒不触发(连接从未建立, 没有"断了"这个事实)。
/// 三个事件都用显式 add/remove 而不是字段式事件, 理由同 <see cref="InlineTransport"/>:
/// 字段式事件永不 raise 会触发 CS0067, 且订阅成功却永不回调是典型的静默失效。</para>
///
/// <para>⚠️ 本类不记录日志: 原因由构造它的 <see cref="WorkerPool"/> 统一打(带会话与目录上下文),
/// 这里再打一遍只会让同一个故障出现两条互相没有关联的记录。</para>
/// </remarks>
public sealed class UnavailableTransport : IToolTransport
{
    private readonly string _reason;

    /// <param name="reason">Worker 不可用的原因(会原样进入日志与工具错误文案, 因此必须是给人看的话)。</param>
    public UnavailableTransport(string reason)
    {
        _reason = string.IsNullOrWhiteSpace(reason) ? "原因未知" : reason.Trim();
    }

    /// <summary>恒为 false: 本类没有可用的连接, 上层据此在真正发起请求前就失败。</summary>
    public bool IsConnected => false;

    /// <summary>
    /// 直接失败: 握手只对"存在一个对端"的传输有意义, 走到这里说明上游的生命周期判断已经错了,
    /// 与其返回一个伪造的响应让上层以为"握手成功", 不如把事实暴露出来。
    /// </summary>
    public Task<WorkerHelloResponse> HandshakeAsync(WorkerHelloRequest request, CancellationToken ct) =>
        throw new InvalidOperationException($"Worker 不可用: {_reason}");

    /// <summary>直接失败: 同 <see cref="HandshakeAsync"/>, 没有对端可以同步工具集。</summary>
    public Task<WorkerToolsResponse> SyncToolsAsync(WorkerToolsSyncRequest request, CancellationToken ct) =>
        throw new InvalidOperationException($"Worker 不可用: {_reason}");

    /// <summary>
    /// 以<b>软失败</b>返回而不是抛异常: 让引擎能把这条错误当作普通工具结果交给 LLM
    /// (用户可以重试或换手段), 而不是让整回合以异常收场。
    /// </summary>
    public Task<WorkerToolCallResponse> CallToolAsync(WorkerToolCallRequest request, CancellationToken ct) =>
        Task.FromResult(new WorkerToolCallResponse
        {
            IsError = true,
            Content = $"Worker 不可用: {_reason}"
        });

    /// <summary>空操作: 没有在飞调用, 也没有对端可通知。</summary>
    public Task SendCancelAsync(string callId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>空操作: 没有进程可关(本类存在的意义正是"从未启动成功")。</summary>
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>恒不触发: 没有对端, 也就没有实时输出。</summary>
    public event Action<WorkerToolOutputNotification>? ToolOutputReceived
    {
        add { }
        remove { }
    }

    /// <summary>恒不触发: 没有对端, 也就没有分派状态推送。</summary>
    public event Action<WorkerAssignmentNotification>? AssignmentReceived
    {
        add { }
        remove { }
    }

    /// <summary>恒不触发: 连接从未建立, 不存在"之后断了"这个事实(它是不可用, 不是故障)。</summary>
    public event Action<string>? Faulted
    {
        add { }
        remove { }
    }

    /// <summary>空实现: 本类不持有任何资源。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
