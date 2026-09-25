using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Engine;
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
    private WorkspaceActivity? _activeActivity;
    private int _disposed;

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

    /// <summary>本会话持有的工作区执行权(无则 null)。</summary>
    public WorkspaceActivity? ActiveActivity
    {
        get { lock (_lock) return _activeActivity; }
    }

    /// <summary>会话运行状态变化(回合开始/结束), 供 UI/协调器订阅。</summary>
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

        StateChanged?.Invoke();
        return true;
    }

    void ISessionEngineHost.EndTurn(AgentEngine engine, WorkspaceActivity? activity)
    {
        _coordinator.EndTurn(activity);
        lock (_lock)
        {
            _activeActivity = null;
            IsRunning = false;
            ActiveTurnId = null;
        }

        StateChanged?.Invoke();
    }

    /// <summary>停止该会话进行中/后续回合(每会话独立取消)。</summary>
    public void Stop() => Engine.CancelSession();

    private void OnEngineRawEvent(AgentEngineEvent e) => RawEvent?.Invoke(this, e);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Engine.RawEvent -= OnEngineRawEvent;
        Engine.CancelSession(); // 进行中的回合随会话释放而终止
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
