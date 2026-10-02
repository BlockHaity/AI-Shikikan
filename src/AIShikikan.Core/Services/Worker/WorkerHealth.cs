namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// Worker 池的整体运行模式。<see cref="WorkerPool.GetHealth"/> 的聚合结论,
/// 供状态栏与 <c>doctor</c> 直接展示。
/// </summary>
/// <remarks>
/// <para><b>⚠️ 不要按数值大小做优先级判断</b> —— 与 <see cref="WorkerLocationSource"/> 同一纪律。
/// 数值只用于日志与可读性;控制流一律按语义分支(<c>== WorkerMode.Inline</c> / <c>== NotFound</c>)。</para>
///
/// <para><b>为什么 <see cref="NotFound"/> 取 0</b>(而不是按声明顺序把 <see cref="Pipe"/> 放 0):
/// 与 <see cref="WorkerLocationSource"/> 的同一条理由 —— 默认值就该是最需要警惕的那一档。
/// <c>WorkerMode mode = 0;</c> 表示「没找到 Worker, 正在退回进程内执行」,
/// 于是「忘了判降级」的调用方在开发期就会暴露, 而不是静默落到 <c>Pipe</c> 这个看起来一切正常的值上。</para>
///
/// <para><b>这一枚举存在的全部理由是"降级必须可见"</b>: 引入 Worker 之后, 工具仍能跑、界面完全正常,
/// 只是子代理又回到主进程、<c>grep</c> 又能卡 UI、git 又在 UI 线程排队 —— 用户没有任何线索,
/// 事后只会觉得"最近变慢了"。所以 <see cref="Inline"/> 是本枚举里最需要被单独拎出来的一档。</para>
/// </remarks>
public enum WorkerMode
{
    /// <summary>
    /// <b>Worker 可执行文件没定位到</b>: 任何工具调用都会退回主进程内联执行(<c>InlineTransport</c>)。
    /// 这是 <see cref="WorkerLocationSource.NotFound"/> / <see cref="WorkerLocationSource.EnvVarPathMissing"/>
    /// 对应的池级结论 —— 定位细节看 <see cref="WorkerHealth.Location"/>。
    /// </summary>
    NotFound = 0,

    /// <summary>
    /// 定位到了可执行文件, 但此刻<b>没有任何存活的 Worker 实例</b>。
    /// 注意这<b>不一定是故障</b>: 会话还没开过第一回合同样是这个状态(懒启动, 见 L1)。
    /// 真正的故障要看 <see cref="WorkerHealthEntry.LastFault"/> 与 <see cref="WorkerHealthEntry.LastSpawnError"/>。
    /// </summary>
    NotConnected = 1,

    /// <summary>
    /// <b>该 (目录, 会话) 的工具此刻在主进程内执行。</b>功能完好, 但失去了崩溃隔离与资源隔离,
    /// 子代理会拖住主进程、<c>grep</c> 能卡 UI。doctor 与状态栏必须把它显示成需要用户注意的一档。
    /// </summary>
    /// <remarks>
    /// <b>涵盖两种形态, 这是刻意的</b>:
    /// <list type="number">
    /// <item><b>退避期内</b>: 上一次拉起失败, 正在等退避窗口, 本回合的工具改由内联执行, 之后仍会重试;</item>
    /// <item><b>已钉死降级</b>: 连续失败达到上限(或定位未命中 / 协议版本不符), 不再重试, 直到该会话被释放。</item>
    /// </list>
    /// <para><b>为什么把"退避期内"也算进来</b>: 那段时间里工具确实已经在主进程里跑了,
    /// 若报成 <see cref="NotConnected"/> 就制造了一个可见性缺口 —— 用户看到的"没连上"与实际的
    /// 性能/隔离回归对不上号, 而这正是最需要被看见的那次回归。区分两者请看
    /// <see cref="WorkerHealthEntry.SpawnAttempts"/> 是否已达上限。</para>
    /// </remarks>
    Inline = 2,

    /// <summary>正常: 有 Worker 存活且已连接, 工具在独立进程里执行。</summary>
    Pipe = 3,
}

/// <summary>
/// 单个 (工作目录 × 会话) 对应的 Worker 健康快照。由 <see cref="WorkerPool.GetHealth"/> 生成,
/// <b>是纯数据</b>(不持有 <see cref="WorkerClient"/>, 不订阅任何事件), 因此可以安全地跨线程传给 UI。
/// </summary>
/// <remarks>
/// <para><b>为什么按 (目录, 会话) 而不是一个笼统的汇总数字</b>: 故障总是局部的 ——
/// 某一个 Worker 崩了, 同目录其它会话照常工作。汇总成"1/3 失败"会让人以为三个都有问题,
/// 定位不到真正出问题的那个; 而 doctor 场景需要的就是"逐条列出, 每条给出目录 + 会话 + pid + 故障原因"。</para>
///
/// <para><b>三个"故障史"字段都不在成功路径上清空</b>(<see cref="LastFault"/> /
/// <see cref="LastSpawnError"/> / <see cref="SpawnAttempts"/>): 它们回答的是"之前发生过什么",
/// 当前是否健康只看 <see cref="IsConnected"/>。理由见 <see cref="WorkerClient.LastFault"/> 的同款说明 ——
/// 清空会让"刚才那个 Worker 崩过一次"这条最有价值的线索在用户报告问题时已经消失。</para>
/// </remarks>
public sealed record WorkerHealthEntry
{
    /// <summary>协议层 Worker 键(<see cref="WorkerProtocol.MakeWorkerKey"/> 的结果), 十六进制小写 16 位。</summary>
    public required string WorkerKey { get; init; }

    /// <summary>规范化后的工作目录(分组键)。</summary>
    public required string WorkDir { get; init; }

    /// <summary>归属会话 Id。</summary>
    public required string SessionId { get; init; }

    /// <summary>该实例当前的模式: 管道 / 内联降级 / 未连接。</summary>
    public required WorkerMode Mode { get; init; }

    /// <summary>传输是否可用(瞬时值)。内联模式恒为 true —— 见 <see cref="WorkerClient.IsConnected"/>。</summary>
    public bool IsConnected { get; init; }

    /// <summary>Worker 进程 pid; 内联模式是主进程 pid(内联"就是"本进程); 尚未拉起时为 null。</summary>
    public int? ProcessId { get; init; }

    /// <summary>最近一次故障原因(含代理侧观察, 如"调用前已断开"), 无则 null。<b>成功不清空</b>。</summary>
    public string? LastFault { get; init; }

    /// <summary>该槽位<b>连续</b>拉起失败次数(一次成功后清零)。达到阈值即永久降级为内联。</summary>
    public int SpawnAttempts { get; init; }

    /// <summary>最近一次拉起失败的原因文本, 无则 null。</summary>
    public string? LastSpawnError { get; init; }

    /// <summary>该 (目录, 会话) 因崩溃/僵死而成功重拉的次数。</summary>
    public int RestartCount { get; init; }

    /// <summary>是否有一次拉起正在飞行中(同一会话的并发 <c>AcquireAsync</c> 会复用它, 不重复起进程)。</summary>
    public bool HasPendingSpawn { get; init; }

    /// <summary>该槽位最近一次活动时间(<see cref="Environment.TickCount64"/> 毫秒基准), 未活跃过为 null。</summary>
    public long? LastActivityTicks { get; init; }
}

/// <summary>
/// Worker 池的可展示快照: 给 <c>doctor</c>(多行文本) 与状态栏(单行模式标记) 消费。
/// </summary>
/// <remarks>
/// <para><b>为什么是一份"值快照"而不是让 UI 直接问池子</b>: ① 池的状态在锁内读取, UI 线程轮询不该
/// 反复抢那些锁(它们与 spawn/handshake 共用同一把组锁的快照路径);② 快照天然是<b>一致</b>的 ——
/// 一份里不会出现"模式是内联、但列表里三条全是管道"这种跨锁读出来的矛盾;
///③ 便于将来落盘或跨进程传递。</para>
///
/// <para><b>⚠️ 本类型不做任何序列化</b>(不走 <c>AppJsonContext</c>): 它是给人看的, 不是持久化格式。
/// 跨进程结构化数据一律用 <c>WorkerMessages.cs</c> 里的 DTO, 见该文件顶部的编帧纪律。</para>
/// </remarks>
public sealed class WorkerHealth
{
    /// <summary>
    /// 构造快照。
    /// </summary>
    /// <param name="mode">池级聚合模式(判定规则见 <see cref="WorkerHealth"/> 的类型 remarks)。</param>
    /// <param name="location">
    /// <see cref="WorkerLocator.Locate"/> 的原始结果, 原样保留 —— 尤其
    /// <see cref="WorkerLocationResult.Detail"/> 与 <see cref="WorkerLocationResult.AttemptedPaths"/>,
    /// 它们是用户排查"为什么在进程内跑"的唯一线索(见 worker-architecture.md 6.1)。
    /// </param>
    /// <param name="entries">逐个 (目录, 会话) 的条目, 可为空表(还没开过会话)。</param>
    /// <param name="restartsTotal">全池因崩溃/僵死而成功重拉的累计次数。</param>
    public WorkerHealth(
        WorkerMode mode,
        WorkerLocationResult location,
        IReadOnlyList<WorkerHealthEntry> entries,
        int restartsTotal)
    {
        Mode = mode;
        Location = location ?? throw new ArgumentNullException(nameof(location));
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        RestartsTotal = restartsTotal;
    }

    /// <summary>池级聚合模式。</summary>
    public WorkerMode Mode { get; }

    /// <summary>定位结果原样保留(未定位时 <see cref="WorkerLocationResult.Found"/> 为 false)。</summary>
    public WorkerLocationResult Location { get; }

    /// <summary>逐个 (目录, 会话) 的条目。</summary>
    public IReadOnlyList<WorkerHealthEntry> Entries { get; }

    /// <summary>全池因崩溃/僵死而成功重拉的累计次数(与 <see cref="WorkerHealthEntry.RestartCount"/> 之和同源)。</summary>
    public int RestartsTotal { get; }

    /// <summary>快照时刻。</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    // ── 便捷派生项(全部从上面三个真源算出, 不缓存, 免得出现两份真相) ──────────────

    /// <summary>定位命中来源(等价于 <c>Location.Source</c>, 为状态栏省一次点号)。</summary>
    public WorkerLocationSource LocationSource => Location.Source;

    /// <summary>定位失败/成功的人类可读说明(等价于 <c>Location.Detail</c>)。</summary>
    public string? LocationDetail => Location.Detail;

    /// <summary>是否已有会话退回进程内执行。doctor / 状态栏据此打降级标记。</summary>
    public bool InlineFallbackActive => Mode == WorkerMode.Inline;

    /// <summary>是否处于"应当让用户注意"的任一状态(降级 / 未定位 / 有槽位但全部断开)。</summary>
    public bool NeedsAttention => Mode is WorkerMode.Inline or WorkerMode.NotFound or WorkerMode.NotConnected;

    /// <summary>当前存活的 Worker 实例数(<see cref="WorkerClient.IsConnected"/> 为 true)。</summary>
    public int ConnectedCount
    {
        get
        {
            var n = 0;
            foreach (var e in Entries)
            {
                if (e.IsConnected) n++;
            }

            return n;
        }
    }

    /// <summary>处于进程内降级状态的 (目录, 会话) 个数。</summary>
    public int InlineCount
    {
        get
        {
            var n = 0;
            foreach (var e in Entries)
            {
                if (e.Mode == WorkerMode.Inline) n++;
            }

            return n;
        }
    }

    /// <summary>
    /// 只看定位结果的快照: 池里还没有任何会话时给 doctor / 启动自检用。
    /// </summary>
    /// <remarks>
    /// 存在的理由是 <c>doctor</c> 的既有形状: <c>Program.cs</c> 里每一项检查都是「跑一个探测 → 报一行」,
    /// 而 <c>WorkerLocator.Locate()</c> 是纯读、不抛异常的静态方法。没有这个入口, doctor 就得
    /// 绕开 <see cref="WorkerPool"/> 自己再调一次 <c>Locate()</c> —— 于是同一件事有两个调用点,
    /// 迟早出现"状态栏说没找到、doctor 说找到了"的不一致。
    /// </remarks>
    public static WorkerHealth FromLocation(WorkerLocationResult location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new WorkerHealth(
            location.Found ? WorkerMode.NotConnected : WorkerMode.NotFound,
            location,
            Array.Empty<WorkerHealthEntry>(),
            0);
    }

