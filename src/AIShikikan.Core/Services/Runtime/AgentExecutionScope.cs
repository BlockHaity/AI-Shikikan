using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;

namespace AIShikikan.Core.Services.Runtime;

/// <summary>
/// 工具与子代理执行期对「进程全局状态」的显式依赖容器。
/// 引入它的唯一原因: 这些工具原先反向读静态单例 CommanderRuntime.Instance,
/// 而 ToolContext 是每次调用传入的、ToolExecutor 是 static —— 换到独立 Worker 进程后
/// 单例里没有 GUI 侧的状态, 反向读会静默退化成 ?? false / 空表(不报错, 只是行为错)。
///
/// <para><b>降级约定(全部委托为 null 时必须与今天的行为逐字一致)</b>:
/// <list type="bullet">
/// <item><see cref="CommanderPersonaText"/> 为 null ⇒ 人格解析不落到指挥官人格(等价于
/// 今天 <c>CommanderRuntime.Instance?.CurrentPersonaText</c> 读不到)。</item>
/// <item><see cref="RosterResolver"/> 为 null ⇒ 会话级 Roster 取空表(等价于今天
/// <c>CommanderRuntime.Instance is null</c> 时 <c>RosterOf</c> 返空表), 压缩开关恒 false。</item>
/// <item><see cref="PlanAuthorizer"/> 为 null ⇒ Plan 模式判定为 false(等价于今天
/// <c>CommanderRuntime.Instance?.IsAgentRegisteredInPlanMode(agent) ?? false</c>);
/// 执行层兜底则**不拒绝**(等价于今天 <c>CommanderRuntime.Instance is { } runtime &amp;&amp; …</c>
/// 的短路)。</item>
/// <item><see cref="WorkspaceRoot"/> 为 null ⇒ 不做任何工作区兜底, 一律用
/// <c>ToolContext.WorkspaceRoot</c>(与今天的实际行为一致)。</item>
/// <item><see cref="ActiveSessionIdResolver"/> 为 null ⇒ 无会话兜底, 归属 id 退化为空串
/// (等价于今天 <c>CommanderRuntime.Instance?.Sessions.ActiveSessionId ?? ""</c>)。</item>
/// </list>
/// 全部为 null 时自检/独立进程内跑这些工具不会抛异常, 只是退回"无全局状态"的最小行为。</para>
///
/// <para><b>字段只增不改</b>: 另一进程(Worker)与 GUI 侧按这份契约装配, 增删字段会同时打断两边。</para>
/// </summary>
public sealed class AgentExecutionScope
{
    /// <summary>指挥官人格全文; 三个子代理工具的 persona 兜底来源。</summary>
    public string? CommanderPersonaText { get; init; }

    /// <summary>按 sessionId 查会话级 roster; 返回 null 表示「该会话无限制」。
    ///
    /// <para>实现方需自行处理 sessionId 为 null/空的回退(今天的写法是回退到「活动会话」的
    /// Roster 快照), 工具侧不做第二次回退。</para>
    ///
    /// <para>返回 null 时工具侧按**空表**处理(压缩开关恒 false)而非「无限制」:
    /// RosterBuilder 的 null=无限制语义只属于提示词注入, 泄漏到工具侧会让压缩开关退化成「总是压缩」。</para>
    /// </summary>
    public Func<string?, IReadOnlyList<AgentRosterEntry>?>? RosterResolver { get; init; }

    /// <summary>Plan 模式授权判定: (sessionId, agent) => 是否授权。null 表示恒 false。
    /// 判据应与工具注册过滤共用同一份规则(见 <c>CommanderRuntime.IsAgentPlanModeAuthorized</c>),
    /// 否则会出现"注册了但执行被拒"或"未注册却放行"的漂移。</summary>
    public Func<string?, CliAgentDefinition, bool>? PlanAuthorizer { get; init; }

    /// <summary>工作区根的末级兜底来源 (原 CommanderRuntime.Instance.WorkspaceRoot)。
    /// 当前工具执行路径不使用它(<c>ToolContext.WorkspaceRoot</c> 已经是权威值),
    /// 保留是为了让 Worker 进程能显式声明自己的工作区而不必再反向读单例。</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>诊断用: 当前会话 id 的兜底来源 (原 CommanderRuntime.Instance.Sessions.ActiveSessionId)。
    /// 仅在 <c>ToolContext.SessionId</c> 为空时使用(旧引擎路径 / 自检)。</summary>
    public Func<string?>? ActiveSessionIdResolver { get; init; }
}