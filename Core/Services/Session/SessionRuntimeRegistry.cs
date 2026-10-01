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
            // 身份校验: 若活动条目已被后续回合接管(旧回合的 finally 晚于新回合的 finally 执行),
            // 不能再把 _activeActivity / IsRunning / ActiveTurnId 清空, 否则会抹掉新回合的运行状态
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

    /// <summary>停止该会话进行中/后续回合(每会话独立取消)。</summary>
    public void Stop() => Engine.CancelSession();

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

    /// <summary>全部会话运行时快照。</summary>
    public IReadOnlyList<SessionRuntime> All
    {
        get { lock (_lock) return _sessions.Values.ToList(); }
    }

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

    /// <summary>后台会话用量落盘: 活动会话的用量交给 GUI 记录, 其余会话在此直接记录(每会话独立用量)。</summary>
    private void OnSessionRawEvent(SessionRuntime rt, AgentEngineEvent e)
    {
        if (e is not EngineUsageRecorded usage) return;

        var active = _activeSessionId;
        if (string.Equals(e.SessionId, active, StringComparison.OrdinalIgnoreCase)) return;

        var title = string.IsNullOrWhiteSpace(rt.SessionTitle) ? e.SessionId : rt.SessionTitle;
        UsageStatsService.RecordLlmUsage(e.SessionId, title, usage.Provider, usage.Model,
            usage.Usage.InputTokens, usage.Usage.OutputTokens, usage.Usage.CachedInputTokens);
    }

    /// <summary>读取会话标题(源生成序列化, Native AOT 安全)。</summary>
    private static string ReadTitle(string sessionId)
    {
        try
        {
            var path = Path.Combine(AppPaths.SessionsDir, $"{sessionId}.json");
            if (!File.Exists(path)) return string.Empty;
            var session = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.ChatSession);
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
