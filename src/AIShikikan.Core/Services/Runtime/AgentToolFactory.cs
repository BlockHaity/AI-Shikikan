using System.Text;
using System.Text.Json;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Tools.Builtin;

namespace AIShikikan.Core.Services.Runtime;

/// <summary>构建 LLM 可见工具集: 只读工具 + run_&lt;agent&gt; + assign_task + assignment_* + git_* + persona_*。</summary>
public static class AgentToolFactory
{
    public static IReadOnlyList<ITool> Create(
        IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates,
        GitStepService git,
        AssignmentManager assignments)
    {
        var list = new List<ITool>
        {
            new ReadFileTool(),
            new GlobTool(),
            new GrepTool(),
            new ListDirectoryTool(),
            new AssignmentStatusTool(assignments),
            new AssignmentCancelTool(assignments),
            new GitStatusTool(git),
            new GitMergeStepTool(git),
            new GitDropStepTool(git),
            new GitRevertStepTool(git),
            new PersonaListTool(personas)
        };

        foreach (var agent in agents)
        {
            list.Add(new AgentExecutionTool(agent, personas, templates, git, assignments));
        }

        list.Add(new AssignTaskTool(agents, personas, templates, git, assignments));
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
        bool useCommanderPersona = false)
    {
        if (!string.IsNullOrWhiteSpace(personaId))
        {
            var persona = PersonaService.Find(personaId, personas);
            if (persona is not null)
            {
                return $"{persona.Display}\n{persona.SystemPrompt}";
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
                        return $"{persona.Display}\n{persona.SystemPrompt}";
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
                return $"{persona.Display}\n{persona.SystemPrompt}";
            }
        }

        return string.Empty;
    }

    public static string BuildFinalPrompt(string task, string personaText)
        => string.IsNullOrWhiteSpace(personaText)
            ? task
            : $"# 专家指令\n{personaText}\n\n# 任务\n{task}";

    public static string ResolveMode(CliAgentDefinition def, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return def.DefaultMode;
        }

        return string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase) ? "async" : "sync";
    }

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
        string mode,
        string workDirAbs,
        JsonElement args,
        ToolContext ctx,
        AssignmentManager assignments,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return ToolResult.Error("缺少参数: task");
        }

        var finalPrompt = BuildFinalPrompt(task, personaText);

        var memoryFilePath = (agent.Injection is InjectionMode.MemoryFile or InjectionMode.Both)
            && !string.IsNullOrEmpty(agent.MemoryFile)
            && personaText.Length > 0
                ? Path.Combine(workDirAbs, agent.MemoryFile!)
                : null;

        if (memoryFilePath is not null)
        {
            try
            {
                await File.WriteAllTextAsync(memoryFilePath, personaText, ct);
            }
            catch
            {
                memoryFilePath = null;
            }
        }

        var assignment = assignments.Create(agent, task,
            templateId: GetOpt(args, "templateId"),
            personaId: GetOpt(args, "personaId"),
            mode: mode,
            workingDirectory: workDirAbs);

        var progress = ctx.OnToolOutput is not null
            ? new Progress<string>(ctx.OnToolOutput)
            : null;

        if (mode == "sync")
        {
            try
            {
                var (completed, run) = await assignments.RunSyncAsync(assignment, finalPrompt, progress, ct);
                var tail = completed.OutputTail ?? run.Output;
                var header = $"[agent:{agent.Display}] 完成 (exit {run.ExitCode}, 耗时 {(int)run.Elapsed.TotalSeconds}s)";
                return ToolResult.Ok($"{header}\n\n{Truncate(tail, 26000)}");
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"[agent:{agent.Display}] 执行失败: {ex.Message}");
            }
        }

        assignments.StartAsync(assignment, finalPrompt, progress);
        return ToolResult.Ok(
            $"[异步] 已提交给 {agent.Display}: assignmentId = \"{assignment.AssignmentId}\"。" +
            "之后可调用 assignment_status 查询结果; 完成后注意检查 git 步骤。");
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

    public AgentExecutionTool(CliAgentDefinition agent, IReadOnlyList<Persona> personas,
        IReadOnlyList<AgentTemplate> templates, GitStepService git, AssignmentManager assignments)
    {
        _agent = agent;
        _personas = personas;
        _templates = templates;
        _git = git;
        _assignments = assignments;
    }

    public string Name => $"run_{_agent.Id}";

    public string Description => $"调用命令行 Agent「{_agent.Display}」执行子任务。{_agent.Description} " +
        $"专长: {string.Join("/", _agent.Expertise)}。mode: \"sync\"(等待完成返回结果) | \"async\"(后台运行, 之后用 assignment_status 查询)。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "子任务描述" },
            "personaId": { "type": "string", "description": "专家persona ID(可选)" },
            "templateId": { "type": "string", "description": "专家模板ID(可选, 如 frontend-dev)" },
            "mode": { "type": "string", "enum": ["sync", "async"], "description": "执行模式, 默认按Agent配置" },
            "workingDirectory": { "type": "string", "description": "运行目录(可选)" }
          },
          "required": ["task"]
        }
        """);

    public bool RequiresApproval => _agent.RequireApproval;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var task = Get(args, "task");
        var personaId = Get(args, "personaId");
        var templateId = Get(args, "templateId");
        var mode = Get(args, "mode") ?? _agent.DefaultMode;
        var workDir = Get(args, "workingDirectory");

        var personaText = AgentExecutor.ResolvePersonaText(
            _agent, _personas, _templates, personaId, templateId,
            CommanderRuntime.Instance?.CurrentPersonaText,
            false);
        return AgentExecutor.ExecuteAsync(
            _agent, task ?? string.Empty, personaText,
            AgentExecutor.ResolveMode(_agent, mode),
            AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
            args, ctx, _assignments, ct);
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

    public AssignTaskTool(IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas, IReadOnlyList<AgentTemplate> templates,
        GitStepService git, AssignmentManager assignments)
    {
        _agents = agents;
        _personas = personas;
        _templates = templates;
        _git = git;
        _assignments = assignments;
    }

    public string Name => "assign_task";

    public string Description => "把任务分派给最合适的 Agent: 可显式指定 agentId / templateId / personaId(专家), " +
        "未指定时按任务内容与 Agent 专长自动路由并自动附带推荐专家。返回 assignmentId。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "要执行的任务" },
            "agentId": { "type": "string", "description": "目标Agent ID(可选, 不填自动匹配)" },
            "templateId": { "type": "string", "description": "专家模板ID(可选, 如 frontend-dev)" },
            "personaId": { "type": "string", "description": "专家persona ID(可选)" },
            "mode": { "type": "string", "enum": ["sync", "async"], "description": "执行模式" },
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
        var templateId = Get(args, "templateId");
        var personaId = Get(args, "personaId");
        var mode = Get(args, "mode");
        var workDir = Get(args, "workingDirectory");

        var agent = ResolveAgent(agentId, templateId, task ?? string.Empty);
        if (agent is null)
        {
            return Task.FromResult(ToolResult.Error(
                $"找不到可用 Agent。已配置: {string.Join(", ", _agents.Select(a => a.Id))}"));
        }

        var personaText = AgentExecutor.ResolvePersonaText(
            agent, _personas, _templates, personaId, templateId,
            CommanderRuntime.Instance?.CurrentPersonaText,
            false);
        return AgentExecutor.ExecuteAsync(
            agent, task ?? string.Empty, personaText,
            AgentExecutor.ResolveMode(agent, mode),
            AgentExecutor.ResolveWorkingDir(workDir, ctx.WorkspaceRoot),
            args, ctx, _assignments, ct);
    }

    private CliAgentDefinition? ResolveAgent(string? agentId, string? templateId, string task)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            return AgentConfigService.Find(agentId, _agents);
        }

        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var template = AgentTemplateService.Find(templateId, _templates);
            if (template?.DefaultAgentId is { Length: > 0 })
            {
                return AgentConfigService.Find(template.DefaultAgentId, _agents);
            }
        }

        CliAgentDefinition? best = null;
        var bestScore = 0;
        foreach (var agent in _agents)
        {
            var score = agent.Expertise.Count(k => task.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore)
            {
                best = agent;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : _agents.FirstOrDefault();
    }

    private static string? Get(JsonElement args, string name)
        => args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

public class AssignmentStatusTool : ITool
{
    private readonly AssignmentManager _assignments;

    public AssignmentStatusTool(AssignmentManager assignments) => _assignments = assignments;

    public string Name => "assignment_status";

    public string Description => "查询子代理任务状态: 传 assignmentId 查单个, 不传列出全部。只读。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        { "type": "object", "properties": { "assignmentId": { "type": "string" } } }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var id = args.TryGetProperty("assignmentId", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;

        if (!string.IsNullOrWhiteSpace(id))
        {
            var assignment = _assignments.Get(id);
            if (assignment is null)
            {
                return Task.FromResult(ToolResult.Error($"任务不存在: {id}"));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"任务: {assignment.AssignmentId} | {assignment.AgentName} | {assignment.Mode}");
            sb.AppendLine($"状态: {assignment.Status}");
            sb.AppendLine($"任务描述: {assignment.Task}");
            if (assignment.StepId.Length > 0) sb.AppendLine($"git 步骤: {assignment.StepId}");
            if (assignment.ExitCode is { } code) sb.AppendLine($"退出码: {code}");
            if (!string.IsNullOrEmpty(assignment.Error)) sb.AppendLine($"错误: {assignment.Error}");
            if (!string.IsNullOrEmpty(assignment.OutputTail))
            {
                sb.AppendLine("--- 输出 ---");
                sb.AppendLine(assignment.OutputTail);
            }

            return Task.FromResult(ToolResult.Ok(sb.ToString()));
        }

        var all = _assignments.All.Take(20).ToList();
        if (all.Count == 0)
        {
            return Task.FromResult(ToolResult.Ok("(暂无分派任务)"));
        }

        var sbAll = new StringBuilder("最近分派任务:\n");
        foreach (var item in all)
        {
            sbAll.AppendLine($"{item.AssignmentId} {item.Status,-10} [{item.AgentName}] {item.ShortTask}");
        }

        return Task.FromResult(ToolResult.Ok(sbAll.ToString()));
    }
}

public class AssignmentCancelTool : ITool
{
    private readonly AssignmentManager _assignments;

    public AssignmentCancelTool(AssignmentManager assignments) => _assignments = assignments;

    public string Name => "assignment_cancel";

    public string Description => "取消进行中的子代理任务(终止后台进程)。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": { "assignmentId": { "type": "string" } },
          "required": ["assignmentId"]
        }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var id = args.TryGetProperty("assignmentId", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult(ToolResult.Error("缺少参数: assignmentId"));
        }

        _assignments.Cancel(id);
        return Task.FromResult(ToolResult.Ok($"已请求取消任务: {id}"));
    }
}

public class PersonaListTool : ITool
{
    private readonly IReadOnlyList<Persona> _personas;

    public PersonaListTool(IReadOnlyList<Persona> personas) => _personas = personas;

    public string Name => "persona_list";

    public string Description => "列出可用专家/角色扮演人格(ID+说明), 供分配专家使用。只读。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        { "type": "object", "properties": {} }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        if (_personas.Count == 0)
        {
            return Task.FromResult(ToolResult.Ok("暂无可用人格"));
        }

        var sb = new StringBuilder();
        foreach (var p in _personas)
        {
            var desc = p.Description.Length <= 70 ? p.Description : p.Description[..70] + "...";
            sb.AppendLine($"- {p.Name} ({p.Id}, {p.Kind}) - {desc}");
        }

        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }
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
