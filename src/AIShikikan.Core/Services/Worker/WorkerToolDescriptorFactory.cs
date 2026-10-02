using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Services.Tools;

namespace AIShikikan.Core.Services.Worker;

/*
 * <see cref="WorkerToolDescriptor"/> 的**唯一生成落点**: ITool 列表 → 协议声明。
 *
 * ── 为什么必须是单独一个文件, 而不是"谁要用谁写一份" ──
 * 两侧都要它, 且两侧都不该有自己的那份:
 *   ① Worker 进程: <c>WorkerServer.HandleToolsListAsync</c> 回 <c>worker/tools/list</c>;
 *   ② 主进程内联降级: <c>CommanderRuntime.ListWorkerToolsInlineAsync</c>(Worker 起不来时,
 *      同一进程自己扮演 Worker, 列举的仍然是主进程 <c>ToolRegistry</c>)。
 * 两份实现必然漂移, 而漂移的症状只是「工具分类显示错误」「git 写工具漏标」这类
 * 极难归因的小事 —— 没有任何异常, 也不会有人发现, 直到某天真的串行化 git 写时才会爆。
 * 所以抽到这里, 由两侧共同调用(与 ToolCardDetailCodec 同一纪律: 真源 + 自证, 不复制)。
 *
 * ── 派生规则的性质 ──
 * <see cref="WorkerToolDescriptor.Kind"/> / <see cref="WorkerToolDescriptor.RequiresGitWrite"/>
 * 不在 <see cref="ITool"/> 上: 加这两个成员会迫使主进程侧三十来个工具实现全部改一遍,
 * 而它们只在 Worker 协议里用得上。因此只能**按工具名**派生, 工具名就是协议契约 ——
 * 新增工具时顺手在下面两张表加一行即可(漏标的代价见各表的 remarks)。
 *
 * ⚠ 本文件刻意**不做任何反序列化**(不碰 AppJsonContext): 它只产出 DTO。
 *   跨进程结构化数据的编解码一律走 WorkerMessages.cs + AppJsonContext。
 */

/// <summary>
/// <see cref="ITool"/> 列表 → <see cref="WorkerToolDescriptor"/> 列表。
/// </summary>
/// <remarks>
/// <b>⚠ 分类与白名单表在这里是唯一真源</b>: 别在任何调用方再写一份 <c>DeriveKind</c> /
/// <c>IsGitWrite</c> / <c>EmptySchema</c>。历史上 <c>WorkerSlimHost</c> 与
/// <c>WorkerServer</c> 各留过一份同款实现, 两份都是"看起来对"的死代码,
/// 而漂移的方向永远是「分类显示不出来」这种没有报错的地方。
/// </remarks>
internal static class WorkerToolDescriptorFactory
{
    private const string LogCategory = "Worker";

    /// <summary>空对象 schema(参数 schema 取不到时的降级文本)。</summary>
    /// <remarks>与 <c>WorkerProxyTool</c> 解析失败时的降级 schema 同一份文本 —— 同一份兜底不许有第二种写法。</remarks>
    internal const string EmptySchema = """{ "type": "object", "properties": {} }""";

    /// <summary>子代理工具类别。</summary>
    internal const string KindSubagent = "subagent";

    /// <summary>git 工具类别。</summary>
    internal const string KindGit = "git";

    /// <summary>反问类工具类别(<c>ask_user</c>)。</summary>
    internal const string KindAsk = "ask";

    /// <summary>只读文件类工具类别。</summary>
    internal const string KindFile = "file";

    /// <summary>其余(未来可能出现的 <c>mcp_*</c> 桥接名等)。</summary>
    internal const string KindOther = "other";

    /// <summary>子代理工具名的前缀(后面跟的是 Agent Id)。</summary>
    private const string RunPrefix = "run_";

    /// <summary>
    /// 会写 git 的工具名白名单。<b>刻意用精确名单而不是 <c>git_</c> 前缀</b>:
    /// <c>git_status</c>/<c>git_diff</c> 是只读的, 把它们算成写会让父进程侧的 git 写门
    /// 把只读操作也串行化, 白白损失并发。
    /// <para>⚠ 新增会写 git 的工具时必须同步维护这张表(它<b>目前只是提示</b>:
    /// <see cref="WorkerToolDescriptor.RequiresGitWrite"/> 的说明指出
    /// <c>WorkspaceExecutionCoordinator</c> 的写门还没有生产调用方, 所以漏标暂时不会
    /// 造成比现状更差的结果)。</para>
    /// </summary>
    private static readonly HashSet<string> GitWritingTools = new(StringComparer.Ordinal)
    {
        "git_add",
        "git_commit",
        "git_create_checkpoint",
    };

    /// <summary>只读文件类工具(分类用)。刻意列名单而不是前缀: 工具名是协议契约, 加新工具时顺手加一行即可。</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "read_file",
        "list_directory",
        "grep",
        "glob",
    };

    /// <summary>
    /// 生成<b>不过滤</b>的声明列表(<c>worker/tools/list</c> 的载荷)。
    /// </summary>
    /// <remarks>
    /// <b>为什么 tools/list 不在这里过滤</b>: Worker 侧的注册表<b>已经</b>是
    /// <c>tools/sync</c> 与握手之后的过滤结果, 再按请求载荷过滤一遍等于把策略应用两遍。
    /// 过滤是父进程的职责, 它通过 <c>tools/sync</c> 表达, 再用一次 <c>tools/list</c> 取回权威清单。
    /// </remarks>
    /// <param name="tools">宿主当前注册的工具; 为 null 当空表处理。</param>
    public static IReadOnlyList<WorkerToolDescriptor> Build(IReadOnlyList<ITool>? tools)
        => BuildCore(tools, policy: null);

    /// <summary>
    /// 按一份 <b>sync 载荷</b>过滤后再生成声明列表。
    /// </summary>
    /// <remarks>
    /// <b>谁在用</b>: 主进程的内联降级列举(<c>CommanderRuntime.ListWorkerToolsInlineAsync</c>)。
    /// 降级时"扮演 Worker"的是主进程自己的 <c>ToolRegistry</c>, 而它里面还留着
    /// MCP 桥接工具等不属于本协议的东西, 于是这里必须按请求载荷把工具表裁成
    /// "如果这份载荷发给真 Worker, 它会回报什么" —— 否则主进程与 Worker 的工具表会分叉。
    /// <para>⚠ <b>过滤只认请求载荷, 不认主进程的私有开关</b>: 这样"同一份载荷在两侧得到同一个答案",
    /// 而答案本身就是协议契约。</para>
    /// </remarks>
    /// <param name="tools">工具清单; 为 null 当空表处理。</param>
    /// <param name="policy">过滤策略; 为 null 等同 <see cref="Build"/>(不过滤)。</param>
    public static IReadOnlyList<WorkerToolDescriptor> BuildFiltered(
        IReadOnlyList<ITool>? tools,
        WorkerToolsSyncRequest? policy)
        => BuildCore(tools, policy);

    private static IReadOnlyList<WorkerToolDescriptor> BuildCore(
        IReadOnlyList<ITool>? tools,
        WorkerToolsSyncRequest? policy)
    {
        if (tools is null || tools.Count == 0) return [];

        var list = new List<WorkerToolDescriptor>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is null) continue;

            var name = tool.Name ?? string.Empty;
            if (name.Length == 0)
            {
                // 空名工具既不可选也不可调, 上报它只会让父进程注册一个永远调不通的条目。
                Log.Warn(LogCategory, $"工具 {tool.GetType().Name} 没有名字, 已从工具清单中省略");
                continue;
            }

            var kind = DeriveKind(name);
            if (policy is not null && IsFilteredOut(name, kind, policy)) continue;

            list.Add(new WorkerToolDescriptor
            {
                Name = name,
                Description = tool.Description ?? string.Empty,
                ParametersJson = ParametersText(tool.Parameters, name),
                RequiresApproval = tool.RequiresApproval,
                Kind = kind,
                RequiresGitWrite = GitWritingTools.Contains(name),
            });
        }

        return list;
    }

    /// <summary>
    /// 按 sync 载荷判定某个工具是否应当从清单里去掉。
    /// </summary>
    /// <remarks>
    /// <para>三条规则(逐条对应 <see cref="WorkerToolsSyncRequest"/> 的三个开关):</para>
    /// <list type="number">
    /// <item><b>CoreTools=false</b> → 去掉固定工具(文件 / git / <c>ask_user</c>)。
    /// 这里按 <b>类别</b>判定而不是按类型: 本工厂刻意不认识具体工具类型
    /// (那属于工具层的知识, 而 Worker 协议只认名字与类别)。</item>
    /// <item><b>SubagentVisible=false</b> → 去掉全部子代理工具, 含 <c>assign_task</c> /
    /// <c>run_subagents</c>(与 <c>CommanderRuntime.RebuildSubagentTools</c> 的注销口径一致)。</item>
    /// <item><b>PlanMode=true 且白名单非空</b> → 去掉未授权的 <c>run_&lt;agentId&gt;</c>。
    /// ⚠ <b>只过滤 <c>run_*</c>, 不动 <c>assign_task</c>/<c>run_subagents</c></b>:
    /// Worker 侧的口径是"按授权过滤 Agent 列表, 再据此注册编排工具", 而白名单非空本就意味着
    /// 至少有一个授权 Agent, 编排工具理应保留。
    /// <b>白名单为空一律不过滤</b>: 空表 = 不限制(见 <see cref="WorkerToolsSyncRequest.AllowedAgentIds"/>),
    /// Plan 模式的"零授权"另有表达(子代理工具一个都不注册, 由 SubagentVisible / 注册状态承载)。</item>
    /// </list>
    /// </remarks>
    private static bool IsFilteredOut(string name, string kind, WorkerToolsSyncRequest policy)
    {
        if (!policy.CoreTools && IsCoreToolKind(kind)) return true;

        if (!policy.SubagentVisible && kind == KindSubagent) return true;

        if (policy.PlanMode && kind == KindSubagent && policy.AllowedAgentIds is { Count: > 0 }
            && name.StartsWith(RunPrefix, StringComparison.Ordinal))
        {
            var agentId = name[RunPrefix.Length..];
            var allowed = false;
            foreach (var id in policy.AllowedAgentIds)
            {
                if (string.Equals(id, agentId, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed) return true;
        }

        return false;
    }

    /// <summary>该类别是否属于"固定工具"(对应 <c>AgentToolFactory.CreateCoreTools</c> 的覆盖面)。</summary>
    private static bool IsCoreToolKind(string kind) =>
        kind is KindFile or KindGit or KindAsk;

    /// <summary>工具类别派生(纯展示/路由用)。</summary>
    /// <remarks>
    /// ⚠️ <b>ask_user 归 "ask" 而不是 "file"</b>: 它是 UI 交互工具, 与文件读取毫无关系;
    /// 归错类会让父进程按"文件类工具"做展示分组。取值不在 file/git/subagent 之内是允许的 ——
    /// <see cref="WorkerToolDescriptor.Kind"/> 刻意不是枚举, 父进程必须容忍未知取值。
    /// </remarks>
    internal static string DeriveKind(string toolName)
    {
        // ⚠️ 顺序有意义: run_<agentId> 里 agentId 可以是任意字符串, 所以先判子代理工具,
        // 否则一个名叫 git_xxx 的 Agent 会被误判成 git 工具。
        if (toolName.StartsWith(RunPrefix, StringComparison.Ordinal)
            || toolName is "assign_task" or "run_subagents")
        {
            return KindSubagent;
        }

        if (toolName.StartsWith("git_", StringComparison.Ordinal))
        {
            return KindGit;
        }

        if (toolName == "ask_user")
        {
            return KindAsk;
        }

        // 未来的 mcp_* 桥接名等归这里: 父进程只按"分类"展示, 不按它做任何决策
        return FileTools.Contains(toolName) ? KindFile : KindOther;
    }

    /// <summary>schema 取原始文本; 取不到就降级成空对象 schema。</summary>
    /// <remarks>
    /// ⚠️ 该文本<b>必然含裸换行</b>(schema 在源码里是多行原始字符串), 所以它只能作为
    /// <b>JSON 字符串字段</b>嵌套 —— 由 STJ 转义成 <c>\n</c>, 外层帧因此仍是一行。
    /// 直接把多行 JSON 拼进帧里会被读行端切成两半, 表现为"偶发解析失败", 极难复现。</remarks>
    private static string ParametersText(JsonElement parameters, string toolName)
    {
        try
        {
            return parameters.ValueKind == JsonValueKind.Undefined
                ? EmptySchema
                : parameters.GetRawText();
        }
        catch (Exception ex)
        {
            // ValueKind=Undefined 时 GetRawText() 会抛; 另有一种是自定义 ITool 返回了已失效的 JsonElement
            Log.Warn(LogCategory, $"工具 {toolName} 的参数 schema 不可读, 降级为空对象 schema: {ex.Message}");
            return EmptySchema;
        }
    }
}
