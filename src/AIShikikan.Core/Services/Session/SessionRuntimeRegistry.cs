using System.Text.Json;
using System.Threading.Channels;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Core.Services.Session;

/// <summary>引擎宿主: 引擎在回合开始/结束时经此向工作区协调器申请执行权。</summary>
public interface ISessionEngineHost
{
    /// <summary>申请回合执行权; 拒绝时返回 false 并给出用户可读原因。</summary>
    bool TryBeginTurn(AgentEngine engine, string turnId, string? workDir,
        out string? blockedReason, out WorkspaceActivity? activity);

    /// <summary>回合结束释放执行权(activity 为申请到的活动条目)。</summary>
    void EndTurn(AgentEngine engine, WorkspaceActivity? activity);
}

/// <summary>会话运行时工厂: 由 CommanderRuntime 注入(闭包捕获 LLM/工具集等装配依赖)。</summary>
public delegate AgentEngine EngineSessionFactory(string sessionId, string sessionTitle, ISessionEngineHost host);

/// <summary>单个会话的运行时: 独立引擎(含独立对话/选项)、独立运行状态与取消入口。</summary>
public sealed class SessionRuntime : ISessionEngineHost, IDisposable
{
    private readonly WorkspaceExecutionCoordinator _coordinator;
    private readonly object _lock = new();

    // 同会话回合串行队列: 工作区协调器只管"同工作树同分支"这一层, 允许多个会话并发;
    // 但同一会话并发跑两个回合会让引擎的对话历史(普通 List, 非线程安全)被并发写、
    // 并让 _currentTurnId 互相覆盖, 因此这里再加一层会话内串行。
    private readonly Channel<PendingTurn> _queue = Channel.CreateUnbounded<PendingTurn>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _pumpLock = new(); // 保护 _pump 的懒启动, 避免多线程重复启动消费者
    private Task? _pump;
    private int _pendingCount; // 排队等待中的回合数(不含正在执行的那个)
    private WorkspaceActivity? _activeActivity;
    private int _disposed;

    /// <summary>排队中的一个回合: 消息 + 图片 + 调用方的取消令牌 + 回填结果的完成源。</summary>
    private sealed record PendingTurn(
        string Message,
        IReadOnlyList<ChatImagePart>? Images,
        CancellationToken Ct,
        TaskCompletionSource<string> Completion);

    public SessionRuntime(string sessionId, string sessionTitle,
        EngineSessionFactory factory, WorkspaceExecutionCoordinator coordinator)
    {
        SessionId = sessionId;
        SessionTitle = sessionTitle;
        _coordinator = coordinator;
        Engine = factory(sessionId, sessionTitle, this);
        Engine.RawEvent += OnEngineRawEvent;
    }

    public string SessionId { get; }

    public string SessionTitle { get; private set; }

    /// <summary>本会话独立引擎(独立对话历史 / EngineOptions / Roster 快照)。</summary>
    public AgentEngine Engine { get; }

    /// <summary>本会话是否正在执行回合。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>进行中的回合 ID(无则 null)。</summary>
    public string? ActiveTurnId { get; private set; }

    /// <summary>当前排队等待执行中的回合数(不含正在执行的那个)。</summary>
    public int PendingTurnCount => Volatile.Read(ref _pendingCount);

    /// <summary>本会话持有的工作区执行权(无则 null)。</summary>
    public WorkspaceActivity? ActiveActivity
    {
        get { lock (_lock) return _activeActivity; }
    }

    /// <summary>会话运行状态变化(回合开始/结束), 供 UI/协调器订阅。
    /// 注意: 排队入队/出队不触发本事件(保持"回合开始/结束"语义不变), UI 需自行轮询 PendingTurnCount。</summary>
    public event Action<SessionRuntime>? StateChanged;

    /// <summary>引擎原始事件转发(注册表用于后台会话用量落盘)。</summary>
    internal event Action<SessionRuntime, AgentEngineEvent>? RawEvent;

    /// <summary>会话工作目录(与引擎 Options.WorkDir 同步)。</summary>
    public string WorkDir => Engine.Options.WorkDir ?? string.Empty;

    /// <summary>更新会话标题(事件归属携带)。</summary>
    public void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        SessionTitle = title;
        Engine.SetSessionInfo(title, null);
    }

    bool ISessionEngineHost.TryBeginTurn(AgentEngine engine, string turnId, string? workDir,
        out string? blockedReason, out WorkspaceActivity? activity)
    {
        if (!_coordinator.TryBeginTurn(SessionId, string.IsNullOrWhiteSpace(workDir) ? WorkDir : workDir,
                out var act, out blockedReason))
        {
            activity = null;
            return false;
        }

        activity = act;
        lock (_lock)
        {
            _activeActivity = act;
            IsRunning = true;
            ActiveTurnId = turnId;
        }

        StateChanged?.Invoke(this);
        return true;
    }

    void ISessionEngineHost.EndTurn(AgentEngine engine, WorkspaceActivity? activity)
    {
        _coordinator.EndTurn(activity);

        bool stateChanged;
        lock (_lock)
        {
            // 身份校验(务必保留): 旧回合的 finally 完全可能晚于新回合的 TryBeginTurn 执行
            // (回合被外部取消后引擎仍要走完清理, 而新回合已在队列里取到执行权)。
            // 若不比对条目身份, 旧回合的收尾会把新回合刚写好的 _activeActivity / IsRunning /
            // ActiveTurnId 清空 —— UI 立刻显示"空闲"而引擎其实在跑, 停止按钮随之失效。
            // 协调器侧 End(activity) 同样只移除"这一条", 语义一致: 只有真正的持有者能释放自己的执行权。
            stateChanged = activity is null || ReferenceEquals(_activeActivity, activity);
            if (stateChanged)
            {
                _activeActivity = null;
                IsRunning = false;
                ActiveTurnId = null;
            }
        }

        if (stateChanged) StateChanged?.Invoke(this);
    }

    /// <summary>
    /// <b>停止整个会话</b>(终态取消): 取消引擎的会话级 CTS, 进行中的回合立即中止,
    /// <b>且该引擎此后无法再执行任何回合</b>(_sessionCts 只建一次、取消后不重置,
    /// 回合入口 ThrowIfCancellationRequested 直接抛)。恢复的唯一途径是
    /// <see cref="SessionRuntimeRegistry.RemoveSession"/> 后重新创建运行时。
    ///
    /// <para>⚠ 这是<b>终态</b>语义, 只在"会话被终止/移除"时使用。要"只停当前回合、
    /// 之后还能继续聊"请用 <see cref="StopCurrentTurn"/>。两者语义不同, 不要混用。</para>
    ///
    /// <para>⚠ GUI 聊天页的「停止」按钮<b>不应</b>接到这里: 它走 ChatPageViewModel 自己的
    /// 每会话 _turnCts, 语义是"停本回合"。若将来要统一, 应接 <see cref="StopCurrentTurn"/>。</para>
    /// </summary>
    public void Stop() => Engine.CancelSession();

    /// <summary>
    /// <b>只停止当前进行中的回合</b>(非终态): 取消引擎的 per-turn CTS,
    /// 排队中的下一回合与后续新回合仍可正常执行。
    /// 这是"停止生成"按钮应当使用的语义, 与 <see cref="Stop"/> 严格区分。
    /// </summary>
    public void StopCurrentTurn() => Engine.CancelTurn();

    /// <summary>
    /// 排入一个回合并等待其完成。同一会话的多个回合在此串行执行,
    /// 避免并发写引擎对话历史(此前会抛异常或静默丢消息)。
    /// </summary>
    public async Task<string> EnqueueTurnAsync(string message, IReadOnlyList<ChatImagePart>? images, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turn = new PendingTurn(message, images, ct, tcs);

        // 先自增再入队: 反过来写的话 pump 可能已取走该回合并先行自减, 计数会瞬时变负, UI 的排队指示会闪一下
        Interlocked.Increment(ref _pendingCount);
        if (!_queue.Writer.TryWrite(turn))
        {
            // 会话已释放(队列已关闭): 用无参 TrySetCanceled 兜底, 带令牌的写法在令牌未取消时
            // 会静默返回 false 导致 await 永久挂起
            Interlocked.Decrement(ref _pendingCount);
            tcs.TrySetCanceled();
            return await tcs.Task.ConfigureAwait(false);
        }

        EnsurePump();
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>懒启动队列消费者(单消费者, 会话生命周期内常驻)。</summary>
    private void EnsurePump()
    {
        lock (_pumpLock)
        {
            // writer 未关闭时 ReadAllAsync 会一直等待, 所以 pump 正常情况下不会自己结束;
            // 仍保留判空以防会话释放后又被排入
            if (_pump is not null) return;
            _pump = Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var turn in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // 出队即减: 计数只表示"还在排队等待"的回合, 不含正在执行的那个(与属性文档一致);
            // 若放到回合结束后再减, 正在执行的回合也会被算进去, UI 的排队指示会多算一条
            Interlocked.Decrement(ref _pendingCount);
            try
            {
                var reply = await Engine.RunTurnAsync(turn.Message, turn.Images, turn.Ct).ConfigureAwait(false);
                turn.Completion.TrySetResult(reply);
            }
            catch (OperationCanceledException)
            {
                // 注意用无参重载: 会话级 Stop() 取消的是引擎的 _sessionCts, 调用方令牌可能并未取消,
                // 带令牌写法此时会返回 false, 任务将永远挂起
                turn.Completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                turn.Completion.TrySetException(ex);
            }
        }
    }

    private void OnEngineRawEvent(AgentEngineEvent e) => RawEvent?.Invoke(this, e);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete(); // 关闭 writer: pump 排空队列后自行退出
        DrainPendingTurns(); // 引擎即将释放, 队列里还没开始的回合不再执行
        Engine.RawEvent -= OnEngineRawEvent;
        Engine.CancelSession(); // 进行中的回合随会话释放而终止
    }

    /// <summary>
    /// 释放队列里尚未开始的回合: 关闭 writer 后 pump 会把剩余项排干, 但此时引擎已在释放,
    /// 必须在这里把它们以取消态了结, 否则调用方的 await 会永久挂起。
    /// </summary>
    private void DrainPendingTurns()
    {
        while (_queue.Reader.TryRead(out var turn))
        {
            Interlocked.Decrement(ref _pendingCount);
            turn.Completion.TrySetCanceled();
        }
    }
}

/// <summary>会话运行时注册表: 每 SessionId 独立引擎/选项/对话/运行状态/事件,
/// 并维护"当前活动会话"(GUI 兼容访问 CommanderRuntime.Engine 即活动会话引擎)。</summary>
public sealed class SessionRuntimeRegistry : IDisposable
{
    /// <summary>兜底会话 ID(GUI 尚未选定会话时使用)。</summary>
    public const string FallbackSessionId = "__default__";

    private readonly Dictionary<string, SessionRuntime> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly EngineSessionFactory _factory;
    private readonly WorkspaceExecutionCoordinator _coordinator;
    private SessionRuntime? _fallback;
    private volatile string? _activeSessionId;
    private int _disposed;

    public SessionRuntimeRegistry(EngineEventHub hub, WorkspaceExecutionCoordinator coordinator,
        EngineSessionFactory factory)
    {
        Hub = hub;
        _coordinator = coordinator;
        _factory = factory;
        hub.ActiveSessionId = () => _activeSessionId;
    }

    public EngineEventHub Hub { get; }

    /// <summary>当前活动会话 ID(空表示未选定)。</summary>
    public string? ActiveSessionId => _activeSessionId;

    /// <summary>当前活动会话(未选定时为 null)。</summary>
    public SessionRuntime? Active => TryGet(_activeSessionId);

    /// <summary>兜底会话(保证 Engine 访问永不为 null)。</summary>
    public SessionRuntime Fallback
    {
        get
        {
            lock (_lock)
            {
                return _fallback ??= CreateRuntimeLocked(FallbackSessionId, "AI-Shikikan");
            }
        }
    }

    /// <summary>活动会话引擎(兼容访问: GUI 现有调用点全部指向它)。</summary>
    public AgentEngine ActiveEngine => (Active ?? Fallback).Engine;

    /// <summary>
    /// 全部会话运行时快照(<b>新代码请用这个名字</b>, 例如 Plan 模式需要同步到所有会话时遍历它)。
    /// 返回的是 <c>_sessions.Values</c> 的只读拷贝, 遍历期间即使有会话被创建/移除也不会失效。
    /// <para>注意: 只含"已按需创建"的运行时; GUI 侧的历史会话列表里那些从未打开过的会话不在其中,
    /// 需要完整集合请走 ChatService.LoadAllSessions / SessionMetadata。</para>
    /// </summary>
    public IReadOnlyList<SessionRuntime> AllSessions
    {
        get { lock (_lock) return _sessions.Values.ToList(); }
    }

    /// <summary>全部会话运行时快照。<see cref="AllSessions"/> 的旧别名(CommanderRuntime 等既有调用点),
    /// 语义完全一致, 仅为不破坏现有调用而保留。</summary>
    public IReadOnlyList<SessionRuntime> All => AllSessions;

    /// <summary>活动会话变化通知。</summary>
    public event Action<SessionRuntime?>? ActiveSessionChanged;

    public SessionRuntime? TryGet(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var rt) ? rt : null;
        }
    }

    /// <summary>获取或创建会话运行时(标题优先取参, 否则从会话文件读取)。</summary>
    public SessionRuntime GetOrCreate(string sessionId, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Fallback;
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var existing)) return existing;
            return CreateRuntimeLocked(sessionId, title ?? ReadTitle(sessionId));
        }
    }

    /// <summary>设置活动会话(GUI 切换会话时调用); 未注册的会话会按需创建。</summary>
    public bool SetActiveSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (_activeSessionId is null) return false;
            _activeSessionId = null;
            ActiveSessionChanged?.Invoke(null);
            return true;
        }

        var rt = GetOrCreate(sessionId);
        if (string.Equals(_activeSessionId, rt.SessionId, StringComparison.OrdinalIgnoreCase)) return false;
        _activeSessionId = rt.SessionId;
        ActiveSessionChanged?.Invoke(rt);
        return true;
    }

    /// <summary>更新会话标题(事件 SessionTitle 携带)。</summary>
    public void SetSessionTitle(string sessionId, string title)
        => TryGet(sessionId)?.SetTitle(title);

    /// <summary>移除会话运行时(会话删除时调用): 取消其进行中回合并释放资源。</summary>
    public bool RemoveSession(string sessionId)
    {
        SessionRuntime? rt;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out rt)) return false;
            _sessions.Remove(sessionId);
        }

        rt!.RawEvent -= OnSessionRawEvent;
        rt.Dispose();

        if (string.Equals(_activeSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            _activeSessionId = null;
            ActiveSessionChanged?.Invoke(null);
        }

        return true;
    }

    private SessionRuntime CreateRuntimeLocked(string sessionId, string title)
    {
        var rt = new SessionRuntime(sessionId, title, _factory, _coordinator);
        rt.RawEvent += OnSessionRawEvent;
        _sessions[sessionId] = rt;
        return rt;
    }

    /// <summary>
    /// 后台会话用量落盘: 活动会话的用量交给 GUI 记录, 其余会话在此直接记录(每会话独立用量)。
    ///
    /// <para><b>不变式(全项目最易被破坏的一条, 改动前务必读完)</b>: 用量落盘由两处互斥负责 ——
    /// ① 事件属于<b>活动会话</b>时, EngineEventHub 判定投递, 由 GUI(ChatPageViewModel)记录;
    /// ② 事件属于<b>非活动会话</b>时, Hub 过滤掉不投 GUI, 由本方法落盘。
    /// 于是任何一条 EngineUsageRecorded 只会有一条落盘路径, 不会重复计费。</para>
    ///
    /// <para>该不变式成立的前提是 <see cref="EngineEventHub.ShouldDeliver"/> 里
    /// "非活动会话事件一律不投递"这条规则。<b>ActiveSessionId 为 null 时 Hub 一律不投</b>,
    /// 此刻本方法也不会落盘(等值比较不成立) —— 这是刻意的: 没有任何界面在展示用量,
    /// 不该写入用户看不到的统计。代价是这段窗口内的用量确实会丢, 属于已接受的取舍。
    /// 若将来要修, 应改成"无活动会话时全部走本方法", 而不是改 Hub 的投递规则。</para>
    /// </summary>
    private void OnSessionRawEvent(SessionRuntime rt, AgentEngineEvent e)
    {
        if (e is not EngineUsageRecorded usage) return;

        // 与 Hub 的过滤规则严格互补: 这里只处理"Hub 不会投"的那部分(见上方不变式)
        var active = _activeSessionId;
        if (string.Equals(e.SessionId, active, StringComparison.OrdinalIgnoreCase)) return;

        var title = string.IsNullOrWhiteSpace(rt.SessionTitle) ? e.SessionId : rt.SessionTitle;
        UsageStatsService.RecordLlmUsage(e.SessionId, title, usage.Provider, usage.Model,
            usage.Usage.InputTokens, usage.Usage.OutputTokens, usage.Usage.CachedInputTokens);
    }

    /// <summary>
    /// 读取会话标题(源生成序列化, Native AOT 安全)。
    /// 走 AtomicFile.TryReadText: 主文件损坏时自动回退 .bak(技术债 #17)。
    /// 裸读一旦遇到半截 JSON 就只会退化出空标题, 而空标题又会被下一次写回覆盖 —— 损坏被放大。
    /// </summary>
    private static string ReadTitle(string sessionId)
    {
        try
        {
            var path = Path.Combine(AppPaths.SessionsDir, $"{sessionId}.json");
            if (!File.Exists(path)) return string.Empty;

            // validate 用"能反序列化出 ChatSession"作可用性判据, 失败即触发 .bak 回退。
            // 这里只求标题, 不需要 messages, 因此不额外做 IsLoaded 之类的完整性判断。
            var ok = AtomicFile.TryReadText(path, out var json, content =>
            {
                try
                {
                    return JsonSerializer.Deserialize(
                        content, AppJsonContext.Default.ChatSession) is not null;
                }
                catch
                {
                    return false;
                }
            });

            if (!ok)
            {
                Log.Warn("Session", $"会话文件不可读(主文件与备份均失败): {sessionId}");
                return string.Empty;
            }

            var session = JsonSerializer.Deserialize(json, AppJsonContext.Default.ChatSession);
            return session?.Title ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn("Session", ex, $"会话标题读取失败: {sessionId}");
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<SessionRuntime> all;
        lock (_lock)
        {
            all = [.. _sessions.Values];
            _sessions.Clear();
            _fallback = null;
        }

        foreach (var rt in all)
        {
            rt.RawEvent -= OnSessionRawEvent;
            rt.Dispose();
        }
    }
}
