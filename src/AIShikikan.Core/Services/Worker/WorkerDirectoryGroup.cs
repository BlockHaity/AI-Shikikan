using AIShikikan.Core.Logging;

namespace AIShikikan.Core.Services.Worker;

/// <summary>
/// 一个工作目录下的全部 Worker 会话槽位: 持有「会话引用计数」与「(会话 → WorkerClient) 映射」,
/// 并实现目录归零时的关闭流程。仅由 <see cref="WorkerPool"/> 驱动。
/// </summary>
/// <remarks>
/// <para><b>⚠️ 分组键必须是规范化后的路径</b>(<c>GitWorkspaceResolver.Normalize</c> 的结果)。
/// 这不是可有可无的整洁问题: 同一个目录会被写成 <c>/repo</c> / <c>/repo/</c> / <c>/repo/./sub/..</c> 三种形态
/// (引擎的 <c>Options.WorkDir</c>、GUI 的会话配置、用户在设置里手输的路径都可能是其中任意一种)。
/// 不规范化就会分裂成三个组, 于是「这个目录明明还有会话」被误判成三个空目录, 每个都被 L3 关闭 ——
/// 表现是"Worker 反复重启", 而计数表看起来完全正常。</para>
///
/// <para><b>比较器同样要对齐平台策略</b>(Windows/macOS 用 <see cref="StringComparer.OrdinalIgnoreCase"/>,
/// Linux 用 <see cref="StringComparer.Ordinal"/>), 与 <c>GitWorkspaceResolver</c> 的私有字段同款判定。
/// 那个字段是 private, 这里复制一份判定 —— 代价是"平台策略在两处各写一遍",
/// 收益是不必为了一行常量去改既有类的可见性。</para>
///
/// <para><b>为什么每组一把锁而不是全局一把</b>: 组的增删改只碰本组的字典, 是纯内存操作(纳秒级);
/// 而 spawn + 握手(可能几十毫秒, 最坏 <see cref="WorkerProtocol.HandshakeTimeout"/> 30s)
/// 刻意<b>不放进任何锁里</b>。这样"目录 A 的 Worker 起得慢"绝不会阻塞"目录 B 的新会话" ——
/// 见 <see cref="WorkerPool"/> 类注释「并发同步方案」一节。</para>
///
/// <para><b>⚠️ 锁纪律</b>: <c>_gate</c> 内<b>只允许</b>字典/字段读写与引用计数变更,
/// 绝不允许 <c>await</c>、<c>ShutdownAsync</c>、<c>DisposeAsync</c> 或任何进程/IO 操作。
/// 一切"要花时间的事"都以返回句柄的方式把状态搬出锁外, 由 <see cref="WorkerPool"/> 在锁外执行。
/// 违反这条的表现是 <c>ShutdownAsync</c> 持锁等一个不肯退出的 Worker,
/// 而同组的 <see cref="AcquireAsync"/> 全部排队 → 单个半死 Worker 拖垮整个目录。</para>
/// </remarks>
public sealed class WorkerDirectoryGroup
{
    private const string LogCategory = "Worker";

    /// <summary>
    /// 目录键的比较器。必须与 <c>GitWorkspaceResolver</c> 的 <c>PathComparer</c> 同款 ——
    /// 否则同一目录在两个地方会被算成两个(见类型 remarks)。
    /// </summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly object _gate = new();

    /// <summary>会话 Id → 槽位。<b>所有读写都在 <c>_gate</c> 内</b>(锁纪律见类型 remarks)。</summary>
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    /// <summary>
    /// 本组已进入退役流程: 新的 <see cref="Attach"/> 一律被拒, 已摘下的槽位不再接受发布。
    /// 池据此把后来的 <see cref="WorkerPool.AcquireAsync"/> 导向一个<b>全新</b>的组实例。
    /// </summary>
    private bool _retiring;

    /// <summary>组级最近活动时间(见 <see cref="LastActivityTicks"/>)。</summary>
    private long _lastActivityTicks;

    /// <param name="directoryKey">
    /// 规范化后的工作目录(<b>调用方必须先 <c>GitWorkspaceResolver.Normalize</c></b>)。
    /// 本类不做规范化, 只按传入值建键 —— 因为"规范化责任在唯一上游"是本仓
    /// <c>GitCheckpointStore.GetRepoHash</c> 已确立的风格, 两处各规范化一次迟早会漂移。
    /// </param>
    public WorkerDirectoryGroup(string directoryKey)
    {
        if (string.IsNullOrWhiteSpace(directoryKey))
        {
            throw new ArgumentException("directoryKey 不能为空", nameof(directoryKey));
        }

        DirectoryKey = directoryKey;
    }

    /// <summary>规范化后的目录路径(分组键)。</summary>
    public string DirectoryKey { get; }

    /// <summary>本组内当前存活的会话 Id 集合快照(锁内拷贝, 调方无需再加锁)。</summary>
    public IReadOnlyList<string> SessionIds
    {
        get { lock (_gate) { return _slots.Keys.ToList(); } }
    }

    /// <summary>
    /// 目录内会话引用计数(L2/L3 的判据): ≥1 → 本组 Worker 保持存活; 归零 → 关闭本组全部 Worker。
    /// </summary>
    public int SessionCount
    {
        get { lock (_gate) { return _slots.Count; } }
    }

    /// <summary>本组是否已进入退役流程。</summary>
    public bool IsRetiring
    {
        get { lock (_gate) { return _retiring; } }
    }

    /// <summary>本组内当前持有的 Worker 句柄快照(锁内拷贝)。</summary>
    public IReadOnlyList<WorkerClient> Workers
    {
        get
        {
            lock (_gate)
            {
                var list = new List<WorkerClient>(_slots.Count);
                foreach (var slot in _slots.Values)
                {
                    if (slot.Client is not null) list.Add(slot.Client);
                }

                return list;
            }
        }
    }

    /// <summary>
    /// 本组最近一次活动时刻(<see cref="Environment.TickCount64"/> 毫秒基准), 从未活动过为 0。
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 它已不是空闲回收的判据了</b> —— 空闲回收判据在 <see cref="WorkerClient"/> 上
    /// (<c>ActiveCallCount</c> / <c>LastActivityTicks</c>, 见 <see cref="DetachIdleClients"/>):
    /// 本属性记的是「池侧记账动作」(Attach / BeginAcquire / Release)的时刻, 看不到一次工具调用
    /// 从开始到结束的区间。若拿它判空闲, 一个正在跑子代理的 Worker 会在 1 分钟后被误杀。
    /// 本属性现在只用于诊断展示(见 <see cref="SnapshotHealth"/>)。
    /// </remarks>
    public long LastActivityTicks
    {
        get { lock (_gate) { return _lastActivityTicks; } }
    }

    /// <summary>更新组级活动时间。调用方必须已在 <c>_gate</c> 内(或独占该组的写路径)。</summary>
    internal void TouchLocked(long nowTicks)
    {
        if (nowTicks > _lastActivityTicks) _lastActivityTicks = nowTicks;
    }

    /// <summary>本组成功重拉次数之和。</summary>
    public int RestartsTotal
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var slot in _slots.Values) n += slot.RestartCount;
                return n;
            }
        }
    }

    // ── 引用计数 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 把会话挂进本组(引用计数 +1), 已存在则刷新其工作目录与活动时间。
    /// </summary>
    /// <returns>
    /// false = 本组正在退役, 调用方(<see cref="WorkerPool.AcquireAsync"/>)必须换一个组实例重试。
    /// </returns>
    /// <remarks>
    /// <b>为什么"已存在也返回 true"</b>: 每次 <c>AcquireAsync</c>(即每次需要工具的回合)都会调它,
    /// 它表达的是"这个会话在本目录有一份引用", 而不是"新建了一个引用"。
    /// 重复挂载必须幂等, 否则多开几个回合就能把引用计数刷上去, L3 永远不触发。</remarks>
    internal bool Attach(string sessionId, string workDir, long nowTicks)
    {
        lock (_gate)
        {
            if (_retiring) return false;

            if (_slots.TryGetValue(sessionId, out var slot))
            {
                slot.WorkDir = workDir;
                slot.Released = false;
                slot.LastActivity = nowTicks;
                TouchLocked(nowTicks);
                return true;
            }

            _slots[sessionId] = new Slot
            {
                SessionId = sessionId,
                WorkDir = workDir,
                LastActivity = nowTicks
            };
            TouchLocked(nowTicks);
            return true;
        }
    }

    /// <summary>
    /// 摘除一个会话(引用计数 -1)。计数归零时由池决定是否退役整组。
    /// </summary>
    /// <param name="detached">被摘下的 Worker 句柄(可能是 null, 也可能是内联降级的句柄), <b>由调用方在锁外关闭</b>。</param>
    /// <returns>该会话此前是否在本组内。</returns>
    internal bool Release(string sessionId, out WorkerClient? detached)
    {
        lock (_gate)
        {
            detached = null;
            if (!_slots.Remove(sessionId, out var slot)) return false;

            // Released 必须在清 InFlight 之前置位: 在飞拉起完成时会看到它, 于是自己关掉自己拉起的进程,
            // 而不是发布一个"没人要的 Worker"(那会留下占着仓库 git 锁的孤儿进程)。
            slot.Released = true;
            slot.InFlight = null;

            detached = slot.Client;
            slot.Client = null;
            slot.ProcessId = null;
            return true;
        }
    }

    // ── Acquire 的状态机 ─────────────────────────────────────────────────────

    /// <summary>
    /// 在锁内判定本次 <c>AcquireAsync</c> 该走哪条路, 并(需要时)登记在飞拉起。
    /// 拉起本身<b>绝不</b>在锁内发生(见类型 remarks 的锁纪律)。
    /// </summary>
    internal WorkerAcquirePlan BeginAcquire(string sessionId, long nowTicks)
    {
        lock (_gate)
        {
            if (_retiring) return WorkerAcquirePlan.GroupRetired();

            if (!_slots.TryGetValue(sessionId, out var slot))
            {
                // 理论不可达: WorkerPool 先 Attach 再 BeginAcquire。保守起见按"可拉起"处理,
                // 而不是抛异常 —— 生命周期代码抛异常会一路冒到引擎的工具执行里, 变成用户可见的失败。
                var fresh = new Slot { SessionId = sessionId, LastActivity = nowTicks };
                _slots[sessionId] = fresh;
                slot = fresh;
                TouchLocked(nowTicks);
            }

            // ① 已连接且**不是内联兜底** → 复用。每个回合都会命中这一行, 它是整条链路上最热的路径。
            //    ⚠️ `!slot.IsInline` 不可省: 拉起失败时会先给一个内联兜底句柄让本回合能用工具,
            //    而那个句柄 IsConnected 恒为 true。若这里不看 IsInline, 它会永久挡住 ④⑤
            //    —— 于是「Worker 第一次没起来」就变成"整个会话生命周期都再也不重试"，
            //    L5 的崩溃重拉与退避逻辑全部失效, 且症状是"悄悄退回单进程"。
            if (slot.Client is { IsConnected: true } && !slot.IsInline)
            {
                return WorkerAcquirePlan.Reuse(slot.Client);
            }

            // ② 同一会话的另一条 Acquire 正在拉起 → 等它, 绝不并发拉起第二个进程。
            //    没有这一步, 两个并发的工具调用(SubagentGroupTool 的并发闸就是 4)
            //    会为同一个会话起两个 Worker, 其中一个立刻变孤儿。
            if (slot.InFlight is not null)
            {
                return WorkerAcquirePlan.Join(slot.InFlight);
            }

            // ③ 已钉死降级 → 永不重试。若已持有内联句柄就复用它(避免每回合新建一个传输)。
            if (slot.Degraded)
            {
                if (slot.Client is { IsConnected: true } sticky)
                {
                    return WorkerAcquirePlan.Reuse(sticky);
                }

                return WorkerAcquirePlan.Degrade(
                    slot.LastSpawnError ?? "Worker 已降级为进程内执行");
            }

            // ④ 退避中 → 本次不拉起, 退回进程内执行。
            //    ⚠️ 刻意**不等**退避窗口走完: 等完等于把这一回合的工具调用挂起最多 60s,
            //    用户看到的是"点了发送没反应"。退避只该约束"再次拉起进程"这件事,
            //    不该约束"这一回合能不能用工具"。
            //    顺带: 退避期内的内联句柄是**一次性**的(不入槽), 退避一过就会重新尝试拉起。
            if (nowTicks < slot.NextAttemptAt)
            {
                var waitMs = slot.NextAttemptAt - nowTicks;
                return WorkerAcquirePlan.Degrade(
                    $"上次拉起失败, 退避中(还需 {(waitMs / 1000.0):0.#}s)");
            }

            // ⑤ 可以拉起: 登记在飞任务, 让并发 Acquire 汇合到它。
            var tcs = new TaskCompletionSource<WorkerClient>(TaskCreationOptions.RunContinuationsAsynchronously);
            slot.InFlight = tcs;
            slot.LastActivity = nowTicks;
            TouchLocked(nowTicks);
            return WorkerAcquirePlan.Proceed(tcs);
        }
    }

    /// <summary>
    /// 拉起成功后发布句柄。
    /// </summary>
    /// <param name="published">
    /// true = 已发布到槽位, 调用方可以把它交给本次 <c>AcquireAsync</c> 的调用者;
    /// false = <b>没能</b>发布(会话已 <see cref="Release"/> 或整组已退役),
    /// 调用方必须自己关闭 <paramref name="client"/>, 并把一个"已断开"的交代给本回合
    /// —— 绝不能把一个没人要的 Worker 留在池里(它会一直占着仓库的 git 写锁)。
    /// </param>
    /// <param name="superseded">被本次发布顶掉的旧句柄, 调用方在锁外关闭(可能为 null)。</param>
    internal bool CompleteSpawn(
        string sessionId,
        WorkerClient client,
        int? processId,
        out WorkerClient? superseded,
        long nowTicks)
    {
        lock (_gate)
        {
            superseded = null;
            var slot = EnsureSlotLocked(sessionId, nowTicks);
            slot.InFlight = null;

            if (slot.Released || _retiring) return false;

            superseded = slot.Client is not null && !ReferenceEquals(slot.Client, client)
                ? slot.Client
                : null;

            slot.Client = client;
            slot.IsInline = false;
            slot.ProcessId = processId ?? client.ProcessId;

            // 一次成功即清零失败计数: 退避是针对"连续失败"的, 让上一次的历史继续压制下一次尝试没有意义。
            slot.SpawnAttempts = 0;
            slot.LastSpawnError = null;
            slot.NextAttemptAt = 0;

            if (slot.RestartPending)
            {
                slot.RestartCount++;
                slot.RestartPending = false;
            }

            slot.LastActivity = nowTicks;
            TouchLocked(nowTicks);
            return true;
        }
    }

    /// <summary>
    /// 拉起失败后发布<b>进程内降级</b>句柄: 本回合仍然有工具可用(行为等同引入 Worker 之前),
    /// 只是失去崩溃隔离。同时记录失败原因、按 <paramref name="backoffFor"/> 设置退避截止。
    /// </summary>
    /// <param name="backoffFor">
    /// 按「累计失败次数」算退避时长的委托(见 <see cref="WorkerPool"/> 的「退避策略」)。
    /// 传委托而不是算好的毫秒数, 是为了让退避曲线与失败计数<b>始终一致</b> ——
    /// 在池里预先算好就等于让调用方自己知道失败次数, 两处一漂移就会出现"第一次失败就等 60s"。
    /// </param>
    /// <param name="newlyDegraded">本次是否刚刚触达失败上限, 由调用方负责打那条唯一的降级 <c>Log.Warn</c>。</param>
    /// <param name="superseded">被顶掉的旧句柄(调用方在锁外关闭)。</param>
    internal bool PublishInline(
        string sessionId,
        WorkerClient inlineClient,
        string error,
        bool terminal,
        Func<int, long> backoffFor,
        int maxAttempts,
        long nowTicks,
        out bool newlyDegraded,
        out WorkerClient? superseded)
    {
        ArgumentNullException.ThrowIfNull(backoffFor);

        lock (_gate)
        {
            newlyDegraded = false;
            superseded = null;
            var slot = EnsureSlotLocked(sessionId, nowTicks);
            slot.InFlight = null;

            slot.LastSpawnError = error;

            // terminal(定位未命中 / 协议版本不符)直接钉死: 同一个二进制、同一份缺失的产物,
            // 重试 N 次的结果完全可预测, 退避只是让用户多等几轮再看到同一句话。
            slot.SpawnAttempts = terminal ? maxAttempts : slot.SpawnAttempts + 1;

            if (slot.SpawnAttempts >= maxAttempts)
            {
                slot.NextAttemptAt = 0; // 已钉死, 退避字段不再有意义
                if (!slot.Degraded)
                {
                    slot.Degraded = true;
                    newlyDegraded = true;
                }
            }
            else
            {
                slot.NextAttemptAt = nowTicks + backoffFor(slot.SpawnAttempts);
            }

            if (slot.Released || _retiring) return false;

            superseded = slot.Client is not null && !ReferenceEquals(slot.Client, inlineClient)
                ? slot.Client
                : null;

            // ⚠️ 内联句柄只在**已钉死降级**时才入槽; 退避期的那一份是一次性的。
            // 入槽会让 BeginAcquire 的 ① 命中一个 IsConnected 恒为 true 的句柄, 于是再也走不到
            // ④⑤ —— 「Worker 第一次没起来」就会变成"这个会话永远不再重试", L5 与退避全部失效。
            // 钉死之后入槽则相反是必要的: 避免每回合都新建一个 InlineTransport。
            slot.Client = slot.Degraded ? inlineClient : null;
            slot.IsInline = slot.Degraded;
            slot.ProcessId = slot.Degraded ? inlineClient.ProcessId : null;

            slot.LastActivity = nowTicks;
            TouchLocked(nowTicks);
            return true;
        }
    }

    /// <summary>
    /// 取消一次拉起(调用方令牌被取消): 清在飞登记并记原因, <b>不</b>消耗失败次数。
    /// </summary>
    /// <remarks>
    /// 取消不是"Worker 起不来" —— 用户点停止不该把会话推进降级路径。
    /// 否则一次随手点的停止就会永久削掉这个会话的隔离能力, 而且没有任何解释。</remarks>
    internal void CancelSpawn(string sessionId, string reason)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(sessionId, out var slot)) return;
            slot.InFlight = null;
            slot.LastSpawnError = reason;
        }
    }

    /// <summary>
    /// 记录一次连接级故障(<see cref="WorkerClient.Faulted"/>)并把该槽位的句柄摘下。
    /// </summary>
    /// <param name="faulted">触发故障的那个句柄。<b>必须传它自己</b>:
    /// 重拉完成后旧 Worker 的 <c>Faulted</c> 可能迟到, 若不校验引用相等,
    /// 一个迟到的故障事件会把<b>刚拉好的新句柄</b>摘掉, 表现为"每次刚重启就又掉线"的死循环。</param>
    /// <param name="detached">被摘下的死句柄, 调用方<b>在锁外延迟</b>关闭(见方法 remarks)。</param>
    internal bool MarkFaulted(string sessionId, WorkerClient faulted, string reason, out WorkerClient? detached)
    {
        lock (_gate)
        {
            detached = null;
            if (!_slots.TryGetValue(sessionId, out var slot)) return false;

            slot.LastFault = reason;

            if (!ReferenceEquals(slot.Client, faulted))
            {
                // 迟到的故障事件(旧 Worker): 只留档, 不动当前句柄。
                return false;
            }

            slot.RestartPending = true;
            slot.Client = null;
            slot.ProcessId = null;
            detached = faulted;
            return true;
        }
    }

    // ── 关闭与退役 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 标记本组退役并摘下全部槽位, 返回需要关闭的句柄。
    /// </summary>
    /// <remarks>
    /// <b>为什么不直接在这里 await 关闭</b>: 关闭是 IO(可能每个 Worker 都要花
    /// <see cref="WorkerProtocol.ShutdownTimeout"/>), 而方法是同步的且由调用方在锁外调用。
    /// 返回句柄让 <see cref="WorkerPool"/> 决定并发度 —— 关 N 个 Worker 用并发而非串行,
    /// 否则最坏耗时是 N × ShutdownTimeout。</remarks>
    internal WorkerClient[] BeginRetire()
    {
        lock (_gate)
        {
            _retiring = true;
            var clients = new List<WorkerClient>(_slots.Count);
            foreach (var slot in _slots.Values)
            {
                slot.Released = true;
                slot.InFlight = null;
                if (slot.Client is not null)
                {
                    clients.Add(slot.Client);
                    slot.Client = null;
                }
                slot.ProcessId = null;
            }

            _slots.Clear();
            return clients.ToArray();
        }
    }

    /// <summary>
    /// reconcile: 与「当前实际存在的会话集合」对账, 把不在其中的会话全部摘掉并返回待关闭的句柄。
    /// </summary>
    /// <remarks>
    /// <para><b>⚠️ 这是 L3 的防漏兜底, 而它的成立有一个承重墙前提</b>:
    /// 「目录完全没有会话 → 关掉该目录全部 Worker」这条硬规则, 依赖<b>每个会话最终都会 -1</b>。
    /// 但本仓当前:</para>
    /// <list type="bullet">
    /// <item><c>SessionRuntimeRegistry.RemoveSession</c> —— <b>零生产调用方</b>(GUI 删除会话目前不走它),</item>
    /// <item><c>SessionRuntimeRegistry.Dispose</c> —— <b>零生产调用方</b>(<c>CommanderRuntime</c> 自身没有 Dispose,
    /// 主进程退出靠进程消亡回收, 没人通知注册表)。</item>
    /// </list>
    /// <para>也就是说: 只要有任何一条路径漏掉 <c>-1</c>, 目录就永远不归零, Worker 进程一直残留,
    /// 而且<b>没有任何现象提示</b>(工具照常工作, 只是 <c>ps</c> 里多了一堆进程)。
    /// 因此 <see cref="WorkerPool"/> 必须每 60s 调一次本方法, 并由调用方提供
    /// 「当前实际存在的会话集合」(应取自 <c>SessionRuntimeRegistry.AllSessions</c>)。</para>
    ///
    /// <para><b>比对基准为什么只按会话 Id</b>(不按 (目录, 会话)): 会话换工作目录时(L4)旧目录里的那份
    /// 自然就"不在 <c>liveSessionIds</c> 里"了 —— 因为会话 Id 在池里是全局唯一的槽位键。
    /// 若按 (目录, 会话) 比对, 换目录反而会让新旧两份同时存活。</para>
    ///
    /// <para><b>在飞拉起与本方法的交互</b>: 被摘下的槽位带 <c>Released = true</c>,
    /// 在飞拉起完成时会看到它并自行关闭 —— 不会出现"reconcile 之后又多出一个 Worker"。</para>
    /// </remarks>
    public IReadOnlyList<ReclaimedSession> Reconcile(IReadOnlySet<string> liveSessionIds)
    {
        ArgumentNullException.ThrowIfNull(liveSessionIds);

        var reclaimed = new List<ReclaimedSession>();
        lock (_gate)
        {
            // ⚠️ 必须先快照再遍历: 边遍历边 Remove 会抛 InvalidOperationException,
            // 而这正好发生在"回收"这个最需要可靠的时刻(与 McpClientBase.FailAllPending 同款纪律)。
            foreach (var sessionId in _slots.Keys.ToList())
            {
                if (liveSessionIds.Contains(sessionId)) continue;

                // TryRemove 而不是 Remove(key, out value): 后者在键不存在时会把 out 参数置 null,
                // 而本方法已经用 Contains 判过 —— 双保险: 键真的不在(理论不可能)时直接跳过,
                // 不去解引用 null 槽位。
                if (!_slots.TryGetValue(sessionId, out var slot)) continue;
                _slots.Remove(sessionId);

                slot.Released = true;
                slot.InFlight = null;

                if (slot.Client is null) continue;
                reclaimed.Add(new ReclaimedSession(sessionId, slot.Client));
                slot.Client = null;
            }
        }

        return reclaimed;
    }

    // ── 槽位级空闲回收（与 Reconcile 是两件不同的事） ─────────────────────────

    /// <summary>
    /// 收集「连续空闲超过 <paramref name="timeout"/>」的槽位并<b>摘下它们的 Worker 句柄</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>与 <see cref="Reconcile"/> 的区别（关键，别混为一谈）</b>：
    /// Reconcile 处理的是「**会话已经不存在了**」，摘的是整个<b>槽位</b>；
    /// 本方法处理的是「**会话还在，只是没在干活**」，只摘<b>Worker 句柄</b>，<b>保留槽位</b>。
    /// 保留槽位意味着会话仍登记在本组里 —— 下次 <c>AcquireAsync</c> 会走"懒重建"路径
    /// 拉起一个新的 Worker，而不是把会话当成外来者重新挂载。</para>
    ///
    /// <para><b>空闲判据是两个条件的合取，缺一不可</b>：
    /// <list type="number">
    /// <item><c>client.IsBusy == false</c>（<c>ActiveCallCount == 0</c>）——
    /// 这一条是<b>正确性底线</b>。子代理工具合法跑 30 分钟，只看"距上次 Acquire 多久"
    /// 会在它跑到 1 分钟时把它当成空闲杀掉，子代理进程随之变孤儿、
    /// 且它持有的仓库 git 写锁要等到超时才释放。</item>
    /// <item><c>now - client.LastActivityTicks &gt;= timeout</c>——
    /// 这一条才对应用户说的"1 分钟无任务"。活动时间由 <c>WorkerClient</c> 在
    /// 调用开始/结束、实时输出到达、工具集同步时刷新。</item>
    /// </list></para>
    ///
    /// <para><b>为什么还要额外判「拉起中」</b>：<c>InFlight != null</c> 表示有人正在 spawn。
    /// 那句柄还没拿到，<c>Client</c> 仍是上一次那个（可能已被判空闲）——
    /// 不跳过就会在拉起窗口里把"即将交付的新 Worker"误收。</para>
    /// </remarks>
    /// <param name="timeout">空闲阈值；<c>&lt;= TimeSpan.Zero</c> 表示不启用（由调用方保证）。</param>
    /// <param name="now">当前 <see cref="Environment.TickCount64"/>。</param>
    /// <returns>被摘下的句柄，<b>由调用方在锁外关闭</b>。</returns>
    internal List<WorkerClient> DetachIdleClients(TimeSpan timeout, long now)
    {
        var detached = new List<WorkerClient>();
        if (timeout <= TimeSpan.Zero) return detached;

        var threshold = (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            foreach (var slot in _slots.Values)
            {
                var client = slot.Client;
                if (client is null) continue;          // 本来就没有 Worker(内联降级 / 已摘过)
                if (slot.InFlight is not null) continue; // 正在拉起: 别动
                if (slot.TurnActive) continue;          // 回合仍在进行(见 MarkTurnActive)

                if (client.IsBusy) continue;           // 有在飞调用: 绝不关
                if (now - client.LastActivityTicks < threshold) continue;

                // 摘句柄但**保留槽位**: 会话仍归属本组, 下次 Acquire 会懒重建。
                slot.Client = null;
                slot.ProcessId = null;
                detached.Add(client);
            }
        }

        return detached;
    }

    /// <summary>
    /// 标记某个会话「当前有一个引擎回合在跑」。回合进行期间即使没有在飞工具调用,
    /// 也不参与空闲回收 —— 否则一个正在流式输出 LLM 答案的回合, 会在两次工具调用之间的
    /// 空档被当成空闲, 于是这一回合的下一次工具调用要付一次重新拉起的代价。
    /// </summary>
    /// <remarks>
    /// 这是<b>性能优化而非正确性要求</b>：不接它最多是「首个工具调用慢几百毫秒」；
    /// 接错的代价更大（把正在跑的回合标记成不活动 → 回合中途 Worker 被回收）。
    /// 调用方应在回合开始时置 true、结束时置 false，且必须保证成对（<c>try/finally</c>）。
    /// </remarks>
    internal void MarkTurnActive(string sessionId, bool active)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(sessionId, out var slot))
            {
                slot.TurnActive = active;
                TouchLocked(nowTicks: Environment.TickCount64);
            }
        }
    }

    /// <summary>
    /// 并发关闭一批 Worker 句柄: 每个都走「<c>ShutdownAsync</c>(限时) → <c>DisposeAsync</c>(限时)」。
    /// 返回的已完成任务<b>不代表已关完</b>(见 remarks); 需要等待请用 <see cref="CloseClientsAwaitedAsync"/>。
    /// </summary>
    /// <remarks>
    /// <para><b>关闭顺序: 先 Shutdown 后 Dispose, 不可颠倒。</b>
    /// <c>ShutdownAsync</c> 给 Worker 一个收尾窗口(协议级 <c>worker/shutdown</c>),
    /// 让它释放自己持有的文件句柄、结束自己的 git 操作; <c>DisposeAsync</c> 是父进程单方面的
    /// 强制收尾(退订事件、断开管道、杀进程)。先 Dispose 再 Shutdown 等于直接跳过了收尾窗口,
    /// Worker 侧任何"退出前落盘/清理"的动作都会丢。</para>
    ///
    /// <para><b>并发而不是串行</b>: 各 Worker 是互相独立的进程, 关 A 与关 B 没有先后依赖;
    /// 串行的最坏耗时是 N × <see cref="WorkerProtocol.ShutdownTimeout"/>,
    /// 10 个会话的目录归零就要 50s —— 而这条路径在"用户一次关掉整个侧栏"时会同步发生。</para>
    ///
    /// <para><b>⚠️ 单个 Worker 的失败绝不阻止其余 Worker 被关闭</b>:
    /// 每个 Worker 各自包一层 try/catch。若让异常冒泡出去 <c>Task.WhenAll</c>, 先失败的那个会让
    /// 其余 Worker <b>永远拿不到 DisposeAsync</b> —— 泄漏的恰恰是那些还没关的进程。
    /// 泄漏比"关得慢"糟得多, 所以这里的选择是明确的。</para>
    ///
    /// <para><b>返回已完成的任务而不是等待</b>: 调用点在 GUI 的会话删除回调与对账循环上,
    /// 在那里等 N × <see cref="WorkerProtocol.ShutdownTimeout"/> 会直接卡界面 / 拖慢对账周期。
    /// 每个 <c>CloseOneAsync</c> 内部已各自吞掉全部异常, 所以「不等待」不等于「不记日志」。</para>
    ///
    /// <para><b>为什么 Dispose 也要限时</b>: <c>DisposeAsync</c> 不可取消, 一个半死的传输实现
    /// 完全可能永远不返回。而这条路径位于应用退出与目录归零上, 不能被单个 Worker 无限期钉住。
    /// 超时后我们只记一条 Warn 并放弃等待(那个任务仍在跑, 但它的异常已被 Observe 掉);
    /// 进程残留留给运维手段处理, 好过整个 GUI 退不出去。</para>
    /// </remarks>
    internal static Task CloseClientsAsync(IReadOnlyList<WorkerClient> clients, CancellationToken ct)
    {
        if (clients is null || clients.Count == 0) return Task.CompletedTask;

        var tasks = new List<Task>(clients.Count);
        foreach (var client in clients)
        {
            var captured = client;
            tasks.Add(Task.Run(() => CloseOneAsync(captured, ct), CancellationToken.None));
        }

        return Task.WhenAll(tasks);
    }

    /// <summary>并发等待式关闭全部(供 <c>ShutdownAllAsync</c> 这类"必须关完才能退出"的路径)。</summary>
    internal static async Task CloseClientsAwaitedAsync(IReadOnlyList<WorkerClient> clients, CancellationToken ct)
    {
        if (clients is null || clients.Count == 0) return;

        var tasks = new List<Task>(clients.Count);
        foreach (var client in clients)
        {
            var captured = client;
            tasks.Add(CloseOneAsync(captured, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task CloseOneAsync(WorkerClient client, CancellationToken ct)
    {
        try
        {
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            shutdownCts.CancelAfter(WorkerProtocol.ShutdownTimeout);
            await client.ShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 两种成因: 调用方主动取消(应用退出), 或上面 CancelAfter 到了 ShutdownTimeout。
            // 都不是"无需处理" —— 但都必须继续往下走 DisposeAsync, 否则进程泄漏。
            Log.Debug(LogCategory,
                $"Worker {client.WorkerKey} 的关闭请求被取消/超时, 直接进入强制释放");
        }
        catch (Exception ex)
        {
            // 关键: 吞掉并继续 DisposeAsync —— 半死的 Worker 恰恰是最需要被强制回收的那个。
            Log.Warn(LogCategory, ex, $"Worker {client.WorkerKey} 优雅关闭失败, 改为强制释放");
        }

        try
        {
            var disposing = client.DisposeAsync().AsTask();
            var finished = await Task
                .WhenAny(disposing, Task.Delay(WorkerProtocol.ShutdownTimeout, CancellationToken.None))
                .ConfigureAwait(false);

            if (finished != disposing)
            {
                ObserveFault(disposing);
                Log.Warn(LogCategory, $"Worker {client.WorkerKey} 释放超时({WorkerProtocol.ShutdownTimeout.TotalSeconds:0}s), 已放弃等待");
                return;
            }

            await disposing.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, ex, $"Worker {client.WorkerKey} 释放失败(进程可能已残留)");
        }
    }

    // ── 诊断 ─────────────────────────────────────────────────────────────────

    /// <summary>逐槽位健康快照(锁内构造, 返回后不再触碰组状态)。</summary>
    internal WorkerHealthEntry[] SnapshotHealth()
    {
        lock (_gate)
        {
            var entries = new WorkerHealthEntry[_slots.Count];
            var i = 0;
            foreach (var s in _slots.Values)
            {
                var client = s.Client;
                var connected = client is { IsConnected: true };

                // ⚠️ "工具此刻在主进程里跑"有两种形态, 都必须报成 Inline:
                // ① 已钉死降级(slot 里存着内联句柄); ② 退避期内(内联句柄是一次性的, 不入槽)。
                // 只报 ① 会造成一个可见性缺口: 第一次拉起失败之后、真正钉死之前的那段时间里,
                // 工具已经在主进程里跑了, 而状态栏与 doctor 都说"没连上" —— 那正是
                // 「悄悄退回单进程」这个最需要被看见的回归。
                var servingInline = s.Degraded || (!connected && s.SpawnAttempts > 0);
                var mode = servingInline ? WorkerMode.Inline
                    : connected ? WorkerMode.Pipe
                    : WorkerMode.NotConnected;

                entries[i++] = new WorkerHealthEntry
                {
                    WorkerKey = client?.WorkerKey ?? string.Empty,
                    WorkDir = string.IsNullOrEmpty(s.WorkDir) ? DirectoryKey : s.WorkDir,
                    SessionId = s.SessionId,
                    Mode = mode,
                    IsConnected = connected,
                    ProcessId = s.ProcessId,
                    LastFault = s.LastFault ?? client?.LastFault,
                    SpawnAttempts = s.SpawnAttempts,
                    LastSpawnError = s.LastSpawnError,
                    RestartCount = s.RestartCount,
                    HasPendingSpawn = s.InFlight is not null,
                    LastActivityTicks = s.LastActivity == 0 ? null : s.LastActivity
                };
            }

            return entries;
        }
    }

    // ── 内部类型 ─────────────────────────────────────────────────────────────

    private Slot EnsureSlotLocked(string sessionId, long nowTicks)
    {
        if (_slots.TryGetValue(sessionId, out var slot)) return slot;
        slot = new Slot { SessionId = sessionId, WorkDir = DirectoryKey, LastActivity = nowTicks };
        _slots[sessionId] = slot;
        return slot;
    }

    private static void ObserveFault(Task task)
        => _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>一个会话在组内的槽位。字段全部只在 <c>_gate</c> 内访问。</summary>
    private sealed class Slot
    {
        public string SessionId = string.Empty;

        /// <summary>会话当前所在的工作目录(规范化)。换目录时由 <see cref="Attach"/> 刷新。</summary>
        public string WorkDir = string.Empty;

        /// <summary>当前持有的句柄(管道连接, 或**钉死后**的内联降级句柄); 已断开 / 已释放 / 退避期内为 null。</summary>
        public WorkerClient? Client;

        /// <summary>
        /// 当前句柄是否是<b>内联降级</b>句柄。仅在 <see cref="Degraded"/> 为 true 时才可能为 true ——
        /// 它存在的唯一目的是让 <see cref="BeginAcquire"/> 的 ① 分得清「可以复用的真实连接」与
        /// 「不该永久复用的内联兜底」。
        /// </summary>
        public bool IsInline;

        /// <summary>是否已触达失败上限(粘滞降级标记; 与 <see cref="IsInline"/> 区别在于它管的是"能不能再拉起")。</summary>
        public bool Degraded;

        /// <summary>在飞拉起的登记点; 并发 <c>AcquireAsync</c> 汇合到它的 <see cref="Task"/>。</summary>
        public TaskCompletionSource<WorkerClient>? InFlight;

        /// <summary>已被 <see cref="Release"/> 或 <see cref="BeginRetire"/> 摘除 —— 在飞拉起据此自行收尾。</summary>
        public bool Released;

        /// <summary>是否需要在下一次拉起时计入"重拉"。</summary>
        public bool RestartPending;

        /// <summary>连续拉起失败次数(成功后清零)。</summary>
        public int SpawnAttempts;

        /// <summary>退避截止时刻(<see cref="Environment.TickCount64"/> 基准, 单调时钟)。</summary>
        public long NextAttemptAt;

        public int RestartCount;

        public long LastActivity;

        /// <summary>
        /// 该会话当前是否有引擎回合在跑(见 <see cref="MarkTurnActive"/>)。
        /// 回合进行期间不参与空闲回收。
        /// </summary>
        public bool TurnActive;

        public string? LastFault;

        public string? LastSpawnError;

        public int? ProcessId;
    }

    /// <summary>reconcile 回收掉的一个会话及其待关闭句柄。</summary>
    /// <param name="SessionId">被回收的会话 Id。</param>
    /// <param name="Client">该会话持有的 Worker 句柄, 调用方负责在锁外关闭。</param>
    public readonly record struct ReclaimedSession(string SessionId, WorkerClient Client);
}

/// <summary>
/// <see cref="WorkerDirectoryGroup.BeginAcquire"/> 的判定结果。
/// </summary>
/// <remarks>
/// 做成"值 + 状态"而不是在方法里直接干活, 是为了让 <b>锁内只出判决</b>:
/// 复用、等待、拉起、退役四种路径里只有"等待"需要 await, 其余都要回到锁外执行 IO。</remarks>
internal readonly record struct WorkerAcquirePlan
{
    public WorkerAcquireState State { get; init; }

    /// <summary><see cref="WorkerAcquireState.Reuse"/> 时非 null。</summary>
    public WorkerClient? Client { get; init; }

    /// <summary>
    /// 在飞拉起的<b>完成源</b>(<see cref="WorkerAcquireState.Join"/> 与
    /// <see cref="WorkerAcquireState.Proceed"/> 时非 null)。
    /// </summary>
    /// <remarks>
    /// 给 TCS 而不是给 <c>Task</c>: <see cref="WorkerAcquireState.Proceed"/> 的发起方必须<b>亲手</b>
    /// 完结它(成功、降级、取消三条路径都必须), 而只暴露 <c>Task</c> 就没有任何办法通知汇合者 ——
    /// 它们会永远等下去, 表现是「同一会话的第二个工具调用卡到用户点停止」。
    /// </remarks>
    public TaskCompletionSource<WorkerClient>? Completion { get; init; }

    /// <summary>本次 Acquire 要等待的在飞拉起任务; 无在飞时为 null。</summary>
    public Task<WorkerClient>? InFlight => Completion?.Task;

    /// <summary><see cref="WorkerAcquireState.Degrade"/> 时的原因(会写进 doctor 与 <c>Log.Warn</c>)。</summary>
    public string? Reason { get; init; }

    /// <summary>已有可用的连接(含内联降级句柄), 直接返回。</summary>
    public static WorkerAcquirePlan Reuse(WorkerClient client) =>
        new() { State = WorkerAcquireState.Reuse, Client = client };

    /// <summary>同一会话已有一次拉起在飞, 等它。</summary>
    public static WorkerAcquirePlan Join(TaskCompletionSource<WorkerClient> inFlight) =>
        new() { State = WorkerAcquireState.Join, Completion = inFlight };

    /// <summary>登记一次新的拉起; 返回值里的 <see cref="Completion"/> 必须由调用方完结。</summary>
    public static WorkerAcquirePlan Proceed(TaskCompletionSource<WorkerClient> tcs) =>
        new() { State = WorkerAcquireState.Proceed, Completion = tcs };

    /// <summary>本次不拉起, 退回进程内执行。</summary>
    public static WorkerAcquirePlan Degrade(string reason) =>
        new() { State = WorkerAcquireState.Degrade, Reason = reason };

    /// <summary>本组正在退役, 调用方应换一个组实例重试。</summary>
    public static WorkerAcquirePlan GroupRetired() =>
        new() { State = WorkerAcquireState.GroupRetired, Reason = "目录正在退役" };
}

/// <summary><see cref="WorkerAcquirePlan"/> 的状态枚举。</summary>
internal enum WorkerAcquireState
{
    /// <summary>复用已有的连接。</summary>
    Reuse,

    /// <summary>汇合到同会话在飞的那次拉起。</summary>
    Join,

    /// <summary>发起一次新的拉起。</summary>
    Proceed,

    /// <summary>退回进程内执行(退避中 / 已钉死降级 / 定位未命中)。</summary>
    Degrade,

    /// <summary>组已退役, 换组重试。</summary>
    GroupRetired
}