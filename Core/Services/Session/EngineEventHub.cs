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

    /// <summary>
    /// 投递判定(静态纯函数, 便于自检): true 表示应送达界面订阅者。
    /// 四条规则的顺序有意义, <b>改规则前先确认下面每一条的理由都还成立</b>:
    /// <list type="number">
    /// <item>Scope 为 null → 全投。历史兼容口径: 早期事件不带归属, 无法判断该给谁, 只能全给。</item>
    /// <item>审批 / 反问 / 分派状态 → 强制全投。这些是<b>引擎在同步等待</b>的事件,
    /// 过滤掉会让引擎挂到 5 分钟超时; 分派状态则是全局面板数据, 与当前会话无关。</item>
    /// <item>活动会话为空 → 一律不投。没有界面在展示任何东西, 投过去只会被 GUI 队列丢弃。</item>
    /// <item>其余按 SessionId 与活动会话<b>忽略大小写</b>比对(会话 ID 的来源不止一处)。</item>
    /// </list>
    /// <para>规则 2 与 SessionRuntimeRegistry.OnSessionRawEvent 的用量落盘构成互斥,
    /// 那条不变式的说明见该方法注释。</para>
    /// </summary>
    public static bool ShouldDeliver(AgentEngineEvent e, string? activeSessionId)
    {
        if (e.Scope is null) return true; // 规则 1: 未标注归属(兼容/全局事件)

        // 规则 2: 审批与 AI 反问必须送达(否则工具永久等待); 分派状态是全局面板数据
        if (e is EngineApprovalRequested or EngineQuestionRequested or EngineAssignmentChanged)
        {
            return true;
        }

        if (string.IsNullOrEmpty(activeSessionId)) return false; // 规则 3: 无活动会话
        return string.Equals(e.SessionId, activeSessionId, StringComparison.OrdinalIgnoreCase); // 规则 4
    }
}
