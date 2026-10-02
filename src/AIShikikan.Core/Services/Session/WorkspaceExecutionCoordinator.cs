namespace AIShikikan.Core.Services.Session;

/// <summary>工作区标识: 规范化 WorkTreeRoot + 分支(git 仓库按真实分支; 非 git 目录 Branch 为空)。</summary>
public sealed record WorkspaceKey(string WorkTreeRoot, string? Branch)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Branch) ? WorkTreeRoot : $"{WorkTreeRoot}@{Branch}";
}

/// <summary>工作区活动类别。</summary>
public enum WorkspaceActivityKind
{
    Turn,
    Assignment
}

/// <summary>已占用的执行权条目(回合或子 Agent 分派)。</summary>
public sealed record WorkspaceActivity(
    string ActivityId,
    string OwnerId,
    WorkspaceKey Key,
    WorkspaceActivityKind Kind,
    DateTime StartedAt);

/// <summary>回滚 / Fork 确认期等"操作保留"句柄; Dispose 即释放。</summary>
public sealed class WorkspaceReservation : IDisposable
{
    private readonly WorkspaceExecutionCoordinator _owner;
    private int _released;

    internal WorkspaceReservation(WorkspaceExecutionCoordinator owner, string reservationId,
        string ownerId, string reason, string root, string? branch, DateTime createdAt)
    {
        _owner = owner;
        ReservationId = reservationId;
        OwnerId = ownerId;
        Reason = reason;
        Key = new WorkspaceKey(root, branch);
        CreatedAt = createdAt;
    }

    public string ReservationId { get; }
    public string OwnerId { get; }
    public string Reason { get; }
    public WorkspaceKey Key { get; }
    public DateTime CreatedAt { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _owner.Release(this);
        }
    }
}

/// <summary>工作区执行协调器: 以规范化 WorkTreeRoot+Branch 为键管理并发执行权。
/// 规则(见 AGENTS 约束): 同键(同 worktree 同分支)多会话可并发; 同 worktree 跨分支互斥;
/// 不同 worktree 互不影响。Git 写操作按 worktree 串行; 回滚/Fork 确认期可持有 reservation
/// 阻止该 worktree 新回合进入。跟踪活动回合与活动 Assignment。</summary>
public sealed class WorkspaceExecutionCoordinator
{
    private static readonly StringComparer RootComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly IWorkspaceResolver _resolver;
    private readonly object _lock = new();

    private readonly List<WorkspaceActivity> _activities = [];
    private readonly List<WorkspaceReservation> _reservations = [];

    /// <summary>
    /// 按 worktree 的 git 写串行门。条目在**没有任何持有者/等待者**时被淘汰(见 ReleaseGateRef),
    /// 否则长跑进程里每个访问过的 worktree 都会永久留下一条字典项 —— 内存上是小事,
    /// 语义上不干净(已关掉的 worktree 永远"存在")。
    /// </summary>
    private readonly Dictionary<string, GitWriteGate> _gitWriteGates = new(RootComparer);

    public WorkspaceExecutionCoordinator(IWorkspaceResolver? resolver = null)
    {
        _resolver = resolver ?? new GitWorkspaceResolver(".");
    }

    /// <summary>活动(回合/分派/保留/git 写)变化通知。</summary>
    public event Action? Changed;

    /// <summary>当前无生产调用方(仅 SelfCheck 覆盖): 供 UI 诊断与将来接线 subagent 面板使用, 不要删。</summary>
    public int ActiveTurnCount
    {
        get { lock (_lock) return _activities.Count(a => a.Kind == WorkspaceActivityKind.Turn); }
    }

    /// <summary>当前无生产调用方(仅 SelfCheck 覆盖): 供 UI 诊断与将来接线 subagent 面板使用, 不要删。</summary>
    public int ActiveAssignmentCount
    {
        get { lock (_lock) return _activities.Count(a => a.Kind == WorkspaceActivityKind.Assignment); }
    }

    /// <summary>解析工作区键(会话回合与分派统一走此入口)。
    /// 生产实现为 GitWorkspaceResolver, 带 2s TTL 缓存: 同一目录的两次 rev-parse 结果会被复用,
    /// 因此这里可以在锁外调用而不必担心"每次申请起 2 个 git 进程 + 长锁阻塞其它 worktree"。</summary>
    public WorkspaceKey ResolveKey(string workDir)
    {
        var root = _resolver.ResolveWorkTreeRoot(workDir);
        var branch = _resolver.ResolveBranch(workDir);
        return new WorkspaceKey(root, branch);
    }

    // ---- 回合执行权 ----

    /// <summary>申请回合执行权; 同 worktree 跨分支已有活动、或该 worktree 存在 reservation/git 写时拒绝。
    /// 已有生产调用方: SessionRuntime 经 ISessionEngineHost.TryBeginTurn 调用, 拒绝会把原因带给 GUI。</summary>
    public bool TryBeginTurn(string ownerSessionId, string workDir,
        out WorkspaceActivity activity, out string? blockedReason)
        => TryBegin(ownerSessionId, workDir, WorkspaceActivityKind.Turn, out activity, out blockedReason);

    /// <summary>已有生产调用方: AgentEngine 回合 finally(经 SessionRuntime.EndTurn 调用)。
    /// 传 null 是合法输入(回合从未成功申请到执行权时), 此时什么都不做。</summary>
    public void EndTurn(WorkspaceActivity? activity) => End(activity);

    /// <summary>
    /// 子 Agent 分派执行权(与回合同规则, 活动计入协调器)。
    ///
    /// <para><b>当前无生产调用方</b>(仅 SelfCheck 覆盖): run_subagents / assign_task 尚未接入,
    /// 因此"同 worktree 跨分支并发跑子代理"目前不被本协调器拦。要接线时必须:
    /// ① 在分派真正开始前调用, 拿到 activity; ② 整个子代理执行体包在 try/finally 里,
    /// finally 中调用 EndAssignment(activity) —— 与 SessionRuntime 对回角的处理同构;
    /// ③ 申请失败时把 blockedReason 作为工具结果文本返回给 LLM, 不要静默忽略(否则 LLM 会反复重试)。</para>
    /// </summary>
    public bool TryBeginAssignment(string assignmentId, string workDir,
        out WorkspaceActivity activity, out string? blockedReason)
        => TryBegin(assignmentId, workDir, WorkspaceActivityKind.Assignment, out activity, out blockedReason);

    /// <summary>配 TryBeginAssignment 的释放口, 必须与申请成对出现在 finally 中(见上)。</summary>
    public void EndAssignment(WorkspaceActivity? activity) => End(activity);

    private bool TryBegin(string ownerId, string workDir, WorkspaceActivityKind kind,
        out WorkspaceActivity activity, out string? blockedReason)
    {
        activity = null!;

        // 键必须在锁外算: 解析可能起 git 子进程(非缓存路径), 持锁会长时间阻塞所有 worktree 的申请。
        // TOCTOU 窗口(算键 → 进锁之间用户切了分支)已由 GitWorkspaceResolver 的 2s TTL 缓存收窄到毫秒级,
        // 且最坏后果只是"这一回合按切分支前的旧键准入", 不会造成数据损坏 —— 真要收紧就把 TTL 调小。
        var key = ResolveKey(workDir);

        lock (_lock)
        {
            // 锁内不再重算键(那等于持锁跑 git); 只用已解析出的 key 做廉价判据校验:
            // reservation / 跨分支活动 / git 写占用三项全部只读内存。
            var blocked = BlockedReasonLocked(key);
            if (blocked is not null)
            {
                blockedReason = blocked;
                return false;
            }

            activity = new WorkspaceActivity(
                Guid.NewGuid().ToString("N")[..8], ownerId, key, kind, DateTime.Now);
            _activities.Add(activity);
            blockedReason = null;
        }

        Changed?.Invoke();
        return true;
    }

    private void End(WorkspaceActivity? activity)
    {
        if (activity is null) return;
        bool removed;
        lock (_lock)
        {
            removed = _activities.Remove(activity);
        }

        if (removed) Changed?.Invoke();
    }

    // ---- 操作保留(回滚 / Fork 确认期) ----

    /// <summary>
    /// 尝试在工作区上持有操作保留: 持有期间该 worktree 的新回合/分派全部拒绝。
    ///
    /// <para><b>当前无生产调用方</b>(仅 SelfCheck 覆盖) —— 这正是技术债 #14 的核心: 回滚 / Fork
    /// 的"确认期"目前只靠会话级守卫(Fork_SessionOccupied: 当前会话在跑就禁用 Fork)，
    /// 没有 worktree 级保护, 于是别的会话仍可在确认弹窗期间对同一 worktree 起回合并写文件。
    /// 一旦确认回滚执行了 <c>reset --hard</c>, 对方刚写的改动就没了。</para>
    ///
    /// <para>接线要点: 保留句柄是 IDisposable, <b>必须在对话框关闭的所有路径上 Dispose</b> ——
    /// 确认、取消、窗口关闭、异常, 一个都不能漏, 漏了会永久堵死该 worktree 的新回合。
    /// 建议用法: 打开确认弹窗<em>前</em> TryReserve, 弹窗返回后立刻 using 包住实际执行。</para>
    /// </summary>
    public bool TryReserve(string workDir, string owner, string reason,
        out WorkspaceReservation? reservation, out string? blockedReason)
    {
        reservation = null;
        var key = ResolveKey(workDir);
        lock (_lock)
        {
            // 已有保留(任何原因)时不允许叠加, 避免两个回滚/Fork 确认互相覆盖
            var existing = _reservations.FirstOrDefault(r => RootComparer.Equals(r.Key.WorkTreeRoot, key.WorkTreeRoot));
            if (existing is not null)
            {
                blockedReason = $"工作区 {key.WorkTreeRoot} 正被 {existing.OwnerId} 保留({existing.Reason}), 请稍后再试。";
                return false;
            }

            reservation = new WorkspaceReservation(this,
                Guid.NewGuid().ToString("N")[..8], owner, reason, key.WorkTreeRoot, key.Branch, DateTime.Now);
            _reservations.Add(reservation);
            blockedReason = null;
        }

        Changed?.Invoke();
        return true;
    }

    internal void Release(WorkspaceReservation reservation)
    {
        bool removed;
        lock (_lock)
        {
            removed = _reservations.Remove(reservation);
        }

        if (removed) Changed?.Invoke();
    }

    // ---- Git 写串行 ----

    /// <summary>
    /// 进入 Git 写临界区(按 worktree 串行); 返回句柄 Dispose 释放。可被取消等待(取消时抛 OCE, 不产生句柄)。
    ///
    /// <para><b>当前无生产调用方</b>(仅 SelfCheck 覆盖) —— git_add / git_commit / git_create_checkpoint
    /// 这三个写工具目前直接跑 git, 不经本门, 因此"git 写"与"回合"、"reset --hard"之间仍可交叉。
    /// 接线要点: 工具方法体整体包在 <c>using</c> 里跑 git 命令, git 返回后再 Dispose;
    /// 命令失败/抛异常也要释放(using 保证), 但**绝不能**在 Dispose 前跳过错误上报。</para>
    /// </summary>
    public async Task<IDisposable> WaitGitWriteAsync(string workDir, string owner, CancellationToken ct = default)
    {
        var root = ResolveKey(workDir).WorkTreeRoot;
        var gate = GateFor(root);
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 等待被取消: 引用计数必须在此归还, 否则该 worktree 的门永远不会被淘汰
            ReleaseGateRef(root, gate);
            throw;
        }

        return new GitWriteLease(this, root, owner, gate);
    }

    /// <summary>非阻塞进入 Git 写临界区; 已被占用时返回 false(lease 为 null, blockedReason 给出原因)。</summary>
    public bool TryEnterGitWrite(string workDir, string owner,
        out IDisposable? lease, out string? blockedReason)
    {
        var root = ResolveKey(workDir).WorkTreeRoot;
        var gate = GateFor(root);
        if (!gate.Semaphore.Wait(0))
        {
            ReleaseGateRef(root, gate);
            lease = null;
            blockedReason = $"工作区 {root} 正在执行 git 写操作, 请稍后再试。";
            return false;
        }

        lease = new GitWriteLease(this, root, owner, gate);
        blockedReason = null;
        return true;
    }

    /// <summary>取出(或新建)该 worktree 的写门, 并记一次引用(+1 表示"有人持有或正在等")。</summary>
    private GitWriteGate GateFor(string root)
    {
        lock (_lock)
        {
            if (!_gitWriteGates.TryGetValue(root, out var gate))
            {
                gate = new GitWriteGate();
                _gitWriteGates[root] = gate;
            }

            gate.Refs++;
            return gate;
        }
    }

    /// <summary>
    /// 归还一次引用; 引用归零且信号量空闲时把条目移出字典(技术债 #18 的"永不回收"修法)。
    ///
    /// <para>为什么"Refs==0 && CurrentCount>0"才是安全的淘汰点: 每次 GateFor 都先 +1 再去等信号量,
    /// 所以 Refs==0 蕴含"没有持有者也没有等待者", 此时该 SemaphoreSlim 不会被任何人再触碰;
    /// 下次 GateFor 会新建一个干净的信号量, 不会把两个写者放进同一个临界区。</para>
    ///
    /// <para>不 Dispose 被移除的 SemaphoreSlim: 此刻已无人引用它, 直接丢给 GC 即可;
    /// 而 Dispose 反而多一个可能抛 ObjectDisposedException 的时机。</para>
    /// </summary>
    private void ReleaseGateRef(string root, GitWriteGate gate)
    {
        lock (_lock)
        {
            if (gate.Refs > 0) gate.Refs--;
            if (gate.Refs > 0) return;
            if (gate.Semaphore.CurrentCount <= 0) return;
            if (_gitWriteGates.TryGetValue(root, out var current) && ReferenceEquals(current, gate))
            {
                _gitWriteGates.Remove(root);
            }
        }
    }

    /// <summary>按 worktree 的写门: 引用计数 + 信号量。Refs 语义见 ReleaseGateRef。</summary>
    private sealed class GitWriteGate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>持有者 + 等待者总数; 在 _lock 内以非原子方式自增自减。</summary>
        public int Refs;
    }

    private sealed class GitWriteLease : IDisposable
    {
        private readonly WorkspaceExecutionCoordinator _owner;
        private readonly string _root;
        private readonly string _ownerId;
        private readonly GitWriteGate _gate;
        private int _released;

        public GitWriteLease(WorkspaceExecutionCoordinator owner, string root, string ownerId,
            GitWriteGate gate)
        {
            _owner = owner;
            _root = root;
            _ownerId = ownerId;
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            // 先 Release 信号量再归还引用: 反过来的话, 中间那一瞬 Refs 可能归零而被淘汰,
            // 造成"信号量仍有持有者但字典里已无此条目"的竞态。
            _gate.Semaphore.Release();
            _owner.ReleaseGateRef(_root, _gate);
        }
    }

    // ---- 查询 ----

    /// <summary>当前 workDir 是否可进入(不占用执行权), 不可用时返回原因。
    /// 当前无生产调用方(UI 侧可用它在发消息前做禁用态提示); 保留为协调器 API 面的组成部分, 不要删。</summary>
    public string? BlockedReason(string workDir)
    {
        var key = ResolveKey(workDir);
        lock (_lock)
        {
            return BlockedReasonLocked(key);
        }
    }

    private string? BlockedReasonLocked(WorkspaceKey key)
    {
        var root = key.WorkTreeRoot;

        var reservation = _reservations.FirstOrDefault(r => RootComparer.Equals(r.Key.WorkTreeRoot, root));
        if (reservation is not null)
        {
            return $"工作区 {root} 正被 {reservation.OwnerId} 保留({reservation.Reason}), 请稍后再试。";
        }

        var conflicting = _activities.FirstOrDefault(a =>
            RootComparer.Equals(a.Key.WorkTreeRoot, root) &&
            !string.Equals(a.Key.Branch ?? string.Empty, key.Branch ?? string.Empty, StringComparison.Ordinal));
        if (conflicting is not null)
        {
            return $"同一工作区 {root} 的分支 {conflicting.Key.Branch ?? "?"} 正在执行({conflicting.OwnerId}), " +
                   "同一 WorkTreeRoot 同时只能在一个分支上运行会话。";
        }

        var gitWrite = _gitWriteGates.ContainsKey(root);
        if (gitWrite)
        {
            // 仅当写锁实际被占用时提示: 信号量存在但计数为 1 表示空闲
            if (!IsGateFreeLocked(root))
            {
                return $"工作区 {root} 正在执行 git 写操作, 请稍后再试。";
            }
        }

        return null;
    }

    // 信号量计数 > 0 即空闲。注释保留原始判据的由来: 门按 worktree 常驻字典,
    // "有条目"不等于"被占用", 因此必须再看计数。条目现已在无引用时被淘汰(见 ReleaseGateRef),
    // 但并发申请瞬间仍可能存在 Refs>0、尚未 Wait 的条目, 该二次判据依旧必要。
    private bool IsGateFreeLocked(string root)
        => _gitWriteGates.TryGetValue(root, out var gate) && gate.Semaphore.CurrentCount > 0;

    /// <summary>活动快照(回合 + 分派)。当前无生产调用方: 供 UI 诊断面板/将来接线使用, 不要删。</summary>
    public IReadOnlyList<WorkspaceActivity> Snapshot()
    {
        lock (_lock)
        {
            return _activities.ToList();
        }
    }

    /// <summary>保留快照。当前无生产调用方: 供 UI 诊断面板/将来接线使用, 不要删。</summary>
    public IReadOnlyList<WorkspaceReservation> Reservations()
    {
        lock (_lock)
        {
            return _reservations.ToList();
        }
    }

    /// <summary>当前驻留的 git 写门条目数。当前无生产调用方, 仅供 SelfCheck 验证淘汰行为, 不要删。</summary>
    private int GitWriteGateCount
    {
        get { lock (_lock) return _gitWriteGates.Count; }
    }

    // ---- 单元级自检 ----

    /// <summary>
    /// 自检覆盖的场景组数。Program.cs 的 doctor 输出原先把组数写死成字符串("9 组场景全部通过"),
    /// 这里新增场景时就会与显示脱节, 故改为引用本常量, 由调用方拼装文案。
    /// </summary>
    public const int SelfCheckScenarioCount = 10;

    /// <summary>单元级自检: 用假解析器跑并发规则场景, 返回失败项(空列表 = 全部通过)。</summary>
    public static IReadOnlyList<string> SelfCheck()
    {
        var failures = new List<string>();
        var resolver = new FakeResolver();
        resolver.Map("/wt-a", "feat-x");
        resolver.Map("/wt-b", "main");
        resolver.Map("/plain", null);     // 非 git 目录
        resolver.Map("/wt-a/sub", "/wt-a", "feat-x"); // /wt-a 的子目录, 归属同一 worktree
        var c = new WorkspaceExecutionCoordinator(resolver);

        void Check(bool condition, string name)
        {
            if (!condition) failures.Add(name);
        }

        // 1. 同 worktree 同分支: 两个会话并发允许
        Check(c.TryBeginTurn("s1", "/wt-a", out var t1, out var r1), "S1 同键首会话应允许");
        Check(r1 is null, "S1 不应有拒绝原因");
        Check(c.TryBeginTurn("s2", "/wt-a", out var t2, out var r2), "S2 同键并发应允许");
        Check(t1.Key == t2.Key, "同键会话键应一致");

        // 2. 同 worktree 跨分支: 拒绝
        resolver.Map("/wt-a", "other-branch");
        Check(!c.TryBeginTurn("s3", "/wt-a", out _, out var r3), "S3 跨分支应拒绝");
        Check(r3?.Contains("分支") == true, "S3 拒绝原因应含分支说明");

        // 3. 不同 worktree: 允许
        resolver.Map("/wt-a", "feat-x"); // 还原
        Check(c.TryBeginTurn("s4", "/wt-b", out var t4, out _), "S4 不同 worktree 应允许");
        Check(!RootComparer.Equals(t4.Key.WorkTreeRoot, t1.Key.WorkTreeRoot), "不同 worktree 键应不同");

        // 4. 非 git 同目录: 同键允许并发
        Check(c.TryBeginTurn("s5", "/plain", out var t5, out _), "S5 非 git 同目录应允许");
        Check(c.TryBeginTurn("s6", "/plain", out var t6, out _), "S6 非 git 同目录并发应允许");
        Check(t5.Key.Branch is null && t5.Key == t6.Key, "非 git 键应一致且无分支");

        // 5. 回合结束后跨分支恢复允许
        c.EndTurn(t1);
        c.EndTurn(t2);
        c.EndTurn(t4);
        c.EndTurn(t5);
        c.EndTurn(t6);
        resolver.Map("/wt-a", "other-branch");
        Check(c.TryBeginTurn("s3", "/wt-a", out var t3, out _), "回合结束后跨分支应允许");
        c.EndTurn(t3);

        // 6. 操作保留期间新回合拒绝, 释放后恢复
        resolver.Map("/wt-a", "feat-x");
        Check(c.TryReserve("/wt-a", "s1", "回滚确认", out var res, out _), "S1 保留应成功");
        Check(!c.TryBeginTurn("s2", "/wt-a", out _, out var r6), "保留期间新回合应拒绝");
        Check(r6 is not null && r6.Contains("保留"), "保留拒绝原因应说明保留");
        Check(!c.TryReserve("/wt-a", "s2", "叠加", out _, out var r6b), "第二个保留应被拒绝");
        Check(r6b is not null, "叠加保留应给出原因");
        res!.Dispose();
        Check(c.TryBeginTurn("s2", "/wt-a", out var t7, out _), "释放保留后应允许");
        c.EndTurn(t7);

        // 7. Assignment 计入协调器: 跨分支被拒
        Check(c.TryBeginAssignment("a1", "/wt-b", out var as1, out _), "Assignment 应允许");
        Check(c.ActiveAssignmentCount == 1, "活动 Assignment 计数应为 1");
        resolver.Map("/wt-b", "feature");
        Check(!c.TryBeginTurn("s9", "/wt-b", out _, out var r7), "Assignment 活动期间跨分支应拒绝");
        resolver.Map("/wt-b", "main");
        c.EndAssignment(as1);
        Check(c.ActiveAssignmentCount == 0, "Assignment 结束后计数应归零");

        // 8. Git 写串行
        var lease = c.TryEnterGitWrite("/wt-a", "rollback", out var gl, out _);
        Check(lease, "首个 git 写应进入");
        Check(!c.TryEnterGitWrite("/wt-a", "other", out _, out var r8), "并发 git 写应被拒绝");
        Check(r8 is not null, "并发 git 写应给出原因");
        Check(!c.TryBeginTurn("s8", "/wt-a", out _, out var r8b), "git 写期间新回合应拒绝");
        gl!.Dispose();
        Check(c.TryBeginTurn("s8", "/wt-a", out var t8, out _), "git 写结束后应允许");
        c.EndTurn(t8);

        // 9. 不同 worktree 的 git 写互不影响
        var gl2ok = c.TryEnterGitWrite("/wt-b", "merge", out var gl2, out _);
        Check(gl2ok, "另一 worktree git 写应允许");
        Check(c.TryBeginTurn("s10", "/wt-a", out var t10, out _), "其它 worktree 不受 git 写影响");
        c.EndTurn(t10);
        gl2!.Dispose();

        // 10. 同一 worktree 的不同目录(子目录): 必须归一到同一 WorkTreeRoot, 从而参与同一套隔离
        // 这个场景原先是死数据("/wt-a2" 既非 /wt-a 的子目录、其映射也从未被任何断言使用),
        // 现由 FakeResolver 的三参 Map 重载显式表达"子目录 → 父 worktree"的归属关系。
        // 注意: 二参 Map 的根是 "/wt-" + dir, 所以父目录 "/wt-a" 的实际根是 "/wt-wt-a";
        // 三参 Map 必须写同一个根, 否则断言比的是两个不同 worktree(这是原先场景的写法错误)
        resolver.Map("/wt-a/sub", "/wt-wt-a", "feat-x");
        Check(c.TryBeginTurn("s11", "/wt-a", out var t11, out _), "S11 父目录首会话应允许");
        Check(c.TryBeginTurn("s12", "/wt-a/sub", out var t12, out _), "S12 子目录首会话应允许");
        Check(RootComparer.Equals(t12.Key.WorkTreeRoot, t11.Key.WorkTreeRoot), "子目录根应归一到父目录的 worktree 根");
        Check(t12.Key == t11.Key, "同 worktree 子目录应与父目录同键");
        // 子目录所在分支与父目录活动分支不同时, 同样受跨分支互斥约束(证明子目录走的是 worktree 级判据)。
        // 根必须与上面一致("/wt-wt-a"), 否则测的是另一个 worktree, 会因"无冲突"而放行。
        resolver.Map("/wt-a/sub", "/wt-wt-a", "other-branch");
        Check(!c.TryBeginTurn("s13", "/wt-a/sub", out _, out var r9), "S13 子目录跨分支应拒绝");
        Check(r9?.Contains("分支") == true, "S13 拒绝原因应含分支说明");
        c.EndTurn(t12);
        c.EndTurn(t11);

        // 收尾不变式(不计入场景数): git 写门在无引用后应被淘汰(技术债 #18),
        // 否则长期驻留会让已关闭的 worktree 永远在字典里"存在"
        Check(c.GitWriteGateCount == 0, "自检结束 git 写门应全部淘汰");
        Check(c.ActiveTurnCount == 0, "自检结束活动回合应为 0");
        Check(c.ActiveAssignmentCount == 0, "自检结束活动分派应为 0");
        Check(c.Reservations().Count == 0, "自检结束保留应全部释放");
        return failures;
    }

    /// <summary>自检用假解析器: 目录 → (worktree, 分支) 内存映射。</summary>
    private sealed class FakeResolver : IWorkspaceResolver
    {
        private readonly Dictionary<string, (string Root, string? Branch)> _map = new(StringComparer.Ordinal);

        public void Map(string dir, string? branch) => _map[dir] = ("/wt-" + dir.TrimStart('/').Replace('/', '-'), branch);

        /// <summary>把任意目录显式挂到指定 worktree 根上(用于模拟子目录归属同一 worktree)。</summary>
        public void Map(string dir, string root, string? branch) => _map[dir] = (root, branch);

        public string ResolveWorkTreeRoot(string dir) =>
            _map.TryGetValue(NormalizeDir(dir), out var v) ? v.Root : dir;

        public string? ResolveBranch(string dir) =>
            _map.TryGetValue(NormalizeDir(dir), out var v) ? v.Branch : null;

        private static string NormalizeDir(string dir) => dir.TrimEnd('/');
    }
}
