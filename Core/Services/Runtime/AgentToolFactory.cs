using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>构建 LLM 可见工具集: 基础只读/git 工具 + 子代理工具(run_&lt;agent&gt; / assign_task / run_subagents)。</summary>
public static class AgentToolFactory
{
    /// <summary>固定基础工具: 只读文件工具 + git 步骤工具(始终注册)。</summary>
    public static IReadOnlyList<ITool> CreateCoreTools(GitStepService git)
    {
        return new List<ITool>
        {
            new ReadFileTool(),
            new GlobTool(),
            new GrepTool(),
            new ListDirectoryTool(),
            new GitStatusTool(git),
            new GitCheckpointTool(git),
            new GitMergeStepTool(git),
            new GitDropStepTool(git),
            new GitRevertStepTool(git),
            new AskUserTool()
        };
    }

    /// <summary>子代理工具: run_&lt;agent&gt; / assign_task / run_subagents(随右侧栏开关注册/注销)。</summary>
    public static IReadOnlyList<ITool> CreateSubagentTools(
        IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        GitStepService git,
        AssignmentManager assignments,
        LlmService? llm = null)
    {
        var list = new List<ITool>();
        foreach (var agent in agents)
        {
            list.Add(new AgentExecutionTool(agent, personas, templates, git, assignments, llm));
        }

        list.Add(new AssignTaskTool(agents, personas, templates, git, assignments, llm));
        list.Add(new SubagentGroupTool(agents, personas, templates, git, assignments, llm));
        return list;
    }
}

public static class AgentExecutor
{
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
                return ToolPathSanitizer.Resolve(root, requested);
            }
            catch
            {
                return root;
            }
        }

        return root;
    }

    public static async Task<ToolResult> ExecuteAsync(
        CliAgentDefinition agent,
        string task,
        string personaText,
        string workDirAbs,
        JsonElement args,
        ToolContext ctx,
        AssignmentManager assignments,
        LlmService? llm,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return ToolResult.Error("缺少参数: task");
        }

        // Plan 模式执行层兜底: 仅允许配置了 plan_args 且开启"在 Plan 模式中使用"的子代理,
        // 防止 AI 通过 assign_task / run_subagents 的 agentId 参数绕过工具注册过滤
        if (ctx.IsPlanMode &&
            CommanderRuntime.Instance is { } runtime && !runtime.IsAgentAllowedInPlanMode(agent))
        {
            return ToolResult.Error($"[agent:{agent.Id}] 拒绝执行: 该子代理未开启\"在 Plan 模式中使用\", Plan 模式下不可调用。");
        }

        var finalPrompt = BuildFinalPrompt(task, personaText);

        var assignment = assignments.Create(agent, task,
            templateId: GetOpt(args, "templateId"),
            personaId: GetOpt(args, "personaId"),
            workingDirectory: workDirAbs,
            planMode: ShouldRunInPlanMode(agent, ctx));

        var progress = ctx.OnToolOutput is not null
            ? new Progress<string>(ctx.OnToolOutput)
            : null;

        try
        {
            var (completed, run) = await assignments.RunSyncAsync(assignment, finalPrompt, progress, ct);
            var tail = completed.OutputTail ?? run.Output;
            var header = $"[agent:{agent.Display}] 完成 (exit {run.ExitCode}, 耗时 {(int)run.Elapsed.TotalSeconds}s)\n检查点: {completed.StepId}";
            var body = await SubagentCompactService.CompactIfNeededAsync(
                llm!, agent.Display, Truncate(tail, 26000), IsCompactEnabled(agent.Id), ct);

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
                        Output = body,
                        StepId = completed.StepId
                    }
                ]
            };
            return new ToolResult { Content = $"{header}\n\n{body}", StepId = completed.StepId, Detail = detail };
        }
        catch (Exception ex)
        {
            // 执行失败但检查点分支可能已建立(含部分变更), 保留回滚入口
            return new ToolResult
            {
                IsError = true,
                Content = $"[agent:{agent.Display}] 执行失败: {ex.Message}",
                StepId = string.IsNullOrEmpty(assignment.StepId) ? null : assignment.StepId,
                Detail = new SubagentsDetail
                {
                    Subagents =
                    [
                        new SubagentResultEntry
                        {
                            AgentId = agent.Id,
                            AgentName = agent.Display,
                            IsError = true,
                            Output = ex.Message,
                            StepId = string.IsNullOrEmpty(assignment.StepId) ? null : assignment.StepId
                        }
                    ]
                }
            };
        }
    }

    /// <summary>主对话处于 Plan 模式且该子代理被会话配置允许时, 以 Plan 模式启动
    /// (需 Agent 配置了 plan_args, 或开启"无 plan 参数也可在 Plan 模式使用"开关)。</summary>
    private static bool ShouldRunInPlanMode(CliAgentDefinition agent, ToolContext ctx)
    {
        if (!ctx.IsPlanMode ||
            (agent.PlanArgs is not { Count: > 0 } && !agent.AllowPlanModeWithoutArgs))
        {
            return false;
        }

        return CommanderRuntime.Instance?.CurrentRosterEntries?.Any(
            e => string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase)
                 && e.UseInPlanMode) ?? false;
    }

    /// <summary>解析该子代理在当前会话的输出压缩开关(右侧栏会话子代理配置, roster.json 持久化)。</summary>
    private static bool IsCompactEnabled(string agentId)
    {
        return CommanderRuntime.Instance?.CurrentRosterEntries?.FirstOrDefault(
            e => string.Equals(e.AgentId, agentId, StringComparison.OrdinalIgnoreCase))?.CompactEnabled ?? false;
    }

    private static string? GetOpt(JsonElement args, string name)
    {
        return args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[^max..] + "\n...(较长已截断)";
}

public class AgentExecutionTool : ITool
{
    private readonly CliAgentDefinition _agent;
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;
    protected readonly GitStepService _git;
    private readonly AssignmentManager _assignments;
    private readonly LlmService? _llm;

    public AgentExecutionTool(CliAgentDefinition agent, IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates, GitStepService git, AssignmentManager assignments,
        LlmService? llm = null)
    {
        _agent = agent;
        _personas = personas;
        _templates = templates;
        _git = git;
        _assignments = assignments;
        _llm = llm;
    }

    public string Name => $"run_{_agent.Id}";

    public string Description => string.IsNullOrWhiteSpace(_agent.Description)
        ? $"调用命令行 Agent「{_agent.Display}」执行子任务。"
        : _agent.Description;

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
        var personaText = AgentExecutor.ResolvePersonaText(
            _agent, _personas, _templates, null, null,
            CommanderRuntime.Instance?.CurrentPersonaText,
            false,
            planMode: ctx.IsPlanMode);
        return AgentExecutor.ExecuteAsync(
            _agent, task ?? string.Empty, personaText,
            AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
            args, ctx, _assignments, _llm, ct);
    }

    private static string? Get(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

public class AssignTaskTool : ITool
{
    private readonly IReadOnlyList<CliAgentDefinition> _agents;
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;
    protected readonly GitStepService _git;
    private readonly AssignmentManager _assignments;
    private readonly LlmService? _llm;

    public AssignTaskTool(IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas, IReadOnlyList<AgentTemplate> templates,
        GitStepService git, AssignmentManager assignments, LlmService? llm = null)
    {
        _agents = agents;
        _personas = personas;
        _templates = templates;
        _git = git;
        _assignments = assignments;
        _llm = llm;
    }

    public string Name => "assign_task";

    public string Description => "把任务分派给合适的 Agent: 可显式指定 agentId, 未指定时自动选择可用 Agent。" +
        "专家由用户配置决定(推荐专家), 调用时无法指定人格/模板。返回 assignmentId。";

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
        var personaText = AgentExecutor.ResolvePersonaText(
            agent, _personas, _templates, null, null,
            CommanderRuntime.Instance?.CurrentPersonaText,
            false,
            planMode: ctx.IsPlanMode);
        return AgentExecutor.ExecuteAsync(
            agent, task ?? string.Empty, personaText,
            AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
            args, ctx, _assignments, _llm, ct);
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
}

public class SubagentGroupTool : ITool
{
    private readonly IReadOnlyList<CliAgentDefinition> _agents;
    private readonly IReadOnlyList<Persona> _personas;
    private readonly IReadOnlyList<AgentTemplate> _templates;
    private readonly GitStepService _git;
    private readonly AssignmentManager _assignments;
    private readonly LlmService? _llm;

    public SubagentGroupTool(IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas, IReadOnlyList<AgentTemplate> templates,
        GitStepService git, AssignmentManager assignments, LlmService? llm = null)
    {
        _agents = agents;
        _personas = personas;
        _templates = templates;
        _git = git;
        _assignments = assignments;
        _llm = llm;
    }

    public string Name => "run_subagents";

    public string Description => "并发调用多个子Agent, 各自执行指定的子任务, 等全部完成后统一返回每个子Agent的结果。" +
        "适合把一个大任务拆成多个相互独立的子任务并行处理。建议每个子任务只分配给一个 Agent。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "subagents": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "agentId": { "type": "string", "description": "目标Agent ID(可选, 不填自动匹配)" },
                  "task": { "type": "string", "description": "该子Agent要执行的任务" },
                  "workingDirectory": { "type": "string", "description": "运行目录(可选)" }
                },
                "required": ["task"]
              }
            }
          },
          "required": ["subagents"]
        }
        """);

    public bool RequiresApproval => false;

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

        var runs = await Task.WhenAll(subItems.Select(el => RunOneAsync(el, ctx, ct)));

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
            // 专家由用户配置决定, AI 不可指定人格/模板
            var personaText = AgentExecutor.ResolvePersonaText(
                agent, _personas, _templates, null, null,
                CommanderRuntime.Instance?.CurrentPersonaText, false,
                planMode: ctx.IsPlanMode);

            var args = JsonSerializer.SerializeToElement(new JsonObject());

            var result = await AgentExecutor.ExecuteAsync(
                agent, task ?? string.Empty, personaText,
                AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
                args, ctx, _assignments, _llm, ct);

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
        catch (Exception ex)
        {
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

public class GitStatusTool : ITool
{
    protected readonly GitStepService _git;

    public GitStatusTool(GitStepService git) => _git = git;

    public string Name => "git_status";

    public string Description => "查看 git 仓库状态: 分支/脏状态/最近提交/待合并步骤。只读。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        { "type": "object", "properties": {} }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (!_git.IsRepoAvailable)
        {
            return Task.FromResult(ToolResult.Ok("当前目录不是 git 仓库。"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"分支: {_git.CurrentBranch() ?? "?"}");
        sb.AppendLine($"工作区: {(_git.HasUncommittedChanges() ? "有未提交变更" : "干净")}");
        var last = _git.LastCommitShort();
        if (last is not null) sb.AppendLine($"最近提交: {last}");

        var pending = _git.PendingReview();
        if (pending.Count > 0)
        {
            sb.AppendLine("待处理步骤(可 merge/drop):");
            foreach (var step in pending)
            {
                sb.AppendLine($"- {step.StepId} [{step.Label}] 分支 {step.StepBranch}");
            }
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }
}

/// <summary>git_create_checkpoint: AI 主动打检查点(创建步骤分支), 后续可合并/丢弃/回滚。</summary>
public class GitCheckpointTool : ITool
{
    private readonly GitStepService _git;

    public GitCheckpointTool(GitStepService git) => _git = git;

    public string Name => "git_create_checkpoint";

    public string Description =>
        "在当前仓库打一个 git 检查点: 创建并切换到步骤分支, 用于在执行有风险改动前留存可回滚快照。" +
        "参数: label(可选, 检查点用途描述)。返回检查点 ID 与分支信息; " +
        "之后可用 git_merge_step 合并 / git_drop_step 丢弃, 也可对返回的检查点 ID 调用 git_revert_step。不改动工作区文件。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "label": { "type": "string", "description": "检查点用途描述(可选)" }
          }
        }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var label = args.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()?.Trim()
            : null;

        try
        {
            var step = _git.BeginStep(string.IsNullOrWhiteSpace(label) ? "ai-checkpoint" : label);
            Log.Info("Git", $"AI 创建检查点: {step.StepId} (分支 {step.StepBranch}, 基于 {step.BaseBranch}, 标签 {step.Label})");
            return Task.FromResult(new ToolResult
            {
                Content = $"检查点已创建: {step.StepId} [{step.Label}]\n" +
                          $"分支: {step.StepBranch} (基于 {step.BaseBranch})\n" +
                          "后续可用 git_merge_step 合并 / git_drop_step 丢弃 / git_revert_step 反做; UI 卡片支持一键回滚。",
                StepId = step.StepId
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"创建检查点失败: {ex.Message}"));
        }
    }
}

public abstract class GitStepTool : ITool
{
    protected readonly GitStepService _git;

    protected GitStepTool(GitStepService git, string name, string description, bool requiresApproval)
    {
        _git = git;
        Name = name;
        Description = description;
        RequiresApproval = requiresApproval;
    }

    public string Name { get; }

    public string Description { get; }

    public bool RequiresApproval { get; }

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        { "type": "object", "properties": { "stepId": { "type": "string" } }, "required": ["stepId"] }
        """);

    protected abstract GitCommandResult ExecuteAction(string stepId);

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var stepId = args.TryGetProperty("stepId", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(stepId))
        {
            return Task.FromResult(ToolResult.Error("缺少参数: stepId"));
        }

        try
        {
            var result = ExecuteAction(stepId);
            return Task.FromResult(result.Succeeded
                ? ToolResult.Ok(result.Stdout.Trim())
                : ToolResult.Error($"git 命令失败: {result.Stderr.Trim()}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
    }
}

public class GitMergeStepTool : GitStepTool
{
    public GitMergeStepTool(GitStepService git)
        : base(git, "git_merge_step", "把已完成步骤分支合并进主分支(--no-ff, 保留历史)。需批准。", true)
    {
    }

    protected override GitCommandResult ExecuteAction(string stepId) => _git.MergeStep(stepId);
}

public class GitDropStepTool : GitStepTool
{
    public GitDropStepTool(GitStepService git)
        : base(git, "git_drop_step", "删除步骤分支并整体丢弃该步骤(工作区需干净)。需批准。", true)
    {
    }

    protected override GitCommandResult ExecuteAction(string stepId) => _git.DropStep(stepId);
}

public class GitRevertStepTool : GitStepTool
{
    public GitRevertStepTool(GitStepService git)
        : base(git, "git_revert_step", "对已合并步骤生成反向提交, 保留历史。需批准。", true)
    {
    }

    protected override GitCommandResult ExecuteAction(string stepId) => _git.RevertStep(stepId);
}
