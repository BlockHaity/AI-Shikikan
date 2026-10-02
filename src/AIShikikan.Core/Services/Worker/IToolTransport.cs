using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/// <summary>Worker 传输抽象: 请求/响应配对 + 断线通知, 不关心工具具体怎么跑。
/// 两个实现: InlineTransport(同进程直调, 零 IPC) / PipeTransport(子进程 stdio 管道)。
///
/// <para><b>为什么要有这层抽象</b>(<c>docs/plans/worker-architecture.md</c> 3.3): 管道不是唯一实现,
/// 抽象之后才能同时拿到两样东西 —— ①「Worker 产物找不到 / 起不来 / 中途崩溃」时<b>静默降级</b>回同进程执行,
/// 行为与今天逐字一致(没有崩溃隔离, 仅此而已); ②「分步验证」: 先用 InlineTransport 在不引入进程的前提下
/// 跑通协议编解码与全部工具, 之后再换 PipeTransport 只改工厂一行。
/// 抽象的边界刻意画在「一次工具调用怎么被执行」这层之上: 它<b>不</b>暴露流、进程句柄与行协议,
/// 上层永远只看见「一个可取消的请求 → 一个响应」。</para>
///
/// <para><b>实现方必须遵守的三条约定</b>(否则与 InlineTransport 的行为会出现分叉, 降级路径就不再等价):
/// <list type="number">
/// <item>所有等待都必须同时受「内部超时」与「调用方 <c>CancellationToken</c>」约束, 且
/// <b>外部取消优先</b> —— 见 <see cref="ToolTransportContract"/>。</item>
/// <item>不得吞掉 <see cref="OperationCanceledException"/>。</item>
/// <item>断线时既要 <see cref="Faulted"/> 广播, 也要把所有在飞请求完成为错误。</item>
/// </list></para>
///
/// <para>⚠️ <b>事件在引擎线程上触发</b>: 与 <c>AgentEngineEvent</c> 同理,
/// 且 <c>run_subagents</c> 会在主进程并发转发多个子代理的输出帧。订阅方必须自行转交 UI 线程
/// (<c>ChatPageViewModel</c> 用 <c>ConcurrentQueue</c> + <c>Interlocked</c> 节流, 不要在这里碰 UI 集合)。</para>
/// </summary>
public interface IToolTransport : IAsyncDisposable
{
    /// <summary>传输是否可用。InlineTransport 恒为 true。
    ///
    /// <para>语义刻意是「<b>现在能不能立刻发请求</b>」, 而不是「曾经成功握手过」: 上层用它决定
    /// 「要不要 fallback 到 InlineTransport」。因此它必须允许在故障后翻成 false, 且由实现方保证
    /// 「false 之后不会有任何请求被受理」。</para>
    /// </summary>
    bool IsConnected { get; }

    /// <summary>握手: 双方对齐协议版本, 返回 Worker 侧身份。
    ///
    /// <para>返回的 <c>ProtocolVersion</c> 必须与请求里的对得上, 对不上时<b>由调用方</b>决定是否降级
    /// (传输自己不比较 —— 它没有权限决定「版本不符就换条路走」, 那会把「重拉一个新 Worker」这种策略
    /// 塞进一个只该负责搬运的实现里)。</para>
    /// </summary>
    Task<WorkerHelloResponse> HandshakeAsync(WorkerHelloRequest request, CancellationToken ct);

    /// <summary>全量重配置 Worker 侧工具集, 返回该配置下的权威工具清单。
    /// 每次 LLM 回合都会读注册表, 故这里同步全量即可, 不需要增量。</summary>
    Task<WorkerToolsResponse> SyncToolsAsync(WorkerToolsSyncRequest request, CancellationToken ct);

    /// <summary>执行一个工具调用。默认无超时(子代理合法跑 30 分钟), 仅由取消与崩溃打断。
    ///
    /// <para><b>为什么默认无超时</b>: <c>CliAgentDefinition.TimeoutMinutes</c> 默认 30、上界 1440(24h),
    /// 而 <c>run_&lt;agent&gt;</c> / <c>assign_task</c> / <c>run_subagents</c> 是<b>阻塞式</b>工具 ——
    /// 它们 await 到子 Agent 终态才返回。传输层再加一个比子代理自身超时更短的上限, 等于把
    /// 「一个合法的长任务」在传输层判成失败: 用户看到工具报错、子进程还在跑、日志里只有一句超时,
    /// 比「慢」糟糕得多。长任务的闸门应该由<b>它自己</b>(agent 的 timeout_minutes)管, 传输层只负责
    /// 「出事了不要让我永久等待」, 那正是 <see cref="Faulted"/> + 外部 <c>CancellationToken</c> 的职责。</para>
    ///
    /// <para><b>取消的语义</b>: 传入的 <paramref name="ct"/> 必须<b>直连</b>到真正干活的那段代码
    /// (Worker 侧 linked 到 <c>CliAgentRunner</c> 的 token; InlineTransport 侧就是本进程的工具执行)。
    /// 收到取消时以 <see cref="OperationCanceledException"/> 终结, <b>不得</b>降级成
    /// <c>ToolResult.Error</c> 或其他异常 —— 否则「停止」按钮只会停住主循环, 子进程仍在后台跑。</para>
    ///
    /// <para><b>失败与崩溃</b>: Worker 崩溃/管道 EOF 时, 应把所有在飞调用完成为带明确原因的响应
    /// (软失败, 让 LLM 能看到失败并在下一轮重试), 而不是让引擎靠异常兜底 —— 兜底文案
    /// 「工具执行异常: ...」会丢掉「Worker 崩了」这个可诊断信息。</para>
    /// </summary>
    Task<WorkerToolCallResponse> CallToolAsync(WorkerToolCallRequest request, CancellationToken ct);

    /// <summary>通知 Worker 取消某个在飞调用(回合取消 / 用户点停止)。
    ///
    /// <para><b>为什么不并进 <see cref="CallToolAsync"/> 的 token</b>: 管道传输下父进程取消的是
    /// 「主进程的等待」, 与「Worker 进程里的那个调用」是<b>两个进程里的两个 CTS</b>, 中间隔着一条可能
    /// 已经断了的管道。只有显式发一帧, Worker 侧才有机会 <c>Kill(entireProcessTree)</c> 掉子进程树;
    /// 靠父侧放弃等待来「取消」子进程, 结果就是一个跑飞了却没人管的孤儿。</para>
    ///
    /// <para>因此本方法<b>不得抛</b>来表达「取消没送达」(如管道已断): 通知送达失败时记日志即可,
    /// 让调用方的取消语义保持成立。已经是终态的 callId 属于正常情况(竞态), 静默忽略。</para>
    /// </summary>
    Task SendCancelAsync(string callId, CancellationToken ct);

    /// <summary>请求 Worker 退出(父进程退出 / 目录引用计数归零)。
    ///
    /// <para>是<b>请求</b>不是命令: 到点没退就由 <see cref="Faulted"/> 走崩溃路径收尾,
    /// 实现方不得在这里无限等待。另外应保证<b>幂等</b> —— 调用方很可能在「正常关闭」与
    /// 「异常兜底清理」两条路径上各调一次。</para>
    /// </summary>
    Task ShutdownAsync(CancellationToken ct);

    /// <summary>Worker 侧推送的工具实时输出(子代理 stdout 逐行) → 主进程 Raise EngineToolOutput。</summary>
    event Action<WorkerToolOutputNotification>? ToolOutputReceived;

    /// <summary>Worker 推送的分派状态变更 → 主进程落盘 + 刷新 Agent 面板 + 记用量。</summary>
    event Action<WorkerAssignmentNotification>? AssignmentReceived;

    /// <summary>传输故障/断线(Worker 崩溃、管道 EOF、握手版本不符)。参数是原因。
    /// <b>上层必须订阅它</b>: 断线时所有在飞调用要被完成为错误, 绝不能让引擎永久等待。</summary>
    ///
    /// <para><b>为什么必须是接口成员, 而不是让上层靠超时兜底</b>: 超时兜底会把两种完全不同的故障
    /// 伪装成同一种现象 —— 「Worker 已死」被报成「工具慢」。后果有三处: ① LLM 拿到「超时」文案后会
    /// 重试同一个工具, 而 Worker 根本不存在, 于是每个回合都空转一个超时周期; ② 日志里全是超时、
    /// 零条崩溃记录, 事后无法区分「工具真的慢」与「进程炸了」; ③ 没有「当前这个 Worker 不可用」的
    /// 信号, 生命周期侧(WorkerKey 引用计数、重拉)就没有触发点, 降级到 InlineTransport 也无从谈起。
    /// 换句话说, <b>超时是兜底, 断线是事实</b>, 二者不能互相顶替。</para>
    ///
    /// <para>触发时机: 读循环 EOF / IO 异常 / 子进程退出 / 写侧失败 / 心跳僵死。
    /// <b>至多一次</b>: 实现应在首次故障后立即把 <see cref="IsConnected"/> 置 false 并停止后续触发,
    /// 免得上层按事件做计数时重复加锁/重复降级。</para>
    /// </summary>
    event Action<string>? Faulted;
}

/// <summary>把一个工具调用派给<b>本进程</b>的执行器(<see cref="InlineTransport"/> 的注入点)。
///
/// <para>刻意用委托而不是接口: 接口要么直接吃 <c>ITool</c>/<c>ToolRegistry</c>(于是
/// <c>Services.Worker</c> 反向依赖 <c>Services.Tools</c>, Worker 进程与主进程都要被拽进工具层),
/// 要么自造一个「一个工具方法」的接口(把 <c>ITool.ExecuteAsync</c> 的形状再抄一遍,
/// 以后加参数就得同步改两处)。委托让 Worker 传输层<b>只认识 DTO</b>, 认识
/// <c>ToolRegistry</c> 的活留给调用方(它本来就已经持有注册表)。</para>
/// </summary>
public delegate Task<WorkerToolCallResponse> InlineToolExecutor(
    WorkerToolCallRequest request, CancellationToken ct);

/// <summary>列出/同步工具清单(见 <see cref="IToolTransport.SyncToolsAsync"/>)。</summary>
public delegate Task<WorkerToolsResponse> InlineToolsLister(
    WorkerToolsSyncRequest request, CancellationToken ct);

/// <summary>两个传输实现共享的「发请求 + 等响应」与超时/取消辅助。
///
/// <para>存在理由: <c>McpClientBase</c> 已经把这条路的坑踩过一遍(<c>RequestAsync</c>,
/// <c>McpClientBase.cs:48-105</c>), 同样的坑在 Worker 传输里会原样重现一遍。抽出来是为了让
/// InlineTransport 与 PipeTransport 的<b>超时/取消语义逐字一致</b> —— 降级路径一旦在这一点上偏了,
/// 「行为与今天一致」就无从验证。</para>
///
/// <para><b>唯一的核心规则: 外部 <c>CancellationToken</c> 的取消优先级高于内部超时。</b>
/// 两者都到点时, 用户看到的必须是「用户取消了」而不是「超时了」: 前者要静默(用户自己点的停止),
/// 后者要留一条 Warn 日志 + 明确文案。顺序反了的表现是「点一下停止, 日志里多出三条超时」,
/// 而真正该修的那条故障线索被冲掉了。</para>
/// </summary>
internal static class ToolTransportContract
{
    private const string Category = "Worker";

    /// <summary>这个时长是否表示「无限」。
    ///
    /// <para>不只认 <see cref="Timeout.InfiniteTimeSpan"/>: 任何<b>负时长</b>与 <see cref="TimeSpan.MaxValue"/>
    /// 都按无限处理。理由是这三个值在底层 API 上全是雷: <c>Task.Delay</c> 对超过
    /// <see cref="uint.MaxValue"/> 毫秒的时长直接抛 <c>ArgumentOutOfRangeException</c>,
    /// <c>CancellationTokenSource.CancelAfter</c> 只放行 -1ms。一个手滑写出来的 -5ms
    /// 或 <c>TimeSpan.MaxValue</c> 若被当成「有限时长」传下去, 会把一次配置错误变成运行期崩溃;
    /// 当成无限则只是少一道闸门, 仍能被外部取消与 <see cref="IToolTransport.Faulted"/> 兜住。</para>
    /// </summary>
    public static bool IsUnbounded(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan || timeout == TimeSpan.MaxValue || timeout < TimeSpan.Zero;

    /// <summary>发起一次请求并等响应: <paramref name="what"/> 是给日志/异常文案用的短描述(如 "工具调用")。
    ///
    /// <para><paramref name="start"/> 收一个<b>已合并</b>的令牌: 有限超时时是「外部 ct + 内部超时」的
    /// linked token, 无限超时时<b>就是外部 ct 本身</b>(不套壳, 让取消直连到真正干活的代码, 少一层
    /// 间接也就少一处可能忘记传递的地方)。要求实现方<b>在 start 内部</b>完成「登记挂起表 → 写帧 →
    /// 返回该 id 的 TCS 任务」, 这样「响应比登记先到」的竞态不可能发生。</para>
    ///
    /// <para><b>约束写的是 <c>where T : class?</c> 而非 <c>class</c></b>: 本 helper 服务的是 JSON-RPC,
    /// 而 JSON-RPC 的 <c>result</c> 允许缺失(此时取到 null)。若约束成非空引用类型, 调用方就得
    /// 为"结果可空"这件事额外套一层 async 去收可空性, 换来的是编译期警告与一层无谓间接。</para>
    /// </summary>
    public static async Task<T> AwaitAsync<T>(
        Func<CancellationToken, Task<T>> start,
        TimeSpan timeout,
        CancellationToken ct,
        string what) where T : class?
    {
        ArgumentNullException.ThrowIfNull(start);
        ct.ThrowIfCancellationRequested();

        if (IsUnbounded(timeout))
        {
            // 无限时长: 刻意**不**创建 Task.Delay。
            // Task.Delay(InfiniteTimeSpan) 虽被接受, 但它只是「一个永远不会完成的 Task」,
            // 既没带来任何约束, 又逼着这条路径依赖 Task.Delay 对边界时长的容忍度
            // (同一条语句换个值就 ArgumentOutOfRange)。改成用一个「ct 取消即完成」的哨兵 TCS 与响应竞速,
            // 语义完全等价 —— 唯一的等待者只能是 ct 或响应, 二者都是真事件。
            var cancelSentinel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelSentinel);

            var pending = start(ct);
            if (await Task.WhenAny(pending, cancelSentinel.Task).ConfigureAwait(false) != pending)
            {
                // 哨兵只可能由 ct 取消完成 ⇒ 这里必然是「外部取消」, 不可能是超时。
                // Observe 掉 pending: 调用方已经不再等它, 稍后它若带异常抵达,
                // 没人观察会触发 UnobservedTaskException, 在别的线程上炸出一条与本处无关的噪声。
                ObserveFault(pending);
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException(ct);
            }

            return await pending.ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        var running = start(token);
        var guard = Task.Delay(timeout, token);

        if (await Task.WhenAny(running, guard).ConfigureAwait(false) != running)
        {
            ObserveFault(running);

            // ⚠️ 判定只能看**外部** ct, 不能看 timeoutCts.Token:
            // 后者被我们自己 CancelAfter 取消过, 用它判定会把「Worker 不响应」报成「用户取消」,
            // 两种语义在日志与上层行为上完全相反(见类注释)。
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            var msg = $"Worker {what} 超时({timeout.TotalSeconds:0.#}s)";
            Log.Warn(Category, msg);
            throw new TimeoutException(msg);
        }

        return await running.ConfigureAwait(false);
    }

    /// <summary>发一条<b>没有响应</b>的帧(<c>notify/cancel</c> / <c>worker/shutdown</c>)并等它写完。
    ///
    /// <para>用 <c>Task.WhenAny</c> 而不是直接 await: 管道写侧可能被一个不肯退出的对端堵死,
    /// 「写一帧」这种本该瞬间完成的操作不该有能力把主进程钉住。超时判定与 <see cref="AwaitAsync"/>
    /// 保持同一套优先级规则。</para>
    ///
    /// <para>⚠️ 调用方不应把本方法抛出的超时当作「帧没送达」的证据去重试: 通知类帧重复发送是无害的,
    /// 而「送达失败」与「写入慢」在管道上无法区分。</para>
    /// </summary>
    public static async Task SendAsync(
        Func<CancellationToken, Task> send,
        TimeSpan timeout,
        CancellationToken ct,
        string what)
    {
        ArgumentNullException.ThrowIfNull(send);
        ct.ThrowIfCancellationRequested();

        if (IsUnbounded(timeout))
        {
            await send(ct).ConfigureAwait(false);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var token = timeoutCts.Token;

        var writing = send(token);
        var guard = Task.Delay(timeout, token);

        if (await Task.WhenAny(writing, guard).ConfigureAwait(false) != writing)
        {
            ObserveFault(writing);
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            var msg = $"Worker {what} 超时({timeout.TotalSeconds:0.#}s)";
            Log.Warn(Category, msg);
            throw new TimeoutException(msg);
        }

        await writing.ConfigureAwait(false);
    }

    /// <summary>断线/释放时统一终结在飞等待(照抄 <c>McpClientBase.FailAllPending</c> 的做法)。
    ///
    /// <para>这是「绝不让引擎永久等待」这条硬约束的落点: 读循环 EOF、进程退出、写侧失败,
    /// 三条路径的收尾都必须经过这里, 且<b>至多一次</b>(靠 <c>TrySetException</c> 保证幂等)。</para>
    ///
    /// <para>⚠️ 必须传<b>快照</b>(传输在锁内 <c>Pending.Values.ToList()</c> 后再传进来):
    /// 一边遍历字典一边被响应派发路径摘除条目会抛 <c>InvalidOperationException</c>,
    /// 而那正好发生在「处理崩溃」的时刻, 会把崩溃处理本身再崩一次。</para>
    /// </summary>
    public static void FailAllPending<T>(IEnumerable<TaskCompletionSource<T>> pending, string reason)
    {
        ArgumentNullException.ThrowIfNull(pending);
        foreach (var tcs in pending)
        {
            tcs.TrySetException(new InvalidOperationException(reason));
        }
    }

    /// <summary>让一个已经没人等待的任务的异常变成「已观察」, 避免 UnobservedTaskException 噪声。</summary>
    private static void ObserveFault(Task task)
        => _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
}