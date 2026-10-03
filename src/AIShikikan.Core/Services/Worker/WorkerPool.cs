using System.Collections.Concurrent;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Session;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// 构造一个<b>进程内降级</b>传输(<see cref="InlineTransport"/>)。
/// </summary>
/// <param name="sessionId">会话 Id(工具的 <c>ToolContext.SessionId</c> 与 Plan 授权查表都要它)。</param>
/// <param name="workDir">会话工作目录(工具解析相对路径的根)。</param>
/// <param name="workspaceRoot">已解析的工作树根(执行权隔离与检查点目录按它建键)。</param>
/// <param name="commanderPersonaText">本回合指挥官人格快照; null/空串 = 本回合无人格。</param>
/// <param name="rosterEntries">
/// 会话级 roster 快照。<b>null 与空表语义不同</b>(null = 无限制 / 空表 = 用户已清空),
/// 委托实现方必须原样透传, 不要用 <c>Count &gt; 0</c> 归一(见 <c>ToolContext.RosterEntries</c> 的说明)。
/// </param>
/// <param name="planMode">本回合是否处于 Plan 模式。</param>
/// <remarks>
/// <b>为什么由外部注入而不是池内 new</b>: <see cref="InlineTransport"/> 只需要两个委托
/// (<see cref="InlineToolExecutor"/> / <see cref="InlineToolsLister"/>), 而它们要读
/// <c>ToolRegistry</c> 与本会话的 <c>ToolContext</c> —— 那是 <c>CommanderRuntime</c> 的领地。
/// 池刻意不反向引用工具层, 否则 <c>Services.Worker</c> 会把整个工具层拽进 Worker 进程的依赖图
/// (与 <see cref="InlineToolExecutor"/> 选委托而非接口是同一条理由)。
/// </remarks>
public delegate IToolTransport InlineTransportFactory(
    string sessionId,
    string workDir,
    string workspaceRoot,
    string? commanderPersonaText,
    IReadOnlyList<AgentRosterEntry>? rosterEntries,
    bool planMode);

/// <summary>
/// 构造一个<b>子进程管道</b>传输(<c>PipeTransport</c>)。
/// </summary>
/// <param name="executablePath"><see cref="WorkerLocationResult.ExecutablePath"/>, 已确认非空。</param>
/// <param name="hello">
/// 会话身份(<see cref="WorkerHelloRequest"/>)。它同时承担两个角色: 子进程的工作目录、
/// Worker key 的计算输入, 以及稍后由 <c>worker/hello</c> 下发的握手载荷。
/// <para>⚠️ <b>必须与池里那个 <c>workerKey</c> 同源</b>: 池算 key 用
/// <c>MakeWorkerKey(workDir, sessionId)</c>, <c>PipeTransport</c> 内部也算同一个 —— 两处都是同一个
/// 函数、同一份输入, 所以必然一致。但前提是这里传进去的 <c>hello.WorkDir</c> 与 <c>hello.SessionId</c>
/// 就是池算 key 时用的那两个值, 不能被顺手改写。</para></param>
/// <remarks>
/// <b>为什么把 <see cref="WorkerHelloRequest"/> 交给工厂而不是各传各的字段</b>:
/// 它同时是"启动上下文"(工作目录)与"握手载荷", 两份由同一个对象派生才不会在改动中分叉;
/// 也因此工厂<b>不能</b>缓存它 —— <c>IsPlanMode</c> 等字段每回合可变。</remarks>
public delegate IToolTransport PipeTransportFactory(
    string executablePath,
    WorkerHelloRequest hello);

/// <summary>
/// <see cref="WorkerPool"/> 的可调参数。默认值即产品要求的默认行为, 绝大多数情况<b>不需要</b>改动。
/// </summary>
public sealed class WorkerPoolOptions
{
    /// <summary>默认参数。</summary>
    public static WorkerPoolOptions Default => new();

    /// <summary>
    /// 目录对账周期。默认 60s; 设为 <see cref="TimeSpan.Zero"/> 或负数即关闭对账。
    /// </summary>
    /// <remarks>
    /// <b>为什么默认开着</b>: 它是 L3(「目录完全没有会话 → 关掉全部 Worker」)的<b>唯一防漏兜底</b>。
    /// 只要有任何一条路径漏掉引用计数 -1, 目录就永远不归零, Worker 进程一直残留,
    /// 而且没有任何现象提示(工具照常工作, 只是 <c>ps</c> 里多了一堆进程)。详见
    /// <see cref="WorkerDirectoryGroup.Reconcile"/> 的 remarks 里的「承重墙前提」。
    /// </remarks>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 连续拉起失败多少次后<b>永久</b>降级为进程内执行(对该 (目录, 会话) 粘滞, 直到它被 Release)。
    /// </summary>
    /// <remarks>
    /// <b>为什么是 3 而不是 1 或 10</b>: 1 次会把瞬时故障(Worker 启动瞬间撞上 <c>git</c> 锁、
    /// 进程创建瞬时失败)直接判成永久降级, 用户必须重开会话才恢复;
    /// 10 次意味着退避要走到 60s 上限, 那一分多钟里用户一直在无隔离状态下运行而不自知。
    /// 3 次 + 2s/4s 的退避刚好覆盖「瞬时抖动」与「稳定失败」两类, 总代价约 6s。</remarks>
    public int MaxSpawnAttemptsBeforeInline { get; init; } = 3;

    /// <summary>重拉退避的起始时长。默认 2s。</summary>
    public TimeSpan RespawnBackoffBase { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>重拉退避的封顶时长。默认 60s。</summary>
    public TimeSpan RespawnBackoffCap { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 空闲超时: **连续 1 分钟没有任何任务**就关掉该 (目录, 会话) 的 Worker。默认 1 分钟。
    /// </summary>
    /// <remarks>
    /// <para><b>与 L3（会话数归零即关闭）是叠加关系，不是替代</b>：L3 管「没人要的目录」，
    /// 本项管「有人要但一直闲着的 Worker」。两者都在时，先命中哪个关哪个。</para>
    ///
    /// <para><b>为什么要有这一条（性能）</b>：每个 Worker 是一份独立的 Core 运行时
    /// （含 JIT 后的代码页、常驻堆、以及 AOT 下约 12MB 的可执行映像）。开着十个会话摆着不动
    /// 就是十份常驻内存；而空闲的 Worker 既不产出价值，又持有工作目录的 git 上下文。
    /// 空闲就关是纯收益 —— 下次调用本来就是懒重建，重建成本远低于长期占着的内存。</para>
    ///
    /// <para><b>空闲判据是合取式，这一条是正确性底线</b>：
    /// <c>ActiveCallCount == 0</c>（<b>没有在飞工具调用</b>）**且**
    /// 距上次活动 ≥ 本值。只看后者会在子代理跑到 1 分钟时误杀它 ——
    /// 子代理工具合法跑 30 分钟，被杀会让子代理进程变孤儿、并拖到超时才释放 git 写锁。</para>
    ///
    /// <para><b>取值 1 分钟的依据</b>：短到能真正省下内存（分钟级而非十分钟级），
    /// 长到不会在正常使用的间隙里反复重建 —— 一次重新拉起约 100~300ms，
    /// 而"用户想了一下措辞再发下一条"通常超过 1 分钟。更短（如 15s）会让密集多轮对话
    /// 每个回合都付一次重建；更长（如 10min）则空闲回收形同虚设。</para>
    ///
    /// <para><b>置 <see cref="TimeSpan.Zero"/> 即完全关闭</b>（退回到"只有 L3 回收"）。</para></remarks>
    public TimeSpan DirectoryIdleTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 单次 <c>AcquireAsync</c> 内因「组退役 / 在飞拉起被取消」而重新决策的最大次数。
    /// 用尽后仍失败则退回进程内执行 —— <b>绝不抛异常</b>。
    /// </summary>
    /// <remarks>
    /// 生命周期代码抛异常会一路冒到引擎的工具执行里, 变成用户可见的「这一回合失败了」;
    /// 而这里所有的失败模式(组竞态 / 取消传播)都只影响「用不用得上隔离」, 不影响「工具能不能跑」。</remarks>
    public int MaxAcquireAttempts { get; init; } = 3;

    /// <summary>
    /// 是否允许拉起子进程。设为 false 时一切 Acquire 都直接走进程内执行。
    /// </summary>
    /// <remarks>
    /// 用途是 <c>IToolTransport</c> 类注释里说的「分步验证」: 先在不引入进程的前提下
    /// 跑通协议编解码与全部工具(doctor 的自检场景)。</remarks>
    public bool PipeEnabled { get; init; } = true;
}

/// <summary>
/// Worker 进程池: 按「工作目录 × 会话」懒启动与回收独立进程, 在崩溃/僵死后重拉,
/// 并在拉不起来时降级为进程内执行(且让降级可见)。
/// </summary>
/// <remarks>
/// <para><b>实例粒度 = (工作目录 × 会话)</b>, 对应 worker-architecture.md 4.2。
/// 分组键是 <c>GitWorkspaceResolver.Normalize(workDir)</c> 的结果, 与
/// <see cref="WorkerProtocol.MakeWorkerKey"/> 内部那一次规范化<b>同口径</b> ——
/// 两处各规范化一次是允许的, 但<b>必须用同一条路径</b>, 否则 Worker 与父进程会算出两个不同的键,
/// 后果是路由/缓存全部错位(父进程认不出自己 spawn 的 Worker)。</para>
///
/// <para><b>硬生命周期规则(L1–L6, 全部来自用户决策)</b>:
/// <list type="number">
/// <item><b>L1 懒启动</b>: 只有 <see cref="AcquireAsync"/> 被调用(即真的要用工具)时才 spawn。不做预热。</item>
/// <item><b>L2/L3 目录级存活</b>: 某目录下会话引用计数 ≥ 1 → 存活; <b>完全没有会话 → 关闭该目录下全部 Worker</b>。</item>
/// <item><b>L4 换目录</b>: 会话 WorkDir 变更 → <see cref="Release"/> 旧目录(<c>-1</c>)、下一次
/// <c>AcquireAsync</c> 自动挂进新目录。</item>
/// <item><b>L5 崩溃重拉</b>: 订阅 <see cref="WorkerClient.Faulted"/>, <b>只重拉那一个 (目录, 会话)</b>,
/// 其它 Worker 一概不动。</item>
/// <item><b>L6 空闲回收</b>: 某个 (目录, 会话) 的 Worker <b>连续 1 分钟没有任何任务</b>就关掉 ——
/// <b>默认 1 分钟、已启用</b>(见 <see cref="WorkerPoolOptions.DirectoryIdleTimeout"/>)。
/// 与 L3 叠加而非替代: L3 管「没人要的目录」(整组退役), L6 管「有人要但一直闲着的 Worker」
/// (只摘句柄、保留槽位, 下次调用懒重建)。判据是「无在飞调用」<b>且</b>「距上次活动 ≥ 阈值」——
/// 第一条是正确性底线(子代理合法跑 30 分钟, 只看时间会在第 1 分钟把它误杀)。</item>
/// </list></para>
///
/// <para><b>⚠️ 并发同步方案: 按目录分组 + 每组一把短锁, 绝不用一把全局锁。</b>
/// <list type="bullet">
/// <item>全局锁会把「A 目录的 Worker 握手慢」传导成「B 目录的新会话也起不来」。
/// <see cref="WorkerProtocol.HandshakeTimeout"/> 是 30s —— 最坏情形下用户在 B 目录点发送,
/// 要等 30s 之后才看到工具出现, 而原因在另一个毫不相干的目录里。<b>这是全局锁唯一但致命的问题</b>,
/// 也是本类所有同步设计围绕的唯一目标。</item>
/// <item>实际用的锁是 <see cref="WorkerDirectoryGroup"/> 内部的 <c>_gate</c>, 只保护字典读写, <b>纳秒级</b>。
/// spawn + 握手一律在锁外: 因此「同目录的 3 个新会话」可以真正并行地拉起 3 个进程。</item>
/// <item>组字典本身用 <see cref="ConcurrentDictionary{TKey,TValue}"/>: 组本身几乎不冲突
/// (增删只在 L3 归零与 L4 换目录时发生), 真正的冲突在组<b>内部</b>的槽位上, 那由 <c>_gate</c> 管。</item>
/// <item>「一个会话的并发 Acquire 不得起两个进程」由槽位里的在飞 <c>TaskCompletionSource</c> 汇合解决
/// (见 <see cref="WorkerDirectoryGroup.BeginAcquire"/> 的 ②)。没有它, <c>SubagentGroupTool</c>
/// 的 4 并发闸会让同一会话瞬间起 4 个 Worker。</item>
/// </list></para>
///
/// <para><b>⚠️ 退避策略: 指数退避 2s → 4s → … → 60s 封顶, ±20% 抖动, 计数上限 3 次后永久降级。</b>
/// <b>理由(逐条)</b>:
/// <list type="number">
/// <item><b>为什么必须有</b>: 不退避时「Worker 起不来 → 立刻重拉 → 又起不来」是一个
/// <b>CPU 与日志双重打爆</b>的循环: 每次失败都要真的 fork 一个进程、跑一遍握手(还会失败),
/// 并且写两条日志。而失败的根因(产物缺失 / 版本不符 / 权限不足)通常在用户下一次干预之前<b>不会自己变好</b>。</item>
/// <item><b>为什么退避只约束"再次拉起"、不阻塞"这一回合用工具"</b>: 等待退避窗口意味着把这一回合的工具调用挂起最多 60s,
/// 用户看到的是「点了发送没反应」。所以退避期间 <c>AcquireAsync</c> <b>立刻</b>返回进程内句柄 ——
/// 这一回合照常有工具可用, 只是没有隔离。这与「降级必须可见」不冲突: 它在 doctor/状态栏里是显式的。</item>
/// <item><b>为什么指数</b>: 固定间隔对「短暂的资源争用」不够(2s 后可能仍在争用), 对「需要人工干预的故障」太吵
/// (每 2s 一条日志, 一小时 1800 条)。指数是这两者之间唯一同时满足的形状。</item>
/// <item><b>为什么 60s 封顶</b>: 再往上就没有实际收益 —— 用户不会在 2 分钟后重试同一个会话,
/// 而 120s+ 的间隔会让「Worker 被 transient 地搞挂」这类本来能自愈的情况失去自愈机会。</item>
/// <item><b>为什么要 ±20% 抖动</b>: 同一目录的 N 个会话同时崩溃时, 无抖动的指数退避会让它们<b>在同一毫秒</b>集体重拉,
/// 退避就从「削峰」变成了「制造尖峰」。抖动让重拉摊开成一小波。</item>
/// <item><b>为什么两次终局(非 terminal)失败后不无限重试</b>: 见 <see cref="WorkerPoolOptions.MaxSpawnAttemptsBeforeInline"/>。
/// 关键性质是<b>降级要可见且可归因</b> —— 一条 <c>Log.Warn</c> + doctor 一行 + 状态栏一个标记。</item>
/// </list></para>
///
/// <para><b>⚠️ 重连 vs 复用: 已在飞的工具调用不因重拉而失败。</b>
/// <see cref="WorkerClient.Faulted"/> 到达时, 本类只做两件事 —— 记账 + 把死句柄从槽位里摘下,
/// 并把它的释放<b>推迟到后台</b>(见 <c>ScheduleDisposalAsync</c> 的 remarks)。新 Worker 是一个
/// <b>全新对象</b>, 老句柄的归属仍在它自己手里(它自己的 <c>IToolTransport</c> 负责把在飞请求
/// 完成为软失败)。因此「重拉」只影响<b>后续</b>调用, 不会额外打断任何在飞调用。</para>
///
/// <para><b>⚠️ 降级必须可见</b>(本类最需要被小心实现的一处):
/// 降级不是故障, 工具照常工作、界面照常正常 —— 这正是它危险的地方:
/// 子代理又回到主进程、<c>grep</c> 又能卡 UI、git 又在 UI 线程排队, 用户<b>没有任何线索</b>。
/// 因此每次「刚刚降级」都会打一条带定位来源与已尝试路径的 <c>Log.Warn</c>,
/// 并由 <see cref="WorkerHealth"/> 同时喂给 doctor 与状态栏。</para>
/// </remarks>
public sealed class WorkerPool : IAsyncDisposable
{
    private const string LogCategory = "Worker";

    /// <summary>重新定位 Worker 可执行文件的最小间隔。</summary>
    /// <remarks>
    /// <see cref="WorkerLocator.Locate"/> 会做若干次 stat, 候选 4 还要向上最多 12 层找
    /// <c>AIShikikan.slnx</c>。状态栏若按秒轮询 <see cref="GetHealth"/>, 没有这个节流就会变成
    /// 每秒一次的全量探测。5s 足够短 —— 用户 <c>./build.sh</c> 之后回到界面上, 5s 内就会看到状态翻转。</remarks>
    private const long RelocateCooldownMs = 5_000;

    /// <summary>
    /// 收到 <see cref="WorkerClient.Faulted"/> 之后, 死句柄在后台被释放前的等待。
    /// </summary>
    /// <remarks>
    /// <b>绝不能在 <c>Faulted</c> 回调里同步释放</b>: 该事件跑在传输自己的读循环线程上,
    /// 而 <c>DisposeAsync</c> 很可能要等那条读循环收尾 —— 于是变成"回调线程等自己", 经典自死锁。
    /// 这一点必须靠「推迟到后台」解决, 不能靠 catch。</remarks>
    private const int FaultDisposalGraceMs = 250;

    /// <summary>
    /// 目录组比较器口径的自检提示串: 分组键的比较策略一旦与
    /// <c>GitWorkspaceResolver.PathComparer</c> 漂移, 同一目录会被算成两个组。
    /// </summary>
    private const string ComparerDriftHint =
        "目录组比较器与 GitWorkspaceResolver.PathComparer 的平台策略必须一致, 否则同一目录会被分裂成多个组";

    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly InlineTransportFactory _inlineFactory;
    private readonly PipeTransportFactory _pipeFactory;
    private readonly Func<IReadOnlySet<string>?>? _liveSessionProvider;
    private readonly WorkerPoolOptions _options;

    private readonly ConcurrentDictionary<string, WorkerDirectoryGroup> _groups =
        new(WorkerDirectoryGroup.PathComparer);

    private readonly object _locationGate = new();
    private WorkerLocationResult? _location;
    private long _relocateAfterTicks;

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _reconcileTask;

    private int _disposed;

    /// <summary>
    /// 构造池。
    /// </summary>
    /// <param name="workspaceResolver">
    /// 工作区解析器, 用于给 <see cref="WorkerHelloRequest.WorkspaceRoot"/> 取工作树根。
    /// 用 <c>CommanderRuntime.Boot</c> 里<b>同一个</b> <c>GitWorkspaceResolver</c> 实例 ——
    /// 它带 2s TTL 缓存, 而每次 spawn 都要解析一次目录。
    /// </param>
    /// <param name="inlineFactory">进程内降级传输的构造委托(见 <see cref="InlineTransportFactory"/>)。</param>
    /// <param name="pipeFactory">
    /// 子进程管道传输的构造委托。默认使用 <see cref="DefaultPipeFactory"/>(直接 <c>new PipeTransport</c>)。
    /// 自检 / 测试可注入假实现。
    /// </param>
    /// <param name="liveSessionProvider">
    /// 「当前实际存在的会话 Id 集合」的提供方, 对账的基准来源。
    /// <b>必须接</b>(接线片段见类型 remarks): 传 null 会关闭对账, 于是 L3 完全依赖每一次
    /// <see cref="Release"/> 都被正确调用 —— 而 GUI 删除会话 / 主进程退出这两条路径目前都没有接。
    /// </param>
    /// <param name="options">可调参数; 为 null 时用 <see cref="WorkerPoolOptions.Default"/>。</param>
    public WorkerPool(
        IWorkspaceResolver workspaceResolver,
        InlineTransportFactory inlineFactory,
        PipeTransportFactory? pipeFactory = null,
        Func<IReadOnlySet<string>?>? liveSessionProvider = null,
        WorkerPoolOptions? options = null)
    {
        _workspaceResolver = workspaceResolver ?? throw new ArgumentNullException(nameof(workspaceResolver));
        _inlineFactory = inlineFactory ?? throw new ArgumentNullException(nameof(inlineFactory));
        _pipeFactory = pipeFactory ?? DefaultPipeFactory;
        _liveSessionProvider = liveSessionProvider;
        _options = options ?? WorkerPoolOptions.Default;

        if (_options.MaxSpawnAttemptsBeforeInline < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSpawnAttemptsBeforeInline 至少为 1: 为 0 会让每个会话第一次拉起失败就永久降级");
        }

        StartReconcileLoop();
    }

    // ── 公开 API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 取得(必要时拉起)该 (工作目录 × 会话) 对应的 Worker 连接句柄。
    /// </summary>
    /// <param name="sessionId">会话 Id。参与 <see cref="WorkerProtocol.MakeWorkerKey"/> 的身份计算。</param>
    /// <param name="workDir">会话工作目录。内部先规范化再用于分组。</param>
    /// <param name="commanderPersonaText">本回合指挥官人格快照(只在需要拉起时用上; 复用已有 Worker 时忽略)。</param>
    /// <param name="rosterEntries">会话级 roster 快照(同上)。</param>
    /// <param name="planMode">本回合是否处于 Plan 模式(同上)。</param>
    /// <param name="ct">取消令牌。除调用方取消外, 本方法<b>不抛任何异常</b> —— 见 <see cref="WorkerPool"/> remarks。</param>
    /// <remarks>
    /// <para><b>返回的句柄一定是「可用的」</b>: 要么是已握手的管道连接, 要么是一个内联句柄
    /// (<c>InlineTransport.IsConnected</c> 恒为 true)。唯一例外是「拉起来之后会话已被 Release /
    /// 组已退役」这条竞态, 此时返回的是一个已关闭的句柄, 由 <see cref="WorkerProxyTool"/> 的
    /// 前置连通性检查转成一条明确的工具错误。</para>
    ///
    /// <para><b>⚠️ 取消语义</b>: <paramref name="ct"/> 取消时本方法抛
    /// <see cref="OperationCanceledException"/>, 且已经拉起一半的 Worker 进程会被立即关闭
    /// (否则会留下占着仓库 git 锁的孤儿)。代价是同会话其它汇合到同一次拉起的 Acquire 会一起被取消 ——
    /// 刻意接受: 拉起本身被 <see cref="WorkerProtocol.HandshakeTimeout"/> 兜住, 代价有界;
    /// 而"让取消只影响自己"需要在 <see cref="WorkerDirectoryGroup"/> 里再维护一套共享取消的仲裁。</para>
    ///
    /// <para><b>用法</b>: 每一回合在需要工具时调一次(可以是同一回合的多次; 第二次起是纯复用路径)。
    /// 会话结束时必须调 <see cref="Release"/>, 否则目录永不归零(见类型 remarks 的 reconcile 说明)。</para>
    /// </remarks>
    public async Task<WorkerClient> AcquireAsync(
        string sessionId,
        string workDir,
        string? commanderPersonaText,
        IReadOnlyList<AgentRosterEntry>? rosterEntries,
        bool planMode,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        ct.ThrowIfCancellationRequested();

        // 目录键必须规范化: 同一个目录会以 /repo、/repo/、/repo/./sub/.. 三种形态出现
        // (引擎 Options.WorkDir / 会话配置 / 用户手输)。不规范化就会分裂成多个组,
        // 于是每个组都自认"我没有会话"而被 L3 关闭 —— 表现是 Worker 反复重启, 而计数表看起来正常。
        var dirKey = GitWorkspaceResolver.Normalize(workDir);
        var workerKey = WorkerProtocol.MakeWorkerKey(workDir, sessionId);
        var now = Environment.TickCount64;

        if (Volatile.Read(ref _disposed) != 0)
        {
            // 池已关闭(应用正在退出)。刻意不抛 ObjectDisposedException: 那会一路冒到引擎的
            // 工具执行里, 把"进程正在退出"变成用户可见的回合失败。退回内联是零风险的收尾。
            Log.Debug(LogCategory, $"池已关闭, 会话 {sessionId} 的工具退回进程内执行");
            return CreateInlineClient(sessionId, dirKey, ResolveWorkspaceRoot(dirKey),
                commanderPersonaText, rosterEntries, planMode, "Worker 池已关闭");
        }

        // WorkspaceRoot 每次都算: GitWorkspaceResolver 有 2s TTL 缓存, 而这里的调用频率是
        // "每次需要工具的回合", 摊薄后每 2s 至多 2 个 git 进程(与 WorkspaceExecutionCoordinator 同款代价)。
        var workspaceRoot = ResolveWorkspaceRoot(dirKey);

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var group = GetOrAddGroup(dirKey);

            // Attach 表达"这个会话在本目录有一份引用"(L2 的计数), 必须在每次 Acquire 幂等调用。
            if (!group.Attach(sessionId, dirKey, now)) continue; // 组正在退役 → 换一个组实例重试

            var plan = group.BeginAcquire(sessionId, now);
            switch (plan.State)
            {
                case WorkerAcquireState.GroupRetired:
                    continue;

                case WorkerAcquireState.Reuse:
                    return plan.Client!;

                case WorkerAcquireState.Join:
                    try
                    {
                        return await JoinInFlightAsync(plan.InFlight!, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 发起那次拉起的调用方被取消了(它会同时清掉在飞登记),
                        // 重新决策一次 —— 自己的令牌没被取消, 所以这次通常能拿到结果。
                        if (attempt >= _options.MaxAcquireAttempts) break;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        // 在飞任务理论上不会带异常(失败时我们会发布内联句柄而不是让任务失败),
                        // 真发生了也不能把回合带崩: 退回内联。
                        Log.Warn(LogCategory, ex, $"等待 Worker 拉起失败(会话 {sessionId}), 退回进程内执行");
                        return CreateInlineClient(sessionId, dirKey, workspaceRoot,
                            commanderPersonaText, rosterEntries, planMode, "等待 Worker 拉起失败");
                    }

                case WorkerAcquireState.Degrade:
                    Log.Debug(LogCategory,
                        $"会话 {sessionId}(目录 {dirKey})本次不拉起 Worker, 退回进程内执行: {plan.Reason}");
                    return CreateInlineClient(sessionId, dirKey, workspaceRoot,
                        commanderPersonaText, rosterEntries, planMode, plan.Reason ?? "已降级");

                case WorkerAcquireState.Proceed:
                    return await SpawnAndPublishAsync(
                        group, plan.Completion!, sessionId, workerKey, dirKey, workspaceRoot,
                        commanderPersonaText, rosterEntries, planMode, ct).ConfigureAwait(false);
            }

            if (attempt >= _options.MaxAcquireAttempts)
            {
                // 到达这里只可能是「组反复退役」这一种情况(理论不可达: 第 2 轮必然拿到全新组实例)。
                // 退回内联而不是抛异常, 与本类型「生命周期不抛」的纪律一致。
                Log.Warn(LogCategory, $"目录 {dirKey} 的 Worker 组反复退役(重试 {attempt} 次), 会话 {sessionId} 退回进程内执行");
                return CreateInlineClient(sessionId, dirKey, workspaceRoot,
                    commanderPersonaText, rosterEntries, planMode, "目录组反复退役");
            }
        }
    }

    /// <summary>
    /// 引用计数 -1。计数归零则关闭该目录下<b>全部</b> Worker(L3)。
    /// </summary>
    /// <param name="sessionId">会话 Id。</param>
    /// <param name="workDir">
    /// 会话当前的工作目录。<b>可传空/陈旧值</b>: 找不到对应组时会在全部组里按会话 Id 搜一遍 ——
    /// 因为 L4(换目录)之后调用方手里的目录可能已经不准了, 而漏掉一次 -1 的代价是进程永久残留。
    /// </param>
    /// <remarks>
    /// <para><b>刻意不抛异常</b>: 它由 GUI 的会话删除回调与应用退出路径调用, 那里没有恢复余地;
    /// 一个参数异常就能把「关闭该目录全部 Worker」整条跳过 —— 泄漏的恰恰是用户以为已经关掉的那些进程。</para>
    ///
    /// <para><b>⚠️ 必须接线</b>: GUI 删除会话 → <c>Release(id, 该会话的 workDir)</c>;
    /// 主进程退出 → <c>ShutdownAllAsync</c>。两者目前都<b>没有</b>生产调用方
    /// (<c>SessionRuntimeRegistry.RemoveSession</c> / <c>Dispose</c> 同为零调用方),
    /// 所以本方法的兜底是每 60s 的 reconcile(见 <see cref="ReconcileNowAsync"/>)。</para>
    /// </remarks>
    public void Release(string sessionId, string workDir)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var group = ResolveGroupForRelease(sessionId, workDir);
        if (group is null) return;

        if (group.Release(sessionId, out var detached) && detached is not null)
        {
            _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { detached }, CancellationToken.None);
        }

        // L3: 目录内已无任何会话 → 关闭本组全部 Worker。
        if (group.SessionCount == 0)
        {
            RetireGroup(group);
        }
    }

    /// <summary>
    /// 标记某个会话「有一个引擎回合正在进行」。回合期间该会话的 Worker **不参与空闲回收**。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它（纯性能，不是正确性）</b>：空闲判据里"没有在飞工具调用"
    /// 这一条在回合的 LLM 流式阶段恒为真 —— 那段时间根本没有工具在跑。
    /// 若不通知本方法，一个正在吐字的回合会在两次工具调用之间的空档被当成空闲，
    /// 于是这个回合的**下一个**工具调用要额外付一次重新拉起（约 100~300ms）。
    /// 空闲阈值 1 分钟恰好大于绝大多数单回合耗时，所以正常情况下不会命中；
    /// 但长上下文 + 慢模型的长回合会命中，故留这个口。</para>
    ///
    /// <para><b>⚠️ 调用方必须保证成对</b>（<c>try/finally</c>）。漏掉 <c>active: false</c> 会让
    /// 该会话的 Worker 永远不参与空闲回收 —— 表现为"有的会话的 Worker 关不掉"，
    /// 而这个偏差极难定位（它是按会话的，只有一个会话出问题）。
    /// 重复置 <c>true</c> 是幂等的；重复置 <c>false</c> 同理。</para>
    ///
    /// <para>本方法<b>不抛异常</b>、也不创建任何东西：会话尚未 <c>Acquire</c> 过时会直接返回
    /// （那时还没有 Worker，收不回收都无所谓）。</para>
    /// </remarks>
    /// <param name="sessionId">会话 Id。</param>
    /// <param name="workDir">会话当前工作目录；解析不到就退化为按会话 Id 全组搜。</param>
    /// <param name="active">true = 回合进行中；false = 回合已结束。</param>
    public void MarkTurnActive(string sessionId, string workDir, bool active)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var group = ResolveGroupForRelease(sessionId, workDir);
        group?.MarkTurnActive(sessionId, active);
    }

    /// <summary>
    /// 生成池的可展示快照, 给 doctor 与状态栏。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ 本方法会(必要时)触发一次 <see cref="WorkerLocator.Locate"/> 探测</b> ——
    /// doctor 必须能看到"就算还没开过会话, Worker 到底找没找到"。有了 5s 节流, 状态栏按秒轮询的
    /// 实际代价是每 5 秒一次全量探测(若干次 stat)。</para>
    ///
    /// <para>返回的是<b>锁内构造的一致快照</b>, 之后不再触碰池状态, 因此可以安全地直接交给 UI 线程。</para>
    /// </remarks>
    public WorkerHealth GetHealth()
    {
        var location = EnsureLocation();

        var entries = new List<WorkerHealthEntry>();
        var restarts = 0;
        foreach (var kv in _groups)
        {
            entries.AddRange(kv.Value.SnapshotHealth());
            restarts += kv.Value.RestartsTotal;
        }

        return new WorkerHealth(ComputeMode(location, entries), location, entries, restarts);
    }

    /// <summary>
    /// 关闭全部目录的全部 Worker(主进程退出时用)。幂等。
    /// </summary>
    /// <param name="ct">取消令牌; 取消只让「等待关闭完成」提前结束, 每个 Worker 自身仍有
    /// <see cref="WorkerProtocol.ShutdownTimeout"/> 的独立闸门 —— 否则取消一次就会留下全部进程。</param>
    /// <remarks>
    /// <para><b>⚠️ L3 的对偶动作</b>: 进程退出会一次性干掉所有目录的所有 Worker, 因此它同时也是
    /// 「<c>SessionRuntimeRegistry.Dispose</c> 没有生产调用方」这条缺口的兜底 ——
    /// 接上它, 主进程退出就不依赖每个会话都被正确 -1。</para>
    ///
    /// <para><b>为什么扫多轮</b>: 清空字典与「另一个线程刚好 <c>GetOrAdd</c>」之间存在窗口。
    /// 单轮清空会漏掉那一组, 于是应用退出后残留一个 Worker 进程(它靠父进程 pid 自检才肯退出,
    /// 而那要等 <see cref="WorkerProtocol.HeartbeatTimeout"/> 量级)。多扫几轮到字典为空即可覆盖。</para>
    /// </remarks>
    public async ValueTask ShutdownAllAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _disposed, 1);
        StopReconcileLoop();

        // 每一组内部已各自吞掉全部异常(见 CloseOneAsync), 所以这里只等"关完", 不处理异常。
        for (var sweep = 0; sweep < 8 && !_groups.IsEmpty; sweep++)
        {
            var snapshot = _groups.ToArray();
            _groups.Clear();

            var tasks = new List<Task>(snapshot.Length);
            foreach (var kv in snapshot)
            {
                var clients = kv.Value.BeginRetire();
                if (clients.Length == 0) continue;
                tasks.Add(WorkerDirectoryGroup.CloseClientsAwaitedAsync(clients, ct));
            }

            if (tasks.Count > 0) await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 立刻与「当前实际存在的会话集合」对账一次(定时器之外的按需触发口, 例如 doctor)。
    /// </summary>
    /// <remarks>
    /// <b>拿不到比对基准时静默跳过本轮</b>: 误回收(把活着的会话的 Worker 关掉, 打断正在跑的子代理)
    /// 比不回收糟糕得多 —— 前者破坏用户的活儿, 后者只是让进程多留一会儿, 而下一轮对账就会补上。</remarks>
    public async ValueTask ReconcileNowAsync(CancellationToken ct = default)
    {
        var live = TryGetLiveSessions();
        if (live is null) return;

        var reclaimed = new List<string>();
        var toClose = new List<WorkerClient>();
        var idleGroups = new List<WorkerDirectoryGroup>();
        var idleClients = new List<WorkerClient>();
        var now = Environment.TickCount64;

        foreach (var kv in _groups.ToArray())
        {
            var group = kv.Value;

            foreach (var r in group.Reconcile(live))
            {
                reclaimed.Add(r.SessionId);
                toClose.Add(r.Client);
            }

            // ── 槽位级空闲回收 ──
            // 摘句柄但**保留槽位**, 所以会话仍登记在本组里: 下次 AcquireAsync 走懒重建,
            // 既不会把会话当成外来者重新挂载, 也不会让这个目录被误判成"没人要"而退役。
            if (IsIdleTimeoutArmed)
            {
                foreach (var client in group.DetachIdleClients(_options.DirectoryIdleTimeout, now))
                {
                    idleClients.Add(client);
                }
            }

            if (group.SessionCount == 0)
            {
                // L3 的兜底: 计数归零但没走过 Release(漏计数)时, 对账负责收尾。
                // 会话已归零 → 整组退役(目录级关闭), 不再等空闲超时 ——
                // "没有任何会话要这个目录"是比"空闲"更强的信号, 没有等待的理由。
                idleGroups.Add(group);
            }
        }

        if (reclaimed.Count > 0)
        {
            Log.Info(LogCategory,
                $"目录对账回收了 {reclaimed.Count} 个已消失会话的 Worker: {string.Join(", ", reclaimed)}");
        }

        if (idleClients.Count > 0)
        {
            // 记数量而不逐个记日志: 空闲回收是常规行为(不是异常), 但也不能完全静默 ——
            // 用户报"我的 Worker 怎么老重启"时, 这一行就是唯一的线索。
            Log.Info(LogCategory,
                $"空闲超过 {_options.DirectoryIdleTimeout.TotalSeconds:0}s, 关闭 {idleClients.Count} 个 Worker " +
                "(无在飞调用; 下次用到时按需重新拉起)");
        }

        // 空闲摘下的句柄与对账摘下的一起关: 并发关闭, 单个失败不影响其余(见 CloseClientsAsync 的 remarks)。
        toClose.AddRange(idleClients);
        foreach (var group in idleGroups) RetireGroup(group);
        if (toClose.Count > 0) await WorkerDirectoryGroup.CloseClientsAwaitedAsync(toClose, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 工具集全量同步的便捷封装(worker-architecture.md R8 的入口点): 发一条
    /// <c>worker/tools/sync</c> 并返回 Worker 侧的权威工具清单。
    /// </summary>
    /// <remarks>
    /// <b>为什么由池代发而不是各代理工具自己发</b>: R8 要求「sync 确认之后主进程才继续」,
    /// 而 sync 的载荷(CoreTools / SubagentVisible / PlanMode / AllowedAgentIds)是从主进程侧状态派生的,
    /// <b>同一回合内必须是同一份快照</b>。散落在各代理工具里发, 就会在回合中途出现"两次 sync 载荷不一致"。</remarks>
    public async Task<WorkerToolsResponse> SyncToolsAsync(
        WorkerClient client,
        bool coreTools,
        bool subagentVisible,
        bool planMode,
        IReadOnlyList<string>? allowedAgentIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new WorkerToolsSyncRequest
        {
            CoreTools = coreTools,
            SubagentVisible = subagentVisible,
            PlanMode = planMode,
            // 空表 = 不限制(不是"零授权"), 语义见 WorkerToolsSyncRequest.AllowedAgentIds 的 remarks
            AllowedAgentIds = allowedAgentIds ?? []
        };

        return await client.SyncToolsAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>当前已登记的目录组数(诊断用)。</summary>
    public int GroupCount => _groups.Count;

    /// <summary>
    /// 池内**当前持有句柄**的全部 Worker 快照(锁外拷贝, 返回后不再触碰池状态)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它</b>: 某些操作是<b>按池广播</b>而不是按单个句柄发起的
    /// (目前只有 <c>tools/sync</c>: 右侧栏可见性 / Plan 模式是全局开关, 必须让所有
    /// (目录 × 会话) 的 Worker 一起跟上)。而池内句柄存在<b>各目录组自己的槽位</b>里
    /// (<c>WorkerDirectoryGroup.Workers</c>), 组字典是私有的 —— 没有这个入口, 调用方
    /// 只能拿到 <see cref="GetHealth"/> 那份<b>纯数据</b>快照, 而它刻意不持有句柄
    /// (便于跨线程交给 UI), 于是广播无从发起。</para>
    /// <para><b>⚠️ 快照可能立刻过期</b>: 目录组会在 L3(引用计数归零)/ L4(换目录)/ 对账时
    /// 增删, 某个句柄也可能在枚举之后被摘下并关闭。所以调用方必须<b>逐个</b>判
    /// <see cref="WorkerClient.IsConnected</see> 并容忍失败 —— 把"句柄已经没了"当成
    /// "这一条跳过"即可, 不需要重试。</para>
    /// <para>刻意<b>不过滤</b>已断开/内联的句柄: "哪些算有效"是调用方的策略
    /// (例如广播可以只关心管道句柄, 而诊断则希望看到全部), 在这里替调用方决定会制造第二个口径。</para>
    /// </remarks>
    public IReadOnlyList<WorkerClient> EnumerateClients()
    {
        var list = new List<WorkerClient>();
        foreach (var kv in _groups)
        {
            // ConcurrentDictionary 的枚举是弱一致的, 而组自己的锁在 Workers 取值内部加:
            // 拿不到某个正在退役的组不该报错, 更不该阻断其余组的广播。
            try
            {
                list.AddRange(kv.Value.Workers);
            }
            catch (Exception ex)
            {
                Log.Debug(LogCategory, $"枚举目录 {kv.Key} 的 Worker 句柄失败, 已跳过该目录: {ex.Message}");
            }
        }

        return list;
    }

    /// <summary>
    /// 由 <see cref="SessionRuntimeRegistry.AllSessions"/> 生成对账基准集合。
    /// </summary>
    /// <remarks>
    /// <b>为什么包一层而不是让池直接依赖注册表</b>: 同一条理由 —— 池不该反向依赖会话层。
    /// 顺带说明该集合的语义: 它<b>只含「已按需创建」的运行时</b>, 历史会话列表里从未打开过的会话不在其中。
    /// 那类会话本来就没有 Worker, 所以不会造成误判。</remarks>
    public static IReadOnlySet<string> LiveSessionIdsFrom(IEnumerable<SessionRuntime> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            if (s is not null && !string.IsNullOrWhiteSpace(s.SessionId)) set.Add(s.SessionId);
        }

        return set;
    }

    // ── 默认管道工厂(⚠️ 与并行开发的 PipeTransport 的唯一耦合点) ──────────────

    /// <summary>
    /// 默认的管道传输构造: 直接 <c>PipeTransport.Start</c>。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ 与并行开发的 <c>PipeTransport</c> 的唯一耦合点, 实际签名如下</b>:</para>
    /// <code>public static PipeTransport Start(string executablePath, WorkerHelloRequest hello, string? logPrefix = null)</code>
    /// <para>它<b>同步</b>建进程与管道(内部没有任何 await 点), 且<b>不含握手</b> —— 握手由
    /// <see cref="WorkerClient.HandshakeAsync"/> 单独发, 因为那一帧里有每回合可变的字段(Plan 模式)。
    /// 它自己会用 <c>hello.WorkDir</c> + <c>hello.SessionId</c> 重算一遍 worker key 当日志前缀,
    /// 与池里那个 key <b>必然一致</b>(同一个 <see cref="WorkerProtocol.MakeWorkerKey"/>、同一份输入),
    /// 所以这里刻意不再额外传 workerKey / logPrefix —— 多传就多一个可能不一致的来源。</para>
    /// <para>本池<b>不</b>自己 fork 进程: 建进程、定 stdio 管道、管读循环、判心跳僵死全是
    /// <c>PipeTransport</c> 的职责。若实际签名再变, <b>只需要改下面这一行</b>, 其余代码零改动。</para>
    /// </remarks>
    private static IToolTransport DefaultPipeFactory(string executablePath, WorkerHelloRequest hello) =>
        PipeTransport.Start(executablePath, hello);

    // ── 内部实现 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 拉起 + 握手 + 发布。整个过程<b>不持有任何锁</b>(见类型的并发同步方案)。
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 必须完结 <paramref name="completion</b></b>: 同一会话的并发 <c>AcquireAsync</c>
    /// 正汇合在它身上(见 <see cref="WorkerDirectoryGroup.BeginAcquire"/> 的 ②)。
    /// 成功、降级、取消<b>三条路径都必须</b>完结它 —— 漏掉任何一条, 那些汇合者就会永远等下去,
    /// 表现是「同一会话的第二个工具调用卡到用户点停止」。
    /// </remarks>
    private async Task<WorkerClient> SpawnAndPublishAsync(
        WorkerDirectoryGroup group,
        TaskCompletionSource<WorkerClient> completion,
        string sessionId,
        string workerKey,
        string dirKey,
        string workspaceRoot,
        string? commanderPersonaText,
        IReadOnlyList<AgentRosterEntry>? rosterEntries,
        bool planMode,
        CancellationToken ct)
    {
        var now = Environment.TickCount64;

        SpawnOutcome outcome;
        try
        {
            outcome = await SpawnAsync(
                sessionId, workerKey, dirKey, workspaceRoot,
                commanderPersonaText, rosterEntries, planMode, group, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 铁律: 调用方取消原样上抛。SpawnAsync 已经把半成品 Worker 关掉了, 这里只补完汇合者。
            completion.TrySetCanceled(ct);
            throw;
        }

        if (outcome.Client is not null)
        {
            var connected = outcome.Client;
            var published = group.CompleteSpawn(sessionId, connected, outcome.ProcessId,
                out var superseded, now);

            if (superseded is not null)
            {
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { superseded }, CancellationToken.None);
            }

            if (!published)
            {
                // 竞态: 拉起来的过程中会话被 Release 或整组退役了。没人要这个进程, 立即关闭 ——
                // 绝不把一个"没人认领的 Worker"留在池里, 它会一直占着仓库的 git 写锁。
                Log.Info(LogCategory, $"Worker {workerKey} 拉起后才被释放, 立即关闭(会话 {sessionId})");
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
            }
            else
            {
                Log.Info(LogCategory,
                    $"Worker {workerKey} 已就绪(会话 {sessionId}, 目录 {dirKey}, pid={outcome.ProcessId?.ToString() ?? "?"})");
            }

            completion.TrySetResult(connected);
            return connected;
        }

        // ── 拉起失败: 降级为进程内执行 ───────────────────────────────────────
        var error = outcome.Error ?? "未知原因";
        var inline = CreateInlineClient(sessionId, dirKey, workspaceRoot,
            commanderPersonaText, rosterEntries, planMode, error);

        group.PublishInline(sessionId, inline, error, outcome.Terminal, BackoffMsFor,
            _options.MaxSpawnAttemptsBeforeInline, now,
            out var newlyDegraded, out var inlineSuperseded);

        if (inlineSuperseded is not null)
        {
            _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { inlineSuperseded }, CancellationToken.None);
        }

        if (newlyDegraded)
        {
            // ⚠️ 整个方案的风险兜底: 降级必须留一条带定位线索的 Warn。
            // 没有它, "悄悄退回单进程"就是一次用户看不见的性能回归。
            LogDegrade(sessionId, dirKey, error);
        }

        // 内联句柄对汇合者同样有效: 它们只是想要一个"能干活"的连接, 不该被区分对待。
        completion.TrySetResult(inline);
        return inline;
    }

    /// <summary>真正的拉起与握手。<b>除调用方取消外不抛异常</b>, 失败以 <see cref="SpawnOutcome"/> 返回。</summary>
    private async Task<SpawnOutcome> SpawnAsync(
        string sessionId,
        string workerKey,
        string workDir,
        string workspaceRoot,
        string? commanderPersonaText,
        IReadOnlyList<AgentRosterEntry>? rosterEntries,
        bool planMode,
        WorkerDirectoryGroup group,
        CancellationToken ct)
    {
        if (!_options.PipeEnabled)
        {
            return new SpawnOutcome(null, "管道模式已被配置关闭(自检/测试)", Terminal: true);
        }

        var location = EnsureLocation();
        if (!location.Found)
        {
            // terminal: 同一份缺失的产物不会因为重试而出现, 退避只是让用户多等几轮再看到同一句话。
            return new SpawnOutcome(null,
                $"Worker 可执行文件未定位(来源 {location.Source}): {location.Detail} | 已尝试: {location.AttemptedPathsText}",
                Terminal: true);
        }

        WorkerClient? client = null;
        try
        {
            // ⚠️ hello 必须**先**构造: 它同时是 PipeTransport 的启动上下文(工作目录 / key 计算输入)
            // 与稍后下发的握手载荷。两处派生自同一个对象才不会出现"子进程跑在 A 目录、握手说 B 目录"。
            var request = new WorkerHelloRequest
            {
                ProtocolVersion = WorkerProtocol.ProtocolVersion,
                SessionId = sessionId,
                WorkDir = workDir,
                WorkspaceRoot = workspaceRoot,
                CommanderPersonaText = commanderPersonaText,
                RosterEntries = rosterEntries,
                IsPlanMode = planMode,
                // Worker 用它做孤儿自检: 父进程被 kill 时管道也会断, 但"断开"与
                // "父进程只是暂时没说话"难以区分, 用 pid 探测能更快更确定地自我了断。
                ParentPid = Environment.ProcessId
            };

            IToolTransport transport;
            try
            {
                transport = _pipeFactory(location.ExecutablePath!, request);
            }
            catch (Exception ex)
            {
                // 传输构造失败通常是环境问题(可执行位 / 依赖缺失 / 管道创建失败)。
                // 归为非 terminal: 权限类故障是可能被外部干预修好的。
                Log.Warn(LogCategory, ex, $"构造 Worker 管道传输失败(会话 {sessionId})");
                return new SpawnOutcome(null, $"构造管道传输失败: {ex.Message}", Terminal: false);
            }

            var connected = new WorkerClient(transport, workerKey, workDir, sessionId, null);
            client = connected;

            // ⚠️ 订阅必须在握手之前: 握手过程中 Worker 就崩掉的话, 事件不能漏 ——
            // 漏了就没人把这个槽位标记成待重拉, 表现为"每个回合都重新握手失败一次"。
            // 闭包里捕获 connected 自身, 是为了 MarkFaulted 能校验引用相等
            // (迟到的旧 Worker 故障事件不能把刚拉好的新句柄摘掉)。
            connected.Faulted += reason => OnWorkerFaulted(group, sessionId, connected, reason);

            WorkerHelloResponse hello;
            try
            {
                // 池侧再兜一层超时: IToolTransport 契约允许实现方自带超时, 但那是"实现细节",
                // 池需要一个<b>与实现无关</b>的上界, 否则一个漏掉超时的传输会让 GUI 无订阅者时永久挂起。
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(WorkerProtocol.HandshakeTimeout);
                hello = await connected.HandshakeAsync(request, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 铁律: 调用方取消必须原样上抛, 且**先**关掉半成品 ——
                // 否则会留下一个没人管的 Worker 进程占着仓库 git 锁。
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
                group.CancelSpawn(sessionId, "拉起被调用方取消");
                throw;
            }
            catch (OperationCanceledException)
            {
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
                return new SpawnOutcome(null,
                    $"握手超时({WorkerProtocol.HandshakeTimeout.TotalSeconds:0}s)", Terminal: false);
            }
            catch (Exception ex)
            {
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
                // ⚠️ 嗅探「协议版本不符」并升级为 terminal: PipeTransport 在版本错位时抛异常而不是
                // 返回 Ok=false(它当场 SignalFaultOnce 后 throw), 所以正常路径下下面的版本比较
                // 分支根本走不到, 而版本错位是**重试多少次都不会变**的 —— 退避重试只会让用户
                // 多等三轮再看到同一句话。
                // ⚠️ 这是刻意嗅探消息文本, 风险方向是安全的: 它只把"可重试"升级为"不再重试",
                // 万一上游改了措辞, 后果退回"重试 3 次后降级"而不是误判成不该重试。
                var terminal = ex.Message.Contains("协议版本不符", StringComparison.Ordinal);
                return new SpawnOutcome(null,
                    $"握手失败{(terminal ? "(协议版本不符, 两侧 Worker 产物与本进程不同源)" : string.Empty)}: {ex.Message}",
                    terminal);
            }

            if (hello is null)
            {
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
                return new SpawnOutcome(null, "握手无响应", Terminal: false);
            }

            // ⚠️ 以下两个分支对 PipeTransport 而言是**防御性**的: 它在 Ok=false 与版本不符时都是
            // 抛异常(PipeTransport.HandshakeAsync 内部 SignalFaultOnce 后 throw), 不会走到这里。
            // 保留它们是为了 IToolTransport 的其它实现(自检用的假传输)也能被正确判 terminal。
            if (!hello.Ok)
            {
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
                return new SpawnOutcome(null, $"Worker 拒绝握手: {hello.Error ?? "(未给原因)"}", Terminal: false);
            }

            if (hello.ProtocolVersion != WorkerProtocol.ProtocolVersion)
            {
                // ⚠️ terminal 且必须终止 Worker: 协议枚举以数字落盘(AppJsonContext 刻意不开
                // UseStringEnumConverter), 版本不一致时不会抛异常, 只会"静默解析成另一个状态",
                // 表现为 UI 上离奇的状态跳变。宁可立刻终止并降级, 也不要"先跑起来再说"。
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { connected }, CancellationToken.None);
                return new SpawnOutcome(null,
                    $"协议版本不符(Worker={hello.ProtocolVersion}, 本进程={WorkerProtocol.ProtocolVersion}); " +
                    "两侧必须来自同一次构建",
                    Terminal: true);
            }

            return new SpawnOutcome(connected, null, Terminal: false) with { ProcessId = hello.WorkerPid };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 兜底: 上面每条失败路径都已各自处理并关闭; 走到这里的是"没预料到的"错误。
            // 仍然不能冒出去 —— 它会变成用户可见的回合失败, 而根因只是 Worker 起不来。
            if (client is not null)
            {
                _ = WorkerDirectoryGroup.CloseClientsAsync(new[] { client }, CancellationToken.None);
            }

            Log.Warn(LogCategory, ex, $"拉起 Worker 意外失败(会话 {sessionId}, key={workerKey})");
            return new SpawnOutcome(null, $"拉起意外失败: {ex.Message}", Terminal: false);
        }
    }

    /// <summary>
    /// <see cref="WorkerClient.Faulted"/> 的处理: 只记账 + 摘下死句柄, 释放<b>推迟到后台</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ 绝不同步释放</b>: 该回调跑在传输自己的读循环线程上, 而 <c>DisposeAsync</c> 很可能要等
    /// 那条读循环收尾 —— 同步释放就是"回调线程等自己"。见 <see cref="FaultDisposalGraceMs"/>。</para>
    ///
    /// <para><b>L5: 只影响这一个 (目录, 会话)</b>。摘下之后, 该会话的<b>下一次</b>工具需求才会重拉
    /// (懒重拉) —— 当前回合里已在飞的调用归它们自己的令牌与老 Worker 管, 不会被这次记账打断。</para>
    /// </remarks>
    private void OnWorkerFaulted(WorkerDirectoryGroup group, string sessionId, WorkerClient faulted, string reason)
    {
        WorkerClient? detached = null;
        try
        {
            group.MarkFaulted(sessionId, faulted, reason, out detached);
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, $"处理 Worker 故障失败(会话 {sessionId})");
        }

        if (detached is null) return;
        ScheduleDisposal(detached);
    }

    /// <summary>把死句柄的释放推到后台线程池。</summary>
    private static void ScheduleDisposal(WorkerClient client)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 短暂等待只为让 Faulted 的调用栈完全退出 —— 不是等在飞调用:
                // IToolTransport 的契约已经要求 Faulted 触发时把所有在飞请求完成为错误。
                if (FaultDisposalGraceMs > 0) await Task.Delay(FaultDisposalGraceMs).ConfigureAwait(false);
                await WorkerDirectoryGroup.CloseClientsAwaitedAsync(new[] { client }, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, ex, $"故障 Worker 的后台释放失败(key={client.WorkerKey})");
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 退避时长: <c>base × 2^(n-1)</c>, 封顶到 <see cref="WorkerPoolOptions.RespawnBackoffCap"/>, 再叠 ±20% 抖动。
    /// </summary>
    /// <remarks>理由逐条见 <see cref="WorkerPool"/> 类型 remarks 的「退避策略」。</remarks>
    private long BackoffMsFor(int attempts)
    {
        var baseMs = Math.Max(1d, _options.RespawnBackoffBase.TotalMilliseconds);
        var capMs = Math.Max(baseMs, _options.RespawnBackoffCap.TotalMilliseconds);

        // shift 上限 6: base 2s << 6 = 128s, 会被 cap 截住; 上限只是防止 attempts 很大时 (1 << n) 溢出 int
        var shift = Math.Clamp(attempts - 1, 0, 6);
        var delay = Math.Min(baseMs * (1 << shift), capMs);

        // ±20% 抖动: 同目录 N 个会话同时崩溃时, 让重拉摊开成一小波而不是同一毫秒的尖峰。
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return (long)(delay * jitter);
    }

    /// <summary>降级时那条唯一的 <c>Log.Warn</c>: 必须带定位来源与已尝试路径。</summary>
    private void LogDegrade(string sessionId, string workDir, string reason)
    {
        var location = EnsureLocation();
        Log.Warn(LogCategory,
            $"Worker 不可用, 会话 {sessionId}(目录 {workDir})的工具改由**主进程内联执行** —— " +
            "功能不丢, 但失去崩溃隔离与资源隔离(子代理会拖住主进程, grep 能卡 UI)。原因: " + reason +
            $" | 定位来源={location.Source} | 定位详情={location.Detail} | 已尝试路径={location.AttemptedPathsText}");
    }

    /// <summary>
    /// 构造内联(降级)句柄。
    /// </summary>
    /// <remarks>
    /// <b>为什么不给内联句柄调 <see cref="WorkerClient.HandshakeAsync"/></b>:
    /// <see cref="InlineTransport.HandshakeAsync"/> 是本地合成的、没有对端可校验, 调它除了多一次
    /// 无意义的 await 之外还会让人误以为"握手过了就算校验过版本"。内联模式压根没有版本问题。</remarks>
    private WorkerClient CreateInlineClient(
        string sessionId,
        string workDir,
        string workspaceRoot,
        string? commanderPersonaText,
        IReadOnlyList<AgentRosterEntry>? rosterEntries,
        bool planMode,
        string reason)
    {
        IToolTransport transport;
        try
        {
            transport = _inlineFactory(sessionId, workDir, workspaceRoot,
                commanderPersonaText, rosterEntries, planMode);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "进程内降级传输工厂抛出异常。降级是本方案的最后一道兜底(Worker 起不来时工具仍然可用), " +
                "它一旦抛异常, 整条链路就会退化成「Worker 不可用 → 回合也失败」。请检查注入的委托。", ex);
        }

        var client = new WorkerClient(transport, WorkerProtocol.MakeWorkerKey(workDir, sessionId),
            workDir, sessionId, null);

        // InlineTransport 恒不触发 Faulted(它没有可崩溃的对端), 这里仍然订阅:
        // 万一委托给了别的实现, 至少"降级路径上出了传输级故障"这件事会被留痕, 而不是无声消失。
        client.Faulted += text => Log.Warn(LogCategory,
            $"进程内降级路径上出现传输故障(会话 {sessionId}): {text}。这不该发生, 请检查降级工厂");

        Log.Debug(LogCategory, $"会话 {sessionId}(目录 {workDir})使用进程内执行: {reason}");
        return client;
    }

    /// <summary>取(或首次探测)Worker 可执行文件的定位结果, 带 5s 节流。</summary>
    private WorkerLocationResult EnsureLocation()
    {
        var now = Environment.TickCount64;
        lock (_locationGate)
        {
            if (_location is not null && now < _relocateAfterTicks) return _location;

            // 节流窗口从"本次探测"开始计, 而不是从失败开始 —— 否则 spawn 风暴会在
            // 5s 内做几十次全量 stat。
            _relocateAfterTicks = now + RelocateCooldownMs;
            _location = WorkerLocator.Locate();
            return _location;
        }
    }

    private string ResolveWorkspaceRoot(string workDir)
    {
        try
        {
            return _workspaceResolver.ResolveWorkTreeRoot(workDir);
        }
        catch (Exception ex)
        {
            // 非 git 目录 / git 不可用时 resolver 自己就会回退到目录自身, 走到这里说明它异常了。
            // 回退到 workDir 本身是安全的: Worker 侧的路径沙箱根退化成 workDir, 而不是崩溃。
            Log.Warn(LogCategory, ex, $"解析工作树根失败(目录 {workDir}), 回退为目录自身");
            return workDir;
        }
    }

    /// <summary>
    /// 取一个「未在退役」的组实例。
    /// </summary>
    /// <remarks>
    /// 退役竞态: 组被标记 <c>retiring</c> 到真正从字典里摘除之间有一个窗口,
    /// 此时 <c>GetOrAdd</c> 会把"正在退役的实例"原样返回(键已存在)。
    /// 用 <c>TryRemove(KeyValuePair)</c> 只在值仍是同一实例时才删, 避免误删别的线程刚换上的新组。
    /// 第二轮必然拿到全新实例, 所以 8 次是纯保险。</remarks>
    private WorkerDirectoryGroup GetOrAddGroup(string dirKey)
    {
        for (var i = 0; i < 8; i++)
        {
            var group = _groups.GetOrAdd(dirKey, static k => new WorkerDirectoryGroup(k));
            if (!group.IsRetiring) return group;
            _groups.TryRemove(new KeyValuePair<string, WorkerDirectoryGroup>(dirKey, group));
        }

        // 理论不可达。保底返回一个不在字典里的游离组: 它不参与全局统计(L3 会漏),
        // 但工具仍然可用 —— 宁可统计不完美, 也不让生命周期代码抛异常。
        Log.Warn(LogCategory,
            $"{ComparerDriftHint}; 目录 {dirKey} 的 Worker 组反复退役, 本次使用游离组(不参与全局统计)");
        return new WorkerDirectoryGroup(dirKey);
    }

    private WorkerDirectoryGroup? ResolveGroupForRelease(string sessionId, string workDir)
    {
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            try
            {
                var key = GitWorkspaceResolver.Normalize(workDir);
                if (_groups.TryGetValue(key, out var byKey)) return byKey;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, ex, $"Release 时规范化目录失败({workDir}), 改按会话 Id 全组搜索");
            }
        }

        // 全组搜索: L4 换目录之后调用方手里的目录可能已经不准了, 而漏掉一次 -1 = 进程永久残留。
        foreach (var kv in _groups)
        {
            foreach (var id in kv.Value.SessionIds)
            {
                if (string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// 把一个组从池里摘掉并关闭它剩下的全部 Worker(L3 / reconcile 触发)。
    /// </summary>
    /// <remarks>
    /// <b>关闭是 fire-and-forget</b>: 本方法是同步的, 而它会被 GUI 的会话删除回调调用 ——
    /// 在那条路径上等最多 N × <see cref="WorkerProtocol.ShutdownTimeout"/> 会让"删会话"卡住界面。
    /// 需要"关完才能继续"的场景(应用退出)请走 <see cref="ShutdownAllAsync"/>, 那条路径是真等待的。</remarks>
    private void RetireGroup(WorkerDirectoryGroup group)
    {
        // 先摘出字典再关: 反过来的话, 关闭期间新来的 Acquire 会挂到一个正在被拆的组上。
        if (!_groups.TryRemove(new KeyValuePair<string, WorkerDirectoryGroup>(group.DirectoryKey, group))) return;

        var clients = group.BeginRetire();
        if (clients.Length == 0) return;

        Log.Info(LogCategory, $"目录 {group.DirectoryKey} 已无会话, 关闭该目录下 {clients.Length} 个 Worker");
        _ = WorkerDirectoryGroup.CloseClientsAsync(clients, CancellationToken.None);
    }

    /// <summary>空闲回收是否启用。</summary>
    private bool IsIdleTimeoutArmed => _options.DirectoryIdleTimeout > TimeSpan.Zero;

    /// <summary>
    /// 对账循环的实际节拍: <b>必须比空闲阈值细</b>, 否则用户说的"空闲 1 分钟就关"
    /// 会实际变成 1~2 分钟(取决于 tick 落在超时的哪一侧)。
    /// </summary>
    /// <remarks>
    /// 取「空闲阈值的 1/4」并夹在 [2s, <see cref="WorkerPoolOptions.ReconcileInterval"/>] 之间:
    /// <list type="bullet">
    /// <item><b>1/4</b>: 关闭延迟的抖动上界是阈值的 1/4。1 分钟阈值 → 最迟 75 秒关闭,
    /// 而抖动 15 秒对"省内存"这个目标完全够用(它不是延迟敏感的功能)。</item>
    /// <item><b>下界 2s</b>: 阈值被配得极小(如 1s)时不能把节拍也压到 1s ——
    /// 每次 tick 都要遍历全部目录组, 太密只是白烧 CPU。</item>
    /// <item><b>上界取 <see cref="WorkerPoolOptions.ReconcileInterval"/></b>: 空闲回收不启用时
    /// 行为与改造前完全一致(仍是那个 60s 的会话对账节拍), 不因为加了这个功能就改变
    /// 已有用户在跑的节奏。</item>
    /// </list></remarks>
    private TimeSpan EffectiveReconcileInterval
    {
        get
        {
            var configured = _options.ReconcileInterval;
            if (configured <= TimeSpan.Zero) return configured;
            if (!IsIdleTimeoutArmed) return configured;

            var quarter = TimeSpan.FromTicks(_options.DirectoryIdleTimeout.Ticks / 4);
            if (quarter < TimeSpan.FromSeconds(2)) quarter = TimeSpan.FromSeconds(2);
            return quarter < configured ? quarter : configured;
        }
    }

    /// <summary>
    /// 等待同会话在飞的那次拉起。
    /// </summary>
    /// <remarks>
    /// <b>外部取消优先, 且不取消共享的拉起任务</b>: 这里等的是<b>别的调用方</b>发起的拉起,
    /// 本调用方取消不该把它一起掀掉(否则它的发起者会拿到一个毫无理由的失败)。
    /// 语义与 <see cref="ToolTransportContract.AwaitAsync"/> 在无限超时下逐字一致, 直接复用。</remarks>
    private static async Task<WorkerClient> JoinInFlightAsync(
        Task<WorkerClient> inFlight,
        CancellationToken ct) =>
        await ToolTransportContract
            .AwaitAsync(_ => inFlight, Timeout.InfiniteTimeSpan, ct, "Worker 拉起")
            .ConfigureAwait(false);

    private IReadOnlySet<string>? TryGetLiveSessions()
    {
        var provider = _liveSessionProvider;
        if (provider is null)
        {
            Log.Debug(LogCategory,
                "未提供存活会话集合, 目录对账已关闭 —— L3 完全依赖每次 Release 都被正确调用(目前尚未接线)");
            return null;
        }

        try
        {
            return provider() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, "获取存活会话集合失败, 跳过本轮目录对账(误回收比不回收糟得多)");
            return null;
        }
    }

    private static WorkerMode ComputeMode(WorkerLocationResult location, List<WorkerHealthEntry> entries)
    {
        // 判定顺序即优先级: 降级 > 有连接 > 有槽位但都没连上 > 没定位到。
        // ⚠️ "有槽位但都没连上"排在"没定位到"之前, 因为槽位存在意味着用户已经用过工具,
        // 此时"定位失败"这条信息要靠 Location 那一段单独透出, 而不是被模式盖掉。
        foreach (var e in entries)
        {
            if (e.Mode == WorkerMode.Inline) return WorkerMode.Inline;
        }

        foreach (var e in entries)
        {
            if (e.IsConnected) return WorkerMode.Pipe;
        }

        return location.Found ? WorkerMode.NotConnected : WorkerMode.NotFound;
    }

    private void StartReconcileLoop()
    {
        if (_liveSessionProvider is null) return;
        if (_options.ReconcileInterval <= TimeSpan.Zero) return;

        _reconcileTask = Task.Run(RunReconcileLoopAsync, CancellationToken.None);
    }

    private async Task RunReconcileLoopAsync()
    {
        var token = _shutdownCts.Token;
        try
        {
            using var timer = new PeriodicTimer(EffectiveReconcileInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await ReconcileNowAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出(ShutdownAllAsync / DisposeAsync)。
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, ex, "Worker 目录对账循环异常退出");
        }
    }

    private void StopReconcileLoop()
    {
        try
        {
            _shutdownCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 重复释放的幂等路径。
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await ShutdownAllAsync(CancellationToken.None).ConfigureAwait(false);

        // 等对账循环真正退出再释放 CTS —— 否则循环可能正拿着这个 token 在 WaitForNextTickAsync 上。
        var loop = _reconcileTask;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 预期内。
            }
        }

        _shutdownCts.Dispose();
    }

    /// <summary>一次拉起的结果。</summary>
    /// <param name="Client">成功时的连接句柄; 失败为 null。</param>
    /// <param name="Error">失败原因(会进 doctor 与日志, 因此必须是人话)。</param>
    /// <param name="Terminal">
    /// true = 重试没有意义(定位未命中 / 协议版本不符 / 管道模式被关闭), 直接钉死降级。
    /// </param>
    private readonly record struct SpawnOutcome(WorkerClient? Client, string? Error, bool Terminal)
    {
        /// <summary>握手响应里 Worker 自报的 pid。</summary>
        public int? ProcessId { get; init; }
    }
}