using AIShikikan.Core.Logging;
using AIShikikan.Core.Services.Engine;

namespace AIShikikan.Core.Services.Session;

/// <summary>引擎事件 Hub: 所有会话引擎共享的订阅入口, 按"当前活动会话"过滤投递。
/// 规则: 未标注归属的事件全量投递(兼容); 审批/ask_user/分派事件始终投递(否则引擎会永久等待);
/// 其余事件(流式增量/工具/用量/完成)仅投递给当前活动会话, 保证界面流不串会话、
/// 用量归属正确(非活动会话的用量由 SessionRuntimeRegistry 直接落盘)。</summary>
public sealed class EngineEventHub
{
    private readonly object _lock = new();
    private Action<AgentEngineEvent>? _subscribers;

    /// <summary>活动会话解析器(由 SessionRuntimeRegistry 注入)。</summary>
    public Func<string?>? ActiveSessionId { get; set; }

    public void Subscribe(Action<AgentEngineEvent>? handler)
    {
        if (handler is null) return;
        lock (_lock)
        {
            _subscribers += handler;
        }
    }

    public void Unsubscribe(Action<AgentEngineEvent>? handler)
    {
        if (handler is null) return;
        lock (_lock)
        {
            _subscribers -= handler;
        }
    }

    /// <summary>发布事件: 过滤后逐个投递(单个订阅者异常不影响其余订阅者与发布方)。</summary>
    public void Publish(AgentEngineEvent e)
    {
        Action<AgentEngineEvent>? subs;
        string? active;
        lock (_lock)
        {
            subs = _subscribers;
            active = ActiveSessionId?.Invoke();
        }

        if (subs is null || !ShouldDeliver(e, active)) return;

        foreach (var d in subs.GetInvocationList())
        {
            try
            {
                ((Action<AgentEngineEvent>)d)(e);
            }
            catch (Exception ex)
            {
                Log.Warn("Session", ex, "引擎事件订阅者处理异常");
            }
        }
    }

    /// <summary>投递判定(静态纯函数, 便于自检): true 表示应送达界面订阅者。</summary>
    public static bool ShouldDeliver(AgentEngineEvent e, string? activeSessionId)
    {
        if (e.Scope is null) return true; // 未标注归属(兼容/全局事件)

        // 审批与 AI 反问必须送达(否则工具永久等待); 分派状态是全局面板数据
        if (e is EngineApprovalRequested or EngineQuestionRequested or EngineAssignmentChanged)
        {
            return true;
        }

        if (string.IsNullOrEmpty(activeSessionId)) return false;
        return string.Equals(e.SessionId, activeSessionId, StringComparison.OrdinalIgnoreCase);
    }
}