    /// <summary>
    /// 产出人类可读的多行文本, <b>直接喂给 doctor</b>(逐行 <c>Console.WriteLine</c>)。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么逐条列出而不是只给一行汇总</b>: doctor 的用途是"用户报障时我们照着排查"。
    /// 汇总一行("Worker: 正常")在真正出问题时毫无用处; 逐条给出「workerKey / 目录 / 会话 / pid /
    /// 故障原因 / 已尝试的定位路径」才是一手排障材料(与既有 <c>ScanLegacyArtifacts</c> 的输出风格一致)。</para>
    ///
    /// <para><b>刻意不用缩进层级</b>: doctor 的输出是等宽控制台文本, 缩进在复制粘贴进 issue 时
    /// 会被 Markdown 折叠掉, 所以用「·」前缀表达层级, 缩进一律为零。</para>
    /// </remarks>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();

        lines.Add($"运行模式: {ModeText(Mode)}");
        lines.Add(InlineFallbackActive
            ? $"⚠ 已有 {InlineCount} 个会话的工具改在**主进程内**执行(失去崩溃隔离与资源隔离)"
            : "工具在独立 Worker 进程中执行");

        lines.Add(Location.Found
            ? $"可执行文件: 已定位 [{Location.Source}] {Location.ExecutablePath}"
            : $"⚠ 可执行文件: 未找到, 将退回主进程内联执行 (来源 {Location.Source})");
        if (!string.IsNullOrWhiteSpace(Location.Detail))
        {
            lines.Add($"  定位说明: {Location.Detail}");
        }

        lines.Add($"已尝试路径: {Location.AttemptedPathsText}");

        if (Entries.Count == 0)
        {
            // 单独说一句, 免得 doctor 把"空表"读成"没有 Worker 出问题"。
            lines.Add("存活 Worker: 0 (尚无会话使用工具; 首次需要工具时才懒启动)");
            return lines;
        }

        var connected = ConnectedCount;
        lines.Add($"存活 Worker: {connected}/{Entries.Count} 已连接 (目录 {DirectoryCount} 个), " +
                  $"进程内降级 {InlineCount} 个, 因故障成功重拉 {RestartsTotal} 次");

        foreach (var e in Entries)
        {
            lines.Add($"  · key={e.WorkerKey} 目录={e.WorkDir} 会话={e.SessionId} " +
                      $"{ModeText(e.Mode)} pid={PidText(e.ProcessId)} " +
                      $"拉起尝试={e.SpawnAttempts} 重拉={e.RestartCount}" +
                      (e.HasPendingSpawn ? " 拉起中" : string.Empty));

            if (!string.IsNullOrWhiteSpace(e.LastFault))
            {
                lines.Add($"      · 最近故障: {e.LastFault}");
            }

            if (!string.IsNullOrWhiteSpace(e.LastSpawnError))
            {
                lines.Add($"      · 最近拉起错误: {e.LastSpawnError}");
            }
        }

        return lines;
    }

    /// <summary>模式的单行可读文本(doctor 与状态栏共用, 避免两处措辞漂移)。</summary>
    public static string ModeText(WorkerMode mode) => mode switch
    {
        WorkerMode.Pipe => "管道模式(Pipe)",
        WorkerMode.Inline => "⚠ 进程内降级(Inline)",
        WorkerMode.NotConnected => "已定位 Worker, 但当前无存活实例(NotConnected)",
        WorkerMode.NotFound => "⚠ 未定位到 Worker, 工具将退回主进程内联执行(NotFound)",
        _ => mode.ToString(),
    };

    /// <summary>存活条目覆盖的**工作目录**数(一个目录可有多个会话, 故与 <see cref="Entries"/> 计数不同)。</summary>
    private int DirectoryCount
    {
        get
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in Entries)
            {
                set.Add(e.WorkDir);
            }

            return set.Count;
        }
    }

    private static string PidText(int? pid) => pid is null ? "-" : pid.Value.ToString();
}