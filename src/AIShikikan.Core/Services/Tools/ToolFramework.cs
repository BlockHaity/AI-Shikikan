using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;

namespace AIShikikan.Core.Services.Tools;

public class ToolResult
{
    public bool IsError { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>关联的 git 检查点步骤 ID, UI 据此提供回滚按钮。
    /// 目前只有 git_create_checkpoint 会赋值; 子代理工具恒为 null —— 子代理在当前分支就地工作,
    /// 回滚入口是"每条用户消息"的检查点, 与单个子代理无关(旧 ac/&lt;stepId&gt; 分支机制已废弃)。</summary>
    public string? StepId { get; set; }

    /// <summary>结构化卡片展示数据(按工具类型渲染专属卡体); 为空时卡片回退到通用文本展示。</summary>
    public ToolCardDetail? Detail { get; set; }

    public static ToolResult Ok(string content) => new() { Content = content };

    public static ToolResult Error(string message) => new() { IsError = true, Content = message };
}

public class ToolContext
{
    public required string WorkspaceRoot { get; init; }

    /// <summary>主对话是否处于 Plan 模式(决定子代理是否以 plan_args 启动)。</summary>
    public bool IsPlanMode { get; init; }

    /// <summary>
    /// 当前回合使用的 Provider Id(引擎注入; 由 <c>CommanderRuntime.ApplyLlmRouting</c> 在回合开始前写入)。
    /// 供需要"与本回合同 Provider"的子流程使用(如子代理输出压缩)。
    /// </summary>
    /// <remarks>
    /// <para>⚠️ <b>为 null 的语义是「跟随全局活动 Provider」，不是「未知」</b>。
    /// <c>LlmService.GetProvider(null)</c> 与 <c>GetClient(null)</c> 解析到同一处
    /// (<c>LlmSettings.ActiveProviderId</c>), 所以压缩与主回路<b>不会因此跑偏 Provider</b>。
    /// 接线本字段的价值在于: 将来某条流程显式指定了非活动 Provider 时, 能把它带下去。</para>
    /// <para>接线位置在<b>每回合开始前</b>(与 GUI 就地写 <c>Options.Thinking/WorkDir/IsPlanMode</c> 同一处)。
    /// 刻意<b>不</b>在会话引擎创建时固化: 会话引擎是长生命周期缓存对象, 不重建;
    /// 一旦创建时写死, 用户在聊天页切 Provider 后「下一条消息生效」这条既有语义就失效。</para>
    /// </remarks>
    public string? ProviderId { get; init; }

    /// <summary>
    /// 当前回合实际生效的模型名(引擎注入, 已解析)。
    /// 供子代理输出压缩等场景复用, 避免各处重复推导导致与主对话模型不一致。
    /// </summary>
    /// <remarks>
    /// 本字段<b>当前是有效的</b>: 引擎在 <c>ResolveModel</c> 之后把结果缓存进 <c>_currentModel</c> 再注入,
    /// 因此它就是本回合真正用的模型(与 <see cref="ProviderId"/> 不同, 这里不是死管线)。
    /// </remarks>
    public string? Model { get; init; }

    /// <summary>执行本工具的会话 Id(引擎注入)。
    /// 会话级子代理配置(Roster: Plan 授权 / 输出压缩开关)必须按发起本回合的会话读取,
    /// 不能读"当前活动会话" —— 后台会话同时跑时两者不是同一个。
    /// 为 null/空时调用方需自行回退(见 AgentExecutor.RosterOf)。</summary>
    public string? SessionId { get; init; }

    /// <summary>本回合所属会话的子代理 roster 快照 (会话级配置: Enabled/CompactEnabled/UseInPlanMode)。
    /// 工具用它判断「输出压缩是否开启」与「Plan 模式是否授权」, 不再读全局活动会话 ——
    /// 后者会让后台会话的判定串味 (已记录的语义缺陷)。</summary>
    /// <remarks>
    /// <para>⚠️ <b>null 与空表的语义截然不同, 判据只能看 null 与否, 绝不能用 <c>Count &gt; 0</c></b>:</para>
    /// <list type="bullet">
    /// <item><c>null</c> = 无会话 roster 下发(会话从未被 GUI 推送过)→ 无限制, 回退全局配置;</item>
    /// <item>非 null(含<em>空表</em>) = 以该表为准, 空表即「用户已清空全部子代理」。</item>
    /// </list>
    /// 与 <c>RosterBuilder.Build</c> 的 <c>rosterEntries</c> 完全同构。历史上用 <c>Count &gt; 0</c>
    /// 一并表达两种含义, 导致 <c>SetSubagentToolsVisible(false)</c> 传入的空表反落到「列出全部 Agent」
    /// 分支: 工具已注销而提示词仍在广告 <c>run_&lt;id&gt;</c>, AI 持续调用不存在的工具。</item>
    /// <para>另两点落地提醒:</para>
    /// <list type="bullet">
    /// <item><b>两条取值路径并存(刻意)</b>: 引擎已注入本字段, 但子代理工具当前仍走
    /// <c>AgentExecutionScope.RosterResolver</c>(进程内委托)。保留两条是因为本字段会随 Worker 协议
    /// 序列化到子进程, 而 scope 是委托、跨不了进程。切换消费方时判据只能是
    /// <c>is not null</c>, 且必须把 resolver 返回的 null <b>先收成空表</b>(scope 那条路径的既有语义),
    /// 否则「未启用」会被误判成「无限制」, 反向则把「无限制」误判成「用户清空」。</item>
    /// <item><b>是引用快照而非深拷贝</b>: 元素为可变 <c>AgentRosterEntry</c>(INotifyPropertyChanged,
    /// 直接绑在右侧栏 UI 上), 工具执行途中用户改开关, 同一对象上看到的值会跟着变。
    /// <c>IReadOnlyList</c> 只挡"替换元素", 挡不住"改元素字段"; 需要严格时点一致请自行复制。</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<AgentRosterEntry>? RosterEntries { get; init; }

    /// <summary>指挥官人格全文快照。子代理工具解析人格文本时用它作为兜底,
    /// 不再读 CommanderRuntime.Instance.CurrentPersonaText。</summary>
    /// <remarks>
    /// <para><b>为什么必须是快照字段而不是继续读全局单例</b>: <c>CurrentPersonaText</c> 是
    /// "用户当前在设置页选中的那一篇人格", 与「谁在本回合当指挥官」无关 —— 两个会话可以各带各的人格,
    /// 而切人格会就地改写这个全局值。子代理 prompt 由它拼装, 读全局值意味着 A 会话派出的子代理
    /// 可能在用户切人格后带着 B 人格跑。</para>
    /// <para>⚠️ 语义约定: null / 空串 = 本回合无指挥官人格(此时子代理应当<em>不</em>附加人格前缀,
    /// 而非回退去取全局默认 —— 回退会把上面那个串味缺陷原地保留)。判定请用
    /// <c>string.IsNullOrWhiteSpace</c>, 勿只判 null(引擎侧赋值可能给到空串)。</para>
    /// <para>⚠️ 同 <see cref="RosterEntries"/>: 引擎已注入本字段, 但子代理工具当前仍走
    /// <c>AgentExecutionScope.CommanderPersonaText</c>(进程内快照)。两条并存的原因同上。</para>
    /// </remarks>
    public string? CommanderPersonaText { get; init; }

    /// <summary>子代理实时输出回调(UI 订阅)。</summary>
    public Action<string>? OnToolOutput { get; init; }

    /// <summary>向用户反问并等待回答的回调(由引擎注入, 返回 null 表示用户未作答/取消)。</summary>
    public Func<string, CancellationToken, Task<string?>>? AskUser { get; init; }
}

public interface ITool
{
    string Name { get; }

    string Description { get; }

    JsonElement Parameters { get; }

    bool RequiresApproval { get; }

    Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default);
}

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(ITool tool)
    {
        lock (_gate)
        {
            // 字典是 OrdinalIgnoreCase: 两个仅大小写不同的名字(如 run_Foo 与 run_foo、
            // MCP 桥接名 mcp_<server>_<tool> 与同名 run_<agent>)会被静默覆盖,
            // 表现为"某个工具莫名消失/被顶替", 且没有任何线索。至少要留一条告警。
            // 仍然覆盖而非抛异常: 覆盖是既有语义(右侧栏/Plan 切换靠它重建工具集),
            // 抛异常会让重建流程整体失败。
            if (_tools.TryGetValue(tool.Name, out var existing) && !ReferenceEquals(existing, tool))
            {
                Log.Warn("Tools",
                    $"工具名冲突: \"{tool.Name}\" 已注册({existing.GetType().Name}), " +
                    $"被本次注册({tool.GetType().Name})覆盖; 工具总数 {_tools.Count}");
            }

            _tools[tool.Name] = tool;
        }
    }

    /// <summary>按条件注销一组工具(MCP 刷新/右侧栏关闭时重建)。</summary>
    public int UnregisterWhere(Func<ITool, bool> predicate)
    {
        lock (_gate)
        {
            var names = _tools.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var n in names)
            {
                _tools.Remove(n);
            }

            return names.Count;
        }
    }

    public bool TryGet(string name, out ITool tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out tool!);
        }
    }

    public ITool Get(string name)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(name, out var tool)
                ? tool
                : throw new KeyNotFoundException($"未知工具: {name}");
        }
    }

    public IReadOnlyList<ITool> All
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values.ToList();
            }
        }
    }

    /// <summary>导出全部工具的 LLM 声明(每回合随请求全量重发)。
    /// ⚠️ 已知问题(技术债 #10): 没有任何上限或裁剪 —— Agent 数量增长时 run_&lt;id&gt; 工具会
    /// 挤占上下文并降低工具选择准确率, 请求体也会按工具数线性膨胀。
    /// 刻意不在此处加限制: 裁剪工具集会直接改变 LLM 可用能力, 属于产品决策
    /// (可选方向: 按任务类型/会话开关裁剪工具集, 或把多个子代理合并成"按描述自动路由"的单入口)。</summary>
    public List<Llm.ToolSpec> ToSpecs()
    {
        lock (_gate)
        {
            return _tools.Values.Select(t => new Llm.ToolSpec
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.Parameters
            }).ToList();
        }
    }
}

public static class ToolSchema
{
    public static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}