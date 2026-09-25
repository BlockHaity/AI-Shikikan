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
    private readonly Dictionary<string, SemaphoreSlim> _gitWriteGates = new(RootComparer);

    public WorkspaceExecutionCoordinator(IWorkspaceResolver? resolver = null)
    {
        _resolver = resolver ?? new GitWorkspaceResolver(".");
    }

    /// <summary>活动(回合/分派/保留/git 写)变化通知。</summary>
    public event Action? Changed;

    public int ActiveTurnCount
    {
        get { lock (_lock) return _activities.Count(a => a.Kind == WorkspaceActivityKind.Turn); }
    }

    public int ActiveAssignmentCount
    {
        get { lock (_lock) return _activities.Count(a => a.Kind == WorkspaceActivityKind.Assignment); }
    }

    /// <summary>解析工作区键(会话回合与分派统一走此入口)。</summary>
    public WorkspaceKey ResolveKey(string workDir)
    {
        var root = _resolver.ResolveWorkTreeRoot(workDir);
        var branch = _resolver.ResolveBranch(workDir);
        return new WorkspaceKey(root, branch);
    }

    // ---- 回合执行权 ----

    /// <summary>申请回合执行权; 同 worktree 跨分支已有活动、或该 worktree 存在 reservation/git 写时拒绝。</summary>
    public bool TryBeginTurn(string ownerSessionId, string workDir,
        out WorkspaceActivity activity, out string? blockedReason)
        => TryBegin(ownerSessionId, workDir, WorkspaceActivityKind.Turn, out activity, out blockedReason);

    public void EndTurn(WorkspaceActivity? activity) => End(activity);

    /// <summary>子 Agent 分派执行权(与回合同规则, 活动计入协调器)。</summary>
    public bool TryBeginAssignment(string assignmentId, string workDir,
        out WorkspaceActivity activity, out string? blockedReason)
        => TryBegin(assignmentId, workDir, WorkspaceActivityKind.Assignment, out activity, out blockedReason);

    public void EndAssignment(WorkspaceActivity? activity) => End(activity);

    private bool TryBegin(string ownerId, string workDir, WorkspaceActivityKind kind,
        out WorkspaceActivity activity, out string? blockedReason)
    {
        activity = null!;
        var key = ResolveKey(workDir);

        lock (_lock)
        {
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

    /// <summary>尝试在工作区上持有操作保留: 持有期间该 worktree 的新回合/分派全部拒绝。</summary>
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

    /// <summary>进入 Git 写临界区(按 worktree 串行); 返回句柄 Dispose 释放。可被取消等待。</summary>
    public async Task<IDisposable> WaitGitWriteAsync(string workDir, string owner, CancellationToken ct = default)
    {
        var root = ResolveKey(workDir).WorkTreeRoot;
        var gate = GateFor(root);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new GitWriteLease(this, root, owner);
    }

    /// <summary>非阻塞进入 Git 写临界区; 已被占用时返回 false。</summary>
    public bool TryEnterGitWrite(string workDir, string owner,
        out IDisposable? lease, out string? blockedReason)
    {
        var root = ResolveKey(workDir).WorkTreeRoot;
        var gate = GateFor(root);
        if (!gate.Wait(0))
        {
            lease = null;
            blockedReason = $"工作区 {root} 正在执行 git 写操作, 请稍后再试。";
            return false;
        }

        lease = new GitWriteLease(this, root, owner);
        blockedReason = null;
        return true;
    }

    private SemaphoreSlim GateFor(string root)
    {
        lock (_lock)
        {
            if (!_gitWriteGates.TryGetValue(root, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gitWriteGates[root] = gate;
            }

            return gate;
        }
    }

    private sealed class GitWriteLease : IDisposable
    {
        private readonly WorkspaceExecutionCoordinator _owner;
        private readonly string _root;
        private readonly string _ownerId;
        private int _released;

        public GitWriteLease(WorkspaceExecutionCoordinator owner, string root, string ownerId)
        {
            _owner = owner;
            _root = root;
            _ownerId = ownerId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            SemaphoreSlim? gate;
            lock (_owner._lock)
            {
                _owner._gitWriteGates.TryGetValue(_root, out gate);
            }

            gate?.Release();
        }
    }

    // ---- 查询 ----

    /// <summary>当前 workDir 是否可进入(不占用执行权), 不可用时返回原因。</summary>
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

    private bool IsGateFreeLocked(string root)
        => _gitWriteGates.TryGetValue(root, out var gate) && gate.CurrentCount > 0;

    /// <summary>活动快照(回合 + 分派)。</summary>
    public IReadOnlyList<WorkspaceActivity> Snapshot()
    {
        lock (_lock)
        {
            return _activities.ToList();
        }
    }

    /// <summary>保留快照。</summary>
    public IReadOnlyList<WorkspaceReservation> Reservations()
    {
        lock (_lock)
        {
            return _reservations.ToList();
        }
    }

    // ---- 单元级自检 ----

    /// <summary>单元级自检: 用假解析器跑并发规则场景, 返回失败项(空列表 = 全部通过)。</summary>
    public static IReadOnlyList<string> SelfCheck()
    {
        var failures = new List<string>();
        var resolver = new FakeResolver();
        resolver.Map("/wt-a", "feat-x");
        resolver.Map("/wt-a2", "feat-y"); // 同 worktree 不同目录(子目录在不同分支的场景由分支映射覆盖)
        resolver.Map("/wt-b", "main");
        resolver.Map("/plain", null);     // 非 git 目录
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

        Check(c.ActiveTurnCount == 0, "自检结束活动回合应为 0");
        return failures;
    }

    /// <summary>自检用假解析器: 目录 → (worktree, 分支) 内存映射。</summary>
    private sealed class FakeResolver : IWorkspaceResolver
    {
        private readonly Dictionary<string, (string Root, string? Branch)> _map = new(StringComparer.Ordinal);

        public void Map(string dir, string? branch) => _map[dir] = ("/wt-" + dir.TrimStart('/').Replace('/', '-'), branch);

        public string ResolveWorkTreeRoot(string dir) =>
            _map.TryGetValue(NormalizeDir(dir), out var v) ? v.Root : dir;

        public string? ResolveBranch(string dir) =>
            _map.TryGetValue(NormalizeDir(dir), out var v) ? v.Branch : null;

        private static string NormalizeDir(string dir) => dir.TrimEnd('/');
    }
}
