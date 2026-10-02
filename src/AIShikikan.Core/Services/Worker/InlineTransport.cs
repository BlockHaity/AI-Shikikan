using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/// <summary>内联传输: 工具改在<b>主进程内</b>直接跑, 零 IPC。
///
/// <para><b>它是降级路径, 不是优化路径</b>: <c>WorkerLocator</c> 的 5 级候选全未命中时
/// (Worker 未构建 / 未随包分发 / 被用户删掉 / 拉起来就崩) 由它接管, 此时用户看到的行为必须与
/// 引入 Worker <b>之前完全一致</b> —— 同样的工具、同样的文案、同样的审批闸、同样的取消语义。
/// 与今天的唯一差别是「没有崩溃隔离与资源隔离」: 子代理进程树卡死会拖住主进程, 工具异常会冒到引擎的
/// 兜底里。这两点是<b>已知且可接受</b>的, 因为它们正是有 Worker 时被消除掉的东西。
/// 降级必须可见(见 <c>worker-architecture.md</c> 第 6 节): 上层负责在 UI/日志/doctor 里报出来,
/// 本类只负责把日志留全 —— 每条同步与调用都带一条 Debug, 便于事后回答「这次到底走没走内联」。</para>
///
/// <para><b>刻意不实现的东西</b>(以及为什么):
/// <list type="bullet">
/// <item><b>心跳</b>: 没有独立进程, 也就没有「僵死」这个状态 —— 进程不响应只可能是本进程在忙,
/// 而忙这件事本身不需要别人来探活。</item>
/// <item><b><see cref="SendCancelAsync"/></b>: 空实现。取消已经由 <see cref="IToolTransport.CallToolAsync"/>
/// 的令牌<b>直连</b>到工具执行上了, 再补一条「取消通道」只会制造第二个取消源。</item>
/// <item><b>三个事件</b>: 见下方「事件恒不触发」一节。</item>
/// </list></para>
///
/// <para><b>事件恒不触发</b>(<see cref="IToolTransport.ToolOutputReceived"/> /
/// <see cref="IToolTransport.AssignmentReceived"/> / <see cref="IToolTransport.Faulted"/>):
/// 内联模式下这三份数据的<b>权威来源仍是进程内通道</b> —— 工具实时输出走
/// <c>ToolContext.OnToolOutput</c>, 分派状态走 <c>AssignmentManager.AssignmentChanged</c>,
/// 与引入 Worker 之前逐字相同。本类若再伪造一遍并触发同名事件, 上层就会<b>双投递</b>
/// (Agent 面板出现两条相同的分派记录、输出被追加两次)。因此这里只是「实现接口以满足抽象」,
/// 恒不发事件; 降级判定时上层只应查 <see cref="IsConnected"/>, 不要靠有没有事件来判别模式。</para>
/// </summary>
public sealed class InlineTransport : IToolTransport
{
    /// <summary>日志 category。</summary>
    private const string Category = "Worker";

    private readonly InlineToolExecutor _executor;
    private readonly InlineToolsLister _lister;

    private volatile bool _disposed;

    /// <summary>构造内联传输。两个委托都由调用方提供: 它持有 <c>ToolRegistry</c> 与本会话的
    /// <c>ToolContext</c> 快照(人格 / roster / Plan 模式 / 工作目录), 因此本类无需认识工具层任何类型。</summary>
    public InlineTransport(InlineToolExecutor executor, InlineToolsLister lister)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(lister);

        _executor = executor;
        _lister = lister;
    }

    /// <summary>恒为 true, <b>包括已 <see cref="DisposeAsync"/> 之后</b>。
    ///
    /// <para>放行之后不翻 false 是刻意的: 本类没有「远端」可重连, 上层若拿到 false 只会去做
    /// 「再拉一个 Worker / 再握手一次」这类注定失败的重试, 而重试不了的原因(已释放)会被这条
    /// false 掩盖成「连不上」。已释放的事实由各处 <see cref="ObjectDisposedException"/> 明确表达。</para>
    /// </summary>
    public bool IsConnected => true;

    /// <summary>握手在降级模式下是<b>本地合成</b>: 没有对端可校验, 因此不回传请求里的任何东西,
    /// 只如实报告「我成功、我就是本进程、版本是当前协议版本」, 让上层能把它与真实 Worker 区分开
    /// 并展示降级状态。版本是否与预期一致由<b>调用方</b>比较(本类没有比较的立场, 见接口注释)。</summary>
    ///
    /// <para><b><c>WorkerPid</c> 填本进程 pid 是刻意的「诚实降级」</b>, 不是占位符:
    /// 内联模式下 Worker 确实<em>就是</em>这个进程。伪造成别的值, 排障时「日志 pid 与 <c>ps</c>
    /// 对不上」会带人往错的方向查。</para>
    /// </summary>
    public Task<WorkerHelloResponse> HandshakeAsync(WorkerHelloRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        Log.Debug(Category, "内联传输握手: 无对端, 直接返回本进程身份");
        return Task.FromResult(new WorkerHelloResponse
        {
            Ok = true,
            WorkerPid = Environment.ProcessId,
            ProtocolVersion = WorkerProtocol.ProtocolVersion
        });
    }

    /// <summary>全量同步工具集: 直接派给注入的列举器(它读的是本进程的 <c>ToolRegistry</c>)。
    /// 超时用 <see cref="WorkerProtocol.ToolsListTimeout"/> —— 列举是纯内存操作, 超时只用来兜住
    /// 「列举器内部反常地卡住」, 与管道传输取同一个值以保持两条路径的可观测性一致。</summary>
    public Task<WorkerToolsResponse> SyncToolsAsync(WorkerToolsSyncRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return ToolTransportContract.AwaitAsync(
            token => _lister(request, token),
            WorkerProtocol.ToolsListTimeout,
            ct,
            "工具集同步");
    }

    /// <summary>执行一个工具调用: 直接派给注入的执行器。
    ///
    /// <para><b>取消语义靠令牌直连保证</b>, 这是本方法唯一不可让步的部分:
    /// 传进去的令牌就是引擎的取消令牌(经 <see cref="ToolTransportContract"/> 在无限超时下原样透传),
    /// 于是「用户点停止」→ 引擎 ct 取消 → 工具/子代理进程当场中止, 与今天的行为逐字相同。
    /// 如果这里再加一层自己的 CTS, 就会出现「主循环停了但子进程还在跑」——
    /// 那是本仓库反复强调的铁律(<c>catch (OperationCanceledException) { throw; }</c> 必须在
    /// <c>catch (Exception)</c> 之前)所防的同一类事故。</para>
    ///
    /// <para>用 <see cref="WorkerProtocol.ToolCallTimeout"/>(无限)而不是自定义值: 降级路径
    /// 不该有比管道路径更短的上限, 否则同一份配置在两种模式下的表现会不一致, 「行为等同今天」
    /// 就没法验证了。</para>
    ///
    /// <para><b>关于 catch 顺序</b>: 本方法<b>一个 catch 都没有</b>, 所以
    /// <see cref="OperationCanceledException"/> 与其它异常原样向上传播, 铁律
    /// 「<c>catch (OperationCanceledException) { throw; }</c> 必须在 <c>catch (Exception)</c> 之前」
    /// 在这里是<b>结构性满足</b>而不是靠人工顺序。那条铁律真正落到
    /// <see cref="InlineToolExecutor"/> 的<b>实现方</b>身上(它要 try/catch 工具异常转成
    /// <c>ToolResult.Error</c>, 那里最容易写反), 接线时务必复核。</para>
    /// </summary>
    public async Task<WorkerToolCallResponse> CallToolAsync(WorkerToolCallRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await ToolTransportContract.AwaitAsync(
            token => _executor(request, token),
            WorkerProtocol.ToolCallTimeout,
            ct,
            "工具调用").ConfigureAwait(false);

        // 不判 IsError: 那是代理层(WorkerProxyTool)该做的事, 本类只负责把执行器的结果原样带出去,
        // 免得同一个错误在两层各被包装一次(上层看到的是「工具执行异常」而不是工具自己的文案)。
        Log.Debug(Category, $"内联工具调用完成({sw.ElapsedMilliseconds}ms)");
        return response;
    }

    /// <summary>空操作: 内联模式下取消已经由 <see cref="CallToolAsync"/> 的令牌<b>直连</b>到
    /// 工具执行, 真的子代理进程树也由工具内部 linked 的 token 负责中止(<c>CliAgentRunner</c> 会
    /// <c>Kill(entireProcessTree)</c>)。</summary>
    ///
    /// <para><b>为什么必须是 no-op 而不是「重复取消一次」</b>: 管道传输下父进程发 cancel 是刚需 ——
    /// 「父进程放弃等待」与「子进程里的那个调用被中止」是两个进程里两件事, 隔着一条随时可能已断的管道,
    /// 只能靠显式一帧送达。内联模式没有这道隔断, 再补一条取消通道会制造<b>第二个取消源</b>:
    /// 上层「停止」按钮、回合 ct、这里 —— 三处都能取消同一个调用, 后果是同一处代码要判断
    /// 「这次到底该由谁取消」, 以及两次取消先后抵达时出现时序相关的行为差异。
    /// 单一取消源(引擎的 ct)在降级路径下是<b>更强</b>的性质, 不是缺失。</para>
    ///
    /// <para>刻意不抛异常: 「取消没送达」不是调用方的错误, 让调用方的取消语义照常成立。</para>
    /// </summary>
    public Task SendCancelAsync(string callId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        Log.Debug(Category, $"内联模式忽略取消通知({callId}): 取消已由调用令牌直连");
        return Task.CompletedTask;
    }

    /// <summary>空操作: 没有进程可关。刻意<b>不</b>在这里关闸(置 disposed) ——
    /// 上层很可能在「正常关闭」与「异常兜底清理」两条路径上各调一次本方法, 一旦它带终止语义,
    /// 第二次调用就会抛 <see cref="ObjectDisposedException"/>。真正的释放闸门在
    /// <see cref="DisposeAsync"/>, 且它是幂等的。</summary>
    public Task ShutdownAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Log.Debug(Category, "内联模式无需关闭 Worker");
        return Task.CompletedTask;
    }

    /// <summary>内联模式恒不触发: 见类注释「事件恒不触发」。
    ///
    /// <para>写成显式 add/remove 而非字段式事件有两个理由: ① 字段式事件永不 raise 会触发
    /// CS0067(「该事件从未被使用」), 而本仓的验收锚点是零警告; ② 更重要的是, 字段式事件的沉默
    /// 发生在订阅者看不见的地方 —— 上层按统一入口写 <c>transport.ToolOutputReceived += h</c>,
    /// 订阅成功、无异常、永远不回调, 是典型的静默失效。改在<b>订阅发生时</b>留一条 Debug,
    /// 排障时至少能立刻确认「内联模式确实没有这条通知」。</para>
    /// </summary>
    public event Action<WorkerToolOutputNotification>? ToolOutputReceived
    {
        add => Log.Debug(Category, "内联模式无 toolOutput 通知: 实时输出仍由 ToolContext.OnToolOutput 直达 UI");
        remove { }
    }

    /// <summary>内联模式恒不触发: 见类注释「事件恒不触发」。</summary>
    public event Action<WorkerAssignmentNotification>? AssignmentReceived
    {
        add => Log.Debug(Category, "内联模式无 assignment 通知: 分派状态仍由 AssignmentManager.AssignmentChanged 直达 UI");
        remove { }
    }

    /// <summary>内联模式恒不触发: 没有独立进程, 也就没有「进程崩了」这个事实。
    /// 工具自身抛异常由调用方在工具层就地看到(并按 <c>ToolResult.Error</c> 之类的既有方式呈现),
    /// 那属于「工具失败」而不是「传输故障」, 混进本事件会让上层的降级/重拉逻辑被工具级错误触发。</summary>
    public event Action<string>? Faulted
    {
        add => Log.Debug(Category, "内联模式无传输故障事件: 本类没有可崩溃的对端, 无需 Faulted 兜底");
        remove { }
    }

    /// <summary>释放闸门: 之后任何请求入口都抛 <see cref="ObjectDisposedException"/>, 而不是静默接受
    /// 然后执行 —— 已释放的传输继续干活比抛异常更难排查。幂等(重复释放无害)。</summary>
    ///
    /// <para><b>刻意不去中断在飞的工具调用</b>: 它们由调用方的令牌管辖, 本类既没有它们的句柄,
    /// 也没有比调用方更「权威」的理由去抢这个取消权(见 <see cref="SendCancelAsync"/> 的说明)。
    /// 真要收尾, 由调用方先取消令牌、再 DisposeAsync, 顺序与语义都由它自己掌控。</para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}