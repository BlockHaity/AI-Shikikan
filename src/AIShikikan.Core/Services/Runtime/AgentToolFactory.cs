using System.Text;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Tools.Builtin;

namespace AIShikikan.Core.Services.Runtime;

/// <summary>构建 LLM 可见工具集: 基础只读/git 工具 + 子代理工具(run_&lt;agent&gt; / assign_task / run_subagents)。
///
/// <para><b>关于 <see cref="AgentExecutionScope"/> 参数(新增, 可选)</b>:
/// 子代理工具与 <c>git_create_checkpoint</c> 原先反向读静态单例 <c>CommanderRuntime.Instance</c>;
/// 工具层被搬到独立 Worker 进程后单例里没有 GUI 侧状态, 反向读会**静默**退化成
/// 「未授权 / 空表 / 空人格」而不报错。现改为由调用方显式注入 scope。
/// <b>scope 为 null 时的降级语义与改动前(单例为 null 时)逐字一致</b>, 详见
/// <see cref="AgentExecutionScope"/> 类注释: 人格不落到指挥官人格、roster 取空表(压缩恒关)、
/// Plan 授权恒 false(执行兜底不拒绝)、会话 id 退化为空串。既有调用方不传 scope 仍可编译运行,
/// 但会失去会话级子代理配置(压缩开关 / Plan 授权), 接线时应显式传入。</para>
/// </summary>
public static class AgentToolFactory
{
    /// <summary>固定基础工具: 只读文件工具 + git 工具(始终注册)。
    ///
    /// <para><paramref name="scope"/> 仅 <c>git_create_checkpoint</c> 会用(工具卡归属会话 id 的兜底);
    /// 为 null 时归属 id 直接取 <c>ToolContext.SessionId</c>, 取不到则留空串
    /// (等价于今天读不到单例的行为)。</para></summary>
    public static IReadOnlyList<ITool> CreateCoreTools(GitService git, AgentExecutionScope? scope = null)
    {
        return new List<ITool>
        {
            new ReadFileTool(),
            new GlobTool(),
            new GrepTool(),
            new ListDirectoryTool(),
            new GitStatusTool(git),
            new GitAddTool(git),
            new GitCommitTool(git),
            new GitCreateCheckpointTool(git, scope),
            new GitDiffTool(git),
            new AskUserTool()
        };
    }

    /// <summary>子代理工具: run_&lt;agent&gt; / assign_task / run_subagents(随右侧栏开关注册/注销)。
    ///
    /// <para><paramref name="scope"/> 为 null 时按 <see cref="AgentExecutionScope"/> 的降级约定执行
    /// (无指挥官人格兜底 / roster 空表 / Plan 授权恒 false), 行为等价于改动前单例不可用的场景。</para></summary>
    public static IReadOnlyList<ITool> CreateSubagentTools(
        IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        AssignmentManager assignments,
        LlmService? llm = null,
        AgentExecutionScope? scope = null)
    {
        // 注: 三个子代理工具本身不直接操作 git(只经 AssignmentManager 派发), 原先的 git 参数
        // 从构造起就只被存成字段而从不读取(仅 `_ = git;` 丢弃), 属纯死参数, 已删除 ——
        // 留着只会让人误以为子代理工具有 git 副作用。
        var list = new List<ITool>();
        foreach (var agent in agents)
        {
            list.Add(new AgentExecutionTool(agent, personas, templates, assignments, llm, scope));
        }

        list.Add(new AssignTaskTool(agents, personas, templates, assignments, llm, scope));
        list.Add(new SubagentGroupTool(agents, personas, templates, assignments, llm, scope));
        return list;
    }
}

/// <summary>单个子代理的执行器(提示词构建 + 派发 + 输出压缩 + 工具卡数据)。
///
/// <para>原为 <c>static class</c>, 内部 6 处反向读 <c>CommanderRuntime.Instance</c>(人格文本 /
/// Plan 授权 / 会话 roster)。改为实例类并把这份全局状态经 <see cref="AgentExecutionScope"/>
/// 显式注入: 工具执行不再依赖进程单例, 独立 Worker 进程里也能正确装配。</para>
///
/// <para><see cref="ResolvePersonaText"/> / <see cref="BuildFinalPrompt"/> / <see cref="ResolveWorkingDir"/>
/// 刻意**保持 public static**: 它们是纯函数(不读任何全局状态), 且 GUI 的
/// <c>AppShell.Dispatch</c>(Agent 面板手动分派)直接静态调用这两个; 改成实例私有会打破
/// 那条 UI 入口。它们"不碰单例"本身就是这次解耦要达到的状态。</para>
/// </summary>
public sealed class AgentExecutor
{
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;
    private readonly AssignmentManager _assignments;
    private readonly LlmService? _llm;
    private readonly AgentExecutionScope? _scope;

    public AgentExecutor(
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        AssignmentManager assignments,
        LlmService? llm,
        AgentExecutionScope? scope = null)
    {
        _personas = personas;
        _templates = templates;
        _assignments = assignments;
        _llm = llm;
        _scope = scope;
    }

    /// <summary>工具层的人格解析入口: personaId / templateId 恒为 null, useCommanderPersona 恒 false
    /// (LLM 无法指定专家, 只能吃用户配置), 指挥官人格兜底文本取自 <see cref="AgentExecutionScope"/>。
    ///
    /// <para>scope 为 null ⇒ 指挥官人格兜底文本为 null, 等价于改动前读不到单例时的行为
    /// (最终仍可由「Agent 推荐专家」兜底, 与今天一致)。</para></summary>
    public string ResolveToolPersonaText(CliAgentDefinition def, bool planMode)
        => ResolvePersonaText(def, _personas, _templates, null, null,
            _scope?.CommanderPersonaText, false, planMode);

    /// <summary>解析注入子代理提示词的人格/模板文本。
    /// ⚠️ <paramref name="personaId"/> / <paramref name="templateId"/> / <paramref name="useCommanderPersona"/>
    /// 只可能来自 UI 侧入口(AppShell.Dispatch 等), LLM 可调用的三个子代理工具**恒传 null / false** ——
    /// 三个工具的 JSON Schema 都不含这两个字段, AI 指定不了专家。因此对 LLM 而言实际优先级只有
    /// 「用户配置(推荐专家 / 指挥官人格, 由 UI 决定)」这一层; 保留参数是为了不破坏 UI 入口的既有能力。</summary>
    public static string ResolvePersonaText(
        CliAgentDefinition def,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        string? personaId,
        string? templateId,
        string? commanderPersonaText = null,
        bool useCommanderPersona = false,
        bool planMode = false)
    {
        if (!string.IsNullOrWhiteSpace(personaId))
        {
            var persona = PersonaService.Find(personaId, personas);
            if (persona is not null)
            {
                return $"{persona.Display}\n{persona.ResolveForMode(planMode)}";
            }
        }

        if (useCommanderPersona && !string.IsNullOrWhiteSpace(commanderPersonaText))
        {
            return commanderPersonaText;
        }

        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var template = AgentTemplateService.Find(templateId, templates);
            if (template is not null)
            {
                if (!string.IsNullOrWhiteSpace(template.PersonaId))
                {
                    var persona = PersonaService.Find(template.PersonaId, personas);
                    if (persona is not null)
                    {
                        return $"{persona.Display}\n{persona.ResolveForMode(planMode)}";
                    }
                }

                return template.SystemPrompt;
            }
        }

        if (!string.IsNullOrWhiteSpace(def.RecommendedPersonaId))
        {
            var persona = PersonaService.Find(def.RecommendedPersonaId, personas);
            if (persona is not null)
            {
                return $"{persona.Display}\n{persona.ResolveForMode(planMode)}";
            }
        }

        return string.Empty;
    }

    public static string BuildFinalPrompt(string task, string personaText)
        => string.IsNullOrWhiteSpace(personaText)
            ? task
            : $"# 专家指令\n{personaText}\n\n# 任务\n{task}";

    public static string ResolveWorkingDir(string? requested, string root)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                // followLinks: 工作目录是显式指定的单一路径(不是遍历结果), 解析符号链接的
                // 成本可以接受, 而不解析就等于允许 "some-dir -> /etc" 这种把子代理工作目录
                // 指到工作区外的配置
                return ToolPathSanitizer.Resolve(root, requested, followLinks: true);
            }
            catch (UnauthorizedAccessException)
            {
                // 越界(含符号链接指向工作区外)不是"没有指定", 不能静默退到 root ——
                // 那会让子代理在用户完全没要求的情况下换了个目录运行, 且没有任何提示
                throw;
            }
            catch
            {
                return root;
            }
        }

        return root;
    }

    /// <summary>子代理输出进主上下文的长度上限。
    /// 与 <see cref="AssignmentManager.MaxOutputTailChars"/> 对齐(TailOf 已按该上限截断),
    /// 额外留出落盘截断标记的长度; 此处仍是必要的兜底, 防止 TailOf 上限被调大后无闸门地灌爆主上下文。</summary>
    private const int MaxSubagentOutputChars = AssignmentManager.MaxOutputTailChars + 64;

    /// <summary>派发并执行单个子代理: Plan 授权兜底 → 建 Assignment → 跑 CLI →(可选)压缩输出 → 工具卡数据。
    ///
    /// <para>实例方法: personas / templates / assignments / llm / scope 均已在构造期注入,
    /// 不再从参数逐次传入。<paramref name="personaText"/> 仍由调用方(工具层)传入 ——
    /// 它由工具从外部(推荐专家 / 指挥官人格)解析后得到, 放在这里比让执行器反向去查更清晰。</para>
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(
        CliAgentDefinition agent,
        string task,
        string personaText,
        string? workDirAbs,
        ToolContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return ToolResult.Error("缺少参数: task");
        }

        // Plan 模式执行层兜底: 仅允许配置了 plan_args 且开启"在 Plan 模式中使用"的子代理,
        // 防止 AI 通过 assign_task / run_subagents 的 agentId 参数绕过工具注册过滤。
        // 短路条件保留原样: 判据缺失(scope 或其 PlanAuthorizer 为 null, 含旧版"单例不可用"的场景)
        // 时**不拒绝** —— 原式是 `CommanderRuntime.Instance is { } runtime && !runtime.IsAgentAllowed…`,
        // 单例缺失即短路放行; 这里若改成"恒拒绝", 未注入判据的调用方(自检 / 半接线环境)会突然
        // 一个子代理都跑不了, 属于比原行为更苛刻的静默变更。
        if (ctx.IsPlanMode && _scope?.PlanAuthorizer is { } authorizer && !authorizer(ctx.SessionId, agent))
        {
            return ToolResult.Error($"[agent:{agent.Id}] 拒绝执行: 该子代理未开启\"在 Plan 模式中使用\", Plan 模式下不可调用。");
        }

        var finalPrompt = BuildFinalPrompt(task, personaText);

        // 工作目录兜底放在这里(而不是只依赖调用方的 ResolveWorkingDir): AssignmentManager.Create 之后
        // 就再没有 ctx, 万一工作目录为空, 它只会退到进程 CWD —— 子代理就会在工作区外运行。
        var workDir = string.IsNullOrWhiteSpace(workDirAbs)
            ? ctx.WorkspaceRoot
            : workDirAbs;

        var assignment = _assignments.Create(agent, task, workingDirectory: workDir,
            planMode: ShouldRunInPlanMode(agent, ctx));

        var progress = ctx.OnToolOutput is not null
            ? new Progress<string>(ctx.OnToolOutput)
            : null;

        try
        {
            var (completed, run) = await _assignments.RunSyncAsync(assignment, finalPrompt, progress, ct);
            // OutputTail 由 RunSyncAsync 无条件赋值(TailOf), 恒非 null。旧代码写的是 "?? run.Output",
            // 那是个不可达分支, 且 run.Output 未截断 —— 真走到会绕过上限直接灌进主上下文, 故只留空串兜底。
            var tail = Truncate(completed.OutputTail ?? string.Empty, MaxSubagentOutputChars);

            // llm 允许为 null(调用方可只注册只读子代理工具): 此时跳过压缩, 直接透传原始输出。
            // 之前是 llm! 空 forgiving —— null 会在 CompactIfNeededAsync 内 NRE, 被下面的
            // catch(Exception) 吞成"执行失败", 把一次成功的子代理调用报成失败。
            // provider/model 转发当前回合快照, 避免用户切了非默认模型后压缩落到另一个模型。
            // 取局部量再做 null 判定: 字段的可空状态窄化跨 await 不可靠, 用局部量与改动前写法一致。
            var llm = _llm;
            var body = llm is null
                ? tail
                : await SubagentCompactService.CompactIfNeededAsync(
                    llm, agent.Display, tail, IsCompactEnabled(agent.Id, ctx), ct,
                    ctx.ProviderId, ctx.Model);

            var header = $"[agent:{agent.Display}] 完成 (exit {run.ExitCode}, 耗时 {(int)run.Elapsed.TotalSeconds}s)";

            // 结构化卡片数据: 单个子 agent 结果框
            var detail = new SubagentsDetail
            {
                Subagents =
                [
                    new SubagentResultEntry
                    {
                        AgentId = agent.Id,
                        AgentName = agent.Display,
                        ExitCode = run.ExitCode,
                        ElapsedSeconds = (int)run.Elapsed.TotalSeconds,
                        TimedOut = run.TimedOut,
                        Output = body
                    }
                ]
            };
            return new ToolResult { Content = $"{header}\n\n{body}", Detail = detail };
        }
        catch (OperationCanceledException)
        {
            // 取消必须上抛: 被下面 catch(Exception) 吞掉会让"停止"按钮只停住主循环,
            // 子代理进程仍在后台跑, 用户以为停了实际没停。
            throw;
        }
        catch (Exception ex)
        {
            // 执行失败但部分变更可能已落在工作区, 回滚入口是"上一条用户消息"的检查点
            return new ToolResult
            {
                IsError = true,
                Content = $"[agent:{agent.Display}] 执行失败: {ex.Message}",
                Detail = new SubagentsDetail
                {
                    Subagents =
                    [
                        new SubagentResultEntry
                        {
                            AgentId = agent.Id,
                            AgentName = agent.Display,
                            IsError = true,
                            Output = ex.Message
                        }
                    ]
                }
            };
        }
    }

    /// <summary>主对话处于 Plan 模式且该子代理被会话配置允许时, 以 Plan 模式启动
    /// (需 Agent 配置了 plan_args, 或开启"无 plan 参数也可在 Plan 模式使用"开关)。</summary>
    private bool ShouldRunInPlanMode(CliAgentDefinition agent, ToolContext ctx)
    {
        // Plan 授权判据统一收敛到一处(与工具注册/执行兜底同一份规则),
        // 避免这里再抄一遍「plan_args 非空 + Roster.UseInPlanMode」而与注册侧漂移;
        // 判据本体仍在 CommanderRuntime.IsAgentPlanModeAuthorized, 这里只经 scope 委托调用。
        if (!ctx.IsPlanMode)
        {
            return false;
        }

        // 判据缺失 ⇒ 恒 false: 与改动前 `CommanderRuntime.Instance?.IsAgentRegisteredInPlanMode(agent) ?? false`
        // 在单例不可用时的结果一致(不启动 Plan 模式, 即子代理按普通模式跑)。
        return _scope?.PlanAuthorizer?.Invoke(ctx.SessionId, agent) ?? false;
    }

    /// <summary>解析该子代理在**发起本回合的会话**里的输出压缩开关
    /// (右侧栏会话子代理配置, roster.json 持久化)。按会话读取而非"当前活动会话",
    /// 否则后台会话的子代理会读到用户在别处切过的开关。</summary>
    private bool IsCompactEnabled(string agentId, ToolContext ctx)
    {
        return RosterOf(ctx).FirstOrDefault(
            e => string.Equals(e.AgentId, agentId, StringComparison.OrdinalIgnoreCase))?.CompactEnabled ?? false;
    }

    /// <summary>取本回合所属会话的 Roster 条目(经 scope 委托)。ctx.SessionId 由引擎注入;
    /// 未注入(旧引擎/自检)时是否回退到活动会话由 <c>AgentExecutionScope.RosterResolver</c>
    /// 的实现方决定, 行为与改动前一致。</summary>
    private IReadOnlyList<AgentRosterEntry> RosterOf(ToolContext ctx)
    {
        // 解析器缺失 ⇒ 空表: 与改动前 `CommanderRuntime.Instance is null → return []` 一致
        // (压缩开关因此恒 false, 不会因缺依赖而崩在空引用上)。
        var resolver = _scope?.RosterResolver;
        if (resolver is null)
        {
            return [];
        }

        // 解析器返 null ⇒ 同样按空表处理: RosterBuilder 的 null=「无限制」只属于提示词注入语义,
        // 泄漏到工具侧会把压缩开关退化成「总是压缩」, 与今天的 GetRosterEntriesFor 返回空表不一致。
        return resolver(ctx.SessionId) ?? [];
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[^max..] + "\n...(较长已截断)";
}

public class AgentExecutionTool : ITool
{
    private readonly CliAgentDefinition _agent;
    private readonly AgentExecutor _executor;

    public AgentExecutionTool(CliAgentDefinition agent, IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates, AssignmentManager assignments,
        LlmService? llm = null, AgentExecutionScope? scope = null)
    {
        _agent = agent;
        _executor = new AgentExecutor(personas, templates, assignments, llm, scope);
    }

    public string Name => $"run_{_agent.Id}";

    // 无论 Agent 自带描述是什么, 都追加统一的执行环境说明: 子代理跑在当前分支当前目录,
    // 不建 worktree、不切分支, 所以它看到/改动的就是主 agent 的工作区。
    public string Description => (string.IsNullOrWhiteSpace(_agent.Description)
        ? $"调用命令行 Agent「{_agent.Display}」执行子任务。"
        : _agent.Description) +
        "该 Agent 在当前分支、当前工作目录下执行(不建 worktree、不切分支), 它的改动直接落在你的工作区, " +
        "阻塞式调用: 等它执行结束才返回结果。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "子任务描述" },
            "workingDirectory": { "type": "string", "description": "运行目录(可选)" }
          },
          "required": ["task"]
        }
        """);

    public bool RequiresApproval => _agent.RequireApproval;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var task = Get(args, "task");
        var workDir = Get(args, "workingDirectory");

        // 专家由用户配置决定(agents.toml 推荐专家 / 面板设置), AI 不可指定人格/模板
        var personaText = _executor.ResolveToolPersonaText(_agent, ctx.IsPlanMode);
        return _executor.ExecuteAsync(
            _agent, task ?? string.Empty, personaText,
            AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
            ctx, ct);
    }

    private static string? Get(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

public class AssignTaskTool : ITool
{
    private readonly IReadOnlyList<CliAgentDefinition> _agents;
    private readonly AgentExecutor _executor;

    public AssignTaskTool(IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas, IReadOnlyList<AgentTemplate> templates,
        AssignmentManager assignments, LlmService? llm = null, AgentExecutionScope? scope = null)
    {
        _agents = agents;
        _executor = new AgentExecutor(personas, templates, assignments, llm, scope);
    }

    public string Name => "assign_task";

    // 描述必须与实现一致: 本工具是阻塞式的, 返回值是子代理的最终输出(而非分派受理回执)。
    // 旧描述宣称"返回 assignmentId"会让 LLM 以为可以拿 ID 后续查询, 然后反复空转 —— 该 ID 不在返回值里。
    public string Description => "把任务分派给合适的 Agent: 可显式指定 agentId, 未指定时自动选择可用 Agent。" +
        "专家由用户配置决定(推荐专家), 调用时无法指定人格/模板。" +
        "阻塞式调用: 等待该 Agent 执行结束, 直接返回它的最终输出(退出码与结果正文), 不返回分派 ID。" +
        "该 Agent 在当前分支、当前工作目录下执行(不建 worktree、不切分支), 它的改动直接落在你的工作区。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "要执行的任务" },
            "agentId": { "type": "string", "description": "目标Agent ID(可选, 不填自动匹配)" },
            "workingDirectory": { "type": "string", "description": "运行目录(可选)" }
          },
          "required": ["task"]
        }
        """);

    public bool RequiresApproval => true;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var task = Get(args, "task");
        var agentId = Get(args, "agentId");
        var workDir = Get(args, "workingDirectory");

        var agent = ResolveAgent(agentId, task ?? string.Empty);
        if (agent is null)
        {
            return Task.FromResult(ToolResult.Error(
                $"找不到可用 Agent。已配置: {string.Join(", ", _agents.Select(a => a.Id))}"));
        }

        // 专家由用户配置决定, AI 不可指定人格/模板
        var personaText = _executor.ResolveToolPersonaText(agent, ctx.IsPlanMode);
        return _executor.ExecuteAsync(
            agent, task ?? string.Empty, personaText,
            AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
            ctx, ct);
    }

    private CliAgentDefinition? ResolveAgent(string? agentId, string task)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            return AgentConfigService.Find(agentId, _agents);
        }

        return _agents.FirstOrDefault();
    }

    private static string? Get(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>读取字符串数组参数(用于文件范围), 忽略非字符串元素。</summary>
    internal static string? GetStringList(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        return items.Count == 0 ? null : string.Join("\n", items);
    }
}

public class SubagentGroupTool : ITool
{
    private readonly IReadOnlyList<CliAgentDefinition> _agents;
    private readonly AgentExecutor _executor;

    public SubagentGroupTool(IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas, IReadOnlyList<AgentTemplate> templates,
        AssignmentManager assignments, LlmService? llm = null, AgentExecutionScope? scope = null)
    {
        _agents = agents;
        _executor = new AgentExecutor(personas, templates, assignments, llm, scope);
    }

    public string Name => "run_subagents";

    public string Description => "并发调用多个子Agent, 各自执行指定的子任务, 等全部完成后统一返回每个子Agent的结果。" +
        "适合把一个大任务拆成多个相互独立的子任务并行处理。建议每个子任务只分配给一个 Agent。" +
        "会同时启动多个子 Agent 并改动工作区, 需批准。" +
        "重要: 所有子 Agent 都在同一个分支、同一个工作目录下工作(不建 worktree、不切分支), " +
        "并发改动同一文件会互相覆盖。因此每个子任务必须限定自己负责的文件范围, 与其他子任务互不重叠; " +
        $"同时启动的子 Agent 最多 {MaxConcurrentSubagents} 个, 超出部分会排队等前面的完成。";

    /// <summary>同时启动的子 Agent 数量上限。超出部分排队等前面的完成。
    /// 无上限时每个子代理都会拉起一个 CLI 进程, 十几个并发会把内存/CPU 与工作区 IO 一起打满,
    /// 且每个子代理输出还要落盘(12KB/条), 反而拖慢整体。</summary>
    public const int MaxConcurrentSubagents = 4;

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "subagents": {
              "type": "array",
              "description": "子任务列表。并发上限 4 个, 超出部分排队。因并发改动同一分支同一目录, 每个子任务的 files 必须互不重叠。",
              "maxItems": 16,
              "items": {
                "type": "object",
                "properties": {
                  "agentId": { "type": "string", "description": "目标Agent ID(可选, 不填自动匹配)" },
                  "task": { "type": "string", "description": "该子Agent要执行的任务" },
                  "files": { "type": "array", "items": { "type": "string" }, "description": "本子任务负责的文件/目录(可选)。会写进子代理提示词, 用于划清并发边界 —— 同批子任务之间不要重叠。" },
                  "workingDirectory": { "type": "string", "description": "运行目录(可选)" }
                },
                "required": ["task"]
              }
            }
          },
          "required": ["subagents"]
        }
        """);

    public bool RequiresApproval => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!args.TryGetProperty("subagents", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return ToolResult.Error("缺少参数: subagents");
        }

        var subItems = items.EnumerateArray().ToList();
        if (subItems.Count == 0)
        {
            return ToolResult.Error("subagents 为空");
        }

        // 并发闸: 一次最多 MaxConcurrentSubagents 个子 Agent 同时在跑, 其余排队。
        // 用 SemaphoreSlim 而非 Task.Delay 错峰: 闸要跟着整个 Task.WhenAll 一起释放,
        // 放在 try 外声明才能在 await 抛异常(取消)时仍能进 finally 释放。
        var gate = new SemaphoreSlim(MaxConcurrentSubagents, MaxConcurrentSubagents);
        SubRunResult[] runs;
        try
        {
            runs = await Task.WhenAll(subItems.Select(async el =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await RunOneAsync(el, ctx, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }

        // 结构化卡片数据: 每个子 agent 一个独立结果框
        var detail = new SubagentsDetail { Subagents = runs.Select(r => r.Entry).ToList() };
        return new ToolResult
        {
            Content = string.Join("\n\n---\n\n", runs.Select(r => r.Result.Content)),
            IsError = runs.Any(r => r.Result.IsError),
            Detail = detail
        };
    }

    private sealed record SubRunResult(ToolResult Result, SubagentResultEntry Entry);

    private async Task<SubRunResult> RunOneAsync(JsonElement el, ToolContext ctx, CancellationToken ct)
    {
        var task = Get(el, "task");
        var agentId = Get(el, "agentId");
        var workDir = Get(el, "workingDirectory");

        var agent = ResolveAgent(agentId, task ?? string.Empty);
        if (agent is null)
        {
            var notFound = $"[agent:{agentId ?? "?"}] 失败: 找不到可用 Agent。已配置: {string.Join(", ", _agents.Select(a => a.Id))}";
            return new SubRunResult(
                ToolResult.Error(notFound),
                new SubagentResultEntry
                {
                    AgentId = agentId ?? "?",
                    AgentName = agentId ?? "?",
                    IsError = true,
                    Output = notFound
                });
        }

        try
        {
            // 并发时子代理同分支同目录工作, 文件范围必须由 AI 显式声明才能划清边界 ——
            // 程序无法强制外部 CLI 不碰某个文件, 只能把 AI 声明的范围写进它的任务提示词。
            var scope = AssignTaskTool.GetStringList(el, "files");
            var scopedTask = task ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(scope))
            {
                scopedTask = $"{scopedTask}\n\n# 文件范围(并发隔离)\n" +
                    "本任务只允许改动以下文件/目录, 不要改动其他文件(同批并发的子代理正在处理其他范围):\n" +
                    scope;
            }

            // 专家由用户配置决定, AI 不可指定人格/模板
            var personaText = _executor.ResolveToolPersonaText(agent, ctx.IsPlanMode);

            // 不再伪造空 args 往下传: AgentExecutor 不读 templateId/personaId(AI 不可指定专家),
            // 之前这份空对象只会让 GetOpt 恒返回 null, 与 AppShell.Dispatch 的真实路径行为不一致。
            var result = await _executor.ExecuteAsync(
                agent, scopedTask, personaText,
                AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
                ctx, ct);

            var entry = (result.Detail as SubagentsDetail)?.Subagents.FirstOrDefault()
                ?? new SubagentResultEntry
                {
                    AgentId = agent.Id,
                    AgentName = agent.Display,
                    IsError = result.IsError,
                    Output = result.Content
                };
            return new SubRunResult(result, entry);
        }
        catch (OperationCanceledException)
        {
            // 取消必须上抛: 用户停止 / 回合取消时要让 Task.WhenAll 整体中断,
            // 否则被下面 catch(Exception) 吞成单条失败, 其余子代理仍在跑, 停止按钮失灵。
            throw;
        }
        catch (Exception ex)
        {
            // 含 MCP tools/call 超时(TimeoutException): 单个子代理超时不应拖垮整批,
            // 转成该条目的失败结果, 其余子代理正常返回。
            return new SubRunResult(
                ToolResult.Error($"[agent:{agent.Id}] 执行失败: {ex.Message}"),
                new SubagentResultEntry
                {
                    AgentId = agent.Id,
                    AgentName = agent.Display,
                    IsError = true,
                    Output = ex.Message
                });
        }
    }

    private CliAgentDefinition? ResolveAgent(string? agentId, string task)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            return AgentConfigService.Find(agentId, _agents);
        }

        var tk = task.Trim();
        return _agents.FirstOrDefault(a =>
            tk.Contains(a.Id, StringComparison.OrdinalIgnoreCase))
            ?? _agents.FirstOrDefault();
    }

    private static string? Get(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

/// <summary>git_status: 只读查看仓库状态(分支 / 脏标记 / 变更文件列表)。</summary>
public class GitStatusTool : ITool
{
    private readonly GitService _git;

    public GitStatusTool(GitService git) => _git = git;

    public string Name => "git_status";

    public string Description => "查看当前工作区所在 git 仓库的状态: 仓库根目录 / 当前分支 / 最近提交 / 工作区是否干净 / 变更文件列表。只读, 不改动任何内容。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        { "type": "object", "properties": {} }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var gctx = _git.ResolveContext(ctx.WorkspaceRoot);
            if (!gctx.IsValidRepo)
            {
                return Task.FromResult(ToolResult.Ok("当前目录不是 git 仓库。"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"仓库: {gctx.RepositoryRoot}");
            sb.AppendLine($"分支: {(gctx.IsDetachedHead ? "(detached HEAD)" : gctx.BranchName)}");
            if (gctx.IsEmptyRepo)
            {
                sb.AppendLine("仓库尚无任何提交。");
            }
            else
            {
                var head = _git.GetHeadSha(gctx.RepositoryRoot);
                if (!string.IsNullOrWhiteSpace(head))
                {
                    sb.AppendLine($"最近提交: {head[..Math.Min(8, head.Length)]}");
                }
            }

            var clean = _git.IsClean(gctx).Succeeded;
            sb.AppendLine($"工作区: {(clean ? "干净" : "有未提交变更")}");

            var files = _git.GetStatusFiles(gctx);
            if (files.Count > 0)
            {
                sb.AppendLine($"变更文件 ({files.Count}):");
                foreach (var f in files)
                {
                    sb.AppendLine($"- [{f.StatusLabel}] {f.Path}");
                }
            }

            return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
    }
}

/// <summary>git_add: 把指定文件或全部改动加入暂存区。影响后续提交内容, 需批准。</summary>
public class GitAddTool : ITool
{
    private readonly GitService _git;

    public GitAddTool(GitService git) => _git = git;

    public string Name => "git_add";

    public string Description => "暂存文件到 git 暂存区。all=true 暂存全部改动, 否则暂存 path 指定的文件(相对仓库根)。需批准。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "相对仓库根的文件路径" },
            "all": { "type": "boolean", "description": "是否暂存全部改动(与 path 二选一)" }
          }
        }
        """);

    public bool RequiresApproval => true;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var gctx = _git.ResolveContext(ctx.WorkspaceRoot);
            if (!gctx.IsValidRepo) return Task.FromResult(ToolResult.Error("当前目录不是 git 仓库。"));

            var all = args.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
            var path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            GitCommandResult? r = all
                ? _git.StageAll(gctx)
                : !string.IsNullOrWhiteSpace(path) ? _git.StageFile(gctx, path!)
                : null;
            if (r is null) return Task.FromResult(ToolResult.Error("需要参数: path 或 all=true"));

            return Task.FromResult(r.Succeeded
                ? ToolResult.Ok(r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : "已暂存。")
                : ToolResult.Error($"git add 失败: {r.Stderr.Trim()}"));
        }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
    }
}

/// <summary>git_commit: 提交暂存区内容(或指定文件)。写入仓库历史, 需批准。</summary>
public class GitCommitTool : ITool
{
    private readonly GitService _git;

    public GitCommitTool(GitService git) => _git = git;

    public string Name => "git_commit";

    public string Description =>
        "提交 git 变更。提供 files 时只提交这些文件(自动暂存), 否则提交当前暂存区的全部内容。" +
        "message 为提交说明。会写入仓库历史, 需批准。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "提交说明" },
            "files": {
              "type": "array",
              "items": { "type": "string" },
              "description": "可选: 只提交这些文件(相对仓库根), 不填则提交暂存区全部内容"
            }
          },
          "required": ["message"]
        }
        """);

    public bool RequiresApproval => true;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var gctx = _git.ResolveContext(ctx.WorkspaceRoot);
            if (!gctx.IsValidRepo) return Task.FromResult(ToolResult.Error("当前目录不是 git 仓库。"));
            if (gctx.IsDetachedHead) return Task.FromResult(ToolResult.Error("HEAD 处于 detached 状态, 无法提交。"));

            var message = args.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(message))
            {
                return Task.FromResult(ToolResult.Error("缺少参数: message"));
            }

            var files = new List<string>();
            if (args.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                files.AddRange(f.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            var r = files.Count > 0
                ? _git.CommitFiles(gctx, files, message)
                : _git.Commit(gctx, message);
            if (!r.Succeeded)
            {
                return Task.FromResult(ToolResult.Error($"git commit 失败: {r.Stderr.Trim()}"));
            }

            var sha = _git.GetHeadSha(gctx.RepositoryRoot);
            var shortSha = string.IsNullOrWhiteSpace(sha) ? "" : sha[..Math.Min(8, sha.Length)];
            var scope = files.Count > 0 ? $"{files.Count} 个指定文件" : "暂存区全部内容";
            var extra = r.Stdout.Trim();
            return Task.FromResult(ToolResult.Ok(
                $"已提交({scope}){(shortSha.Length > 0 ? $", commit {shortSha}" : "")}。" +
                (extra.Length > 0 ? $"\n{extra}" : "")));
        }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
    }
}

/// <summary>git_create_checkpoint: 把当前 HEAD 标记为检查点(轻量标签 + 持久化记录), 供 UI 回滚/分叉使用。</summary>
public class GitCreateCheckpointTool : ITool
{
    /// <summary>AI 工具无法读取持久化消息总数, 用极大值表示"保留全部对话":
    /// UI 侧 Truncate/Fork 都先做 cutoff &lt; Messages.Count 判定, 不会越界也不会截断。</summary>
    private const int KeepAllConversationCutoff = int.MaxValue;

    private readonly GitService _git;
    private readonly AgentExecutionScope? _scope;

    /// <summary><paramref name="scope"/> 仅用于「工具卡归属会话」的兜底(见 <see cref="ExecuteAsync"/>);
    /// 为 null 时只按 <c>ToolContext.SessionId</c> 归属, 取不到即空串。</summary>
    public GitCreateCheckpointTool(GitService git, AgentExecutionScope? scope = null)
    {
        _git = git;
        _scope = scope;
    }

    public string Name => "git_create_checkpoint";

    public string Description =>
        "把当前 HEAD 提交标记为一个检查点, 用于留存可回滚的快照(在本地创建轻量标签并记录元数据, 不改动工作区文件)。" +
        "参数: label(可选, 检查点用途描述)。返回检查点 ID 与标签名;" +
        "之后可在 UI 的检查点卡片上一键回滚或派生分支。需批准。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "label": { "type": "string", "description": "检查点用途描述(可选)" }
          }
        }
        """);

    public bool RequiresApproval => true;

    /// <summary>解析这条检查点记录应归属的会话 id。
    ///
    /// <para><b>为什么优先用 <c>ToolContext.SessionId</c> 而非「全局活动会话」</b>:
    /// 工具卡的回滚 / Fork 是**会话内**操作(按 SessionId 找会话、按 ConversationCutoff 裁剪对话),
    /// 而「活动会话」是用户当前正在看的那个 —— 多会话并发(后台会话在跑子代理)时两者根本不是同一个,
    /// 挂错会话会让检查点卡片出现在无关会话里, 回滚时裁错对话。
    /// 引擎每个回合都会注入 SessionId, 发起这次工具调用的会话就是权威答案。</para>
    ///
    /// <para>ctx.SessionId 为空(旧引擎路径 / 自检 / 未注入)时才回退到 scope 的兜底来源;
    /// 两者都没有则留空串(等价于改动前读不到单例时的行为)。</para></summary>
    private string ResolveSessionId(ToolContext ctx)
    {
        if (!string.IsNullOrEmpty(ctx.SessionId))
        {
            return ctx.SessionId;
        }

        return _scope?.ActiveSessionIdResolver?.Invoke() ?? string.Empty;
    }

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var gctx = _git.ResolveContext(ctx.WorkspaceRoot);
            if (!gctx.IsValidRepo) return Task.FromResult(ToolResult.Error("当前目录不是 git 仓库。"));

            var head = _git.GetHeadSha(gctx.RepositoryRoot);
            if (string.IsNullOrWhiteSpace(head))
            {
                return Task.FromResult(ToolResult.Error("仓库尚无任何提交, 无法创建检查点。"));
            }

            var label = args.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()?.Trim()
                : null;
            var id = Guid.NewGuid().ToString("N")[..8];
            var record = new GitCheckpointRecord
            {
                Id = id,
                RepositoryRoot = gctx.RepositoryRoot,
                WorkDir = gctx.WorkDir,
                BranchName = gctx.BranchName,
                CommitSha = head,
                TagName = $"ai-shikikan/checkpoint/{id}",
                SessionId = ResolveSessionId(ctx),
                ConversationCutoff = KeepAllConversationCutoff,
                Source = GitCheckpointSource.AiTool,
                Label = string.IsNullOrWhiteSpace(label) ? "AI 检查点" : label,
                CreatedAt = DateTime.Now
            };

            var r = _git.MarkCheckpoint(gctx, record);
            if (!r.Succeeded)
            {
                return Task.FromResult(ToolResult.Error($"创建检查点失败: {r.Stderr.Trim()}"));
            }

            Log.Info("Git", $"AI 创建检查点: {record.Id} (分支 {record.BranchName}, 标签 {record.TagName})");
            return Task.FromResult(new ToolResult
            {
                Content = $"检查点已创建: {record.Id} [{record.Label}]\n" +
                          $"标签: {record.TagName}\n" +
                          $"提交: {head[..Math.Min(8, head.Length)]} @ 分支 {record.BranchName}\n" +
                          "可在 UI 的检查点卡片上一键回滚或派生分支。",
                StepId = record.Id,
                Detail = new CheckpointDetail
                {
                    CheckpointId = record.Id,
                    Label = record.Label,
                    ShortSha = head[..Math.Min(8, head.Length)],
                    FullSha = head,
                    BranchName = record.BranchName,
                    WorkDir = record.WorkDir,
                    Source = GitCheckpointSource.AiTool,
                    CreatedAt = record.CreatedAt,
                    SessionId = record.SessionId,
                    ConversationCutoff = record.ConversationCutoff
                }
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"创建检查点失败: {ex.Message}"));
        }
    }
}

/// <summary>git_diff: 只读查看两个提交之间的差异(可选仅看统计)。</summary>
public class GitDiffTool : ITool
{
    private const int MaxDiffChars = 26000;

    private readonly GitService _git;

    public GitDiffTool(GitService git) => _git = git;

    public string Name => "git_diff";

    public string Description =>
        "查看两个提交之间的差异。参数: from(必填, 起点 sha/ref)、to(可选, 终点 sha/ref, 默认 HEAD)、" +
        "stat(可选, 只看统计)。只读, 不改动任何内容。" +
        "注意: sha/ref 只接受提交哈希、HEAD 或普通分支名 —— 不接受 --开头的选项、" +
        "rev:path 语法与 @{...} 修订后缀(如 v1@{0}), 这些会被服务端拒绝。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "起点: 提交哈希、HEAD 或分支名(不接受选项/路径/修订后缀)" },
            "to": { "type": "string", "description": "终点: 提交哈希、HEAD 或分支名(默认 HEAD, 不接受选项/路径/修订后缀)" },
            "stat": { "type": "boolean", "description": "是否只输出 --stat 统计(默认 false)" }
          },
          "required": ["from"]
        }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            var gctx = _git.ResolveContext(ctx.WorkspaceRoot);
            if (!gctx.IsValidRepo) return Task.FromResult(ToolResult.Error("当前目录不是 git 仓库。"));

            var from = Get(args, "from");
            if (string.IsNullOrWhiteSpace(from))
            {
                return Task.FromResult(ToolResult.Error("缺少参数: from"));
            }

            var to = Get(args, "to");
            if (string.IsNullOrWhiteSpace(to))
            {
                to = "HEAD";
            }

            // from/to 由 LLM 给出, 会被拼成单个 argv "{from}..{to}" 交给 git。
            // 以 '-' 开头的值会被 git 当作选项解析(--output=<path> 可把 diff 写到仓库外文件),
            // 所以先做白名单校验再拼串。GitService.GetDiff 内部仍无校验(A10 的责任)。
            if (!IsSafeRevision(from!))
            {
                return Task.FromResult(ToolResult.Error(
                    $"参数 from 不是合法的 git 版本表达式: {from!.Trim()}。只接受 sha 或 ref(如 HEAD、main、HEAD~3、v1.0), 不接受以 '-' 开头或含空白/':' 的值。"));
            }

            if (!IsSafeRevision(to!))
            {
                return Task.FromResult(ToolResult.Error(
                    $"参数 to 不是合法的 git 版本表达式: {to!.Trim()}。只接受 sha 或 ref(如 HEAD、main、HEAD~3、v1.0), 不接受以 '-' 开头或含空白/':' 的值。"));
            }

            var statOnly = args.TryGetProperty("stat", out var s) && s.ValueKind == JsonValueKind.True;

            var statResult = _git.GetDiffStat(gctx, from!, to!);
            if (!statResult.Succeeded)
            {
                return Task.FromResult(ToolResult.Error($"git diff --stat 失败: {statResult.Stderr.Trim()}"));
            }

            if (statOnly)
            {
                var statText = statResult.Stdout.Trim();
                return Task.FromResult(ToolResult.Ok(statText.Length > 0
                    ? statText
                    : $"{from}..{to} 之间没有差异。"));
            }

            var diffResult = _git.GetDiff(gctx, from!, to!);
            if (!diffResult.Succeeded)
            {
                return Task.FromResult(ToolResult.Error($"git diff 失败: {diffResult.Stderr.Trim()}"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"范围: {from}..{to}");
            var statLine = statResult.Stdout.Trim();
            if (statLine.Length > 0) sb.AppendLine(statLine);
            var patch = diffResult.Stdout.Trim();
            sb.AppendLine(patch.Length > 0 ? patch : "(无内容差异)");

            var text = sb.ToString();
            if (text.Length > MaxDiffChars)
            {
                text = text[..MaxDiffChars] + "\n...(diff 过长已截断, 建议加 stat=true 只看统计)";
            }

            return Task.FromResult(ToolResult.Ok(text));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
    }

    private static string? Get(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>校验 git 版本表达式(sha / ref)是否可以安全地拼进 "A..B" 区间。
    /// 刻意**不**限制为纯 hex sha: 工具描述本就承诺 sha 或 ref(main、HEAD~3、tag 都是常见用法),
    /// 一刀切会让 git_diff 只能比历史提交。真正要挡住的是选项注入与区间/路径语义被篡改, 所以:
    /// '-' 开头(被 git 当选项)、空白与控制字符(单个 argv 内不该出现)、':' (rev:path 语法)、
    /// '..' (会被拼进区间串打乱 a..b 语义)一律拒绝。</summary>
    private static bool IsSafeRevision(string value)
    {
        var v = value.Trim();
        if (v.Length == 0 || v[0] == '-')
        {
            return false;
        }

        if (v.Contains("..", StringComparison.Ordinal) || v.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in v)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
