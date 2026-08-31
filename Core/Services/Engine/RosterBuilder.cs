using System.Text;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Templates;

namespace AIShikikan.Core.Services.Engine;

/// <summary>构建 Agent Roster 注入文本。模板化, {agents}/{personas}/{templates}/{rules}/{git} 占位符可自定义。</summary>
public static class RosterBuilder
{
    private const string DefaultTemplate = """
        # 可用 Agent(Roster)
        以下 CLI Agent 可通过工具 run_&lt;agent&gt; / assign_task 调用(子代理)。选择依据: 任务类型匹配其专长。

        {agents}

        # 可用专家(人格)
        {personas}

        # 专家档案模板
        {templates}

        # 分派规则
        {rules}

        # 当前仓库
        {git}
        """;

    public static string Build(IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas, IReadOnlyList<AgentTemplate> templates,
        string rules, GitStepService? git = null,
        IReadOnlyList<AgentRosterEntry>? rosterEntries = null,
        bool enabled = true,
        bool planMode = false)
    {
        if (!enabled)
        {
            return string.Empty;
        }

        var template = LoadTemplate() ?? DefaultTemplate;

        var agentsText = rosterEntries is { Count: > 0 }
            ? BuildAgentsSectionFromRoster(rosterEntries, agents, personas, planMode)
            : BuildAgentsSection(agents);
        var personasText = personas.Count == 0
            ? "(无)"
            : string.Join("\n", personas.Select(p =>
                $"- {p.Name} ({p.Kind}, {p.Description})"));
        var templatesText = templates.Count == 0
            ? "(无)"
            : string.Join("\n", templates.Select(t =>
                $"- {t.Name} [{t.Id}]: 专长 {string.Join("/", t.Expertise.Take(8))}(默认Agent: {t.DefaultAgentId ?? "-"})"));
        var rulesText = string.IsNullOrWhiteSpace(rules)
            ? "- 默认按任务专长匹配 Agent; 需要专家时从上方专家中选择并指派"
            : rules;
        var gitText = BuildGitSection(git);

        return template
            .Replace("{agents}", agentsText)
            .Replace("{personas}", personasText)
            .Replace("{templates}", templatesText)
            .Replace("{rules}", rulesText)
            .Replace("{git}", gitText);
    }

    private static string BuildAgentsSectionFromRoster(
        IReadOnlyList<AgentRosterEntry> rosterEntries,
        IReadOnlyList<CliAgentDefinition> agents,
        IReadOnlyList<Persona> personas,
        bool planMode)
    {
        var sb = new StringBuilder();
        var agentMap = new Dictionary<string, CliAgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents) agentMap[a.Id] = a;

        var personaMap = new Dictionary<string, Persona>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in personas) personaMap[p.Id] = p;

        foreach (var entry in rosterEntries.Where(e => e.Enabled))
        {
            if (!agentMap.TryGetValue(entry.AgentId, out var agent)) continue;

            // Plan 模式下仅列出配置了 plan_args 且开启"在 Plan 模式中使用"的子代理,
            // 与工具注册过滤保持一致: AI 无法发现未授权的子代理
            if (planMode && !(agent.PlanArgs is { Count: > 0 } && entry.UseInPlanMode))
            {
                continue;
            }

            var parts = new List<string> { entry.DisplayText };
            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                parts.Add(entry.Description);
            }
            if (agent.Description.Length > 0) parts.Add(agent.Description);
            if (entry.PersonaId is { Length: > 0 } && personaMap.TryGetValue(entry.PersonaId, out var persona))
            {
                parts.Add($"推荐专家: {persona.Display}");
            }
            if (entry.UseInPlanMode && agent.PlanArgs is { Count: > 0 })
            {
                parts.Add("Plan 模式可用");
            }
            parts.Add($"模式: {agent.DefaultMode}, 并发: {agent.MaxConcurrent}");

            sb.AppendLine($"- run_{agent.Id}: {string.Join(" | ", parts)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildAgentsSection(IReadOnlyList<CliAgentDefinition> agents)
    {
        var sb = new StringBuilder();
        foreach (var agent in agents)
        {
            if (!string.IsNullOrWhiteSpace(agent.RosterEntry))
            {
                sb.AppendLine(agent.RosterEntry);
                continue;
            }

            var parts = new List<string> { agent.Id };
            if (agent.Description.Length > 0) parts.Add(agent.Description);
            if (agent.RecommendedPersonaId is { Length: > 0 }) parts.Add($"推荐专家: {agent.RecommendedPersonaId}");
            if (agent.DefaultTemplateId is { Length: > 0 }) parts.Add($"推荐模板: {agent.DefaultTemplateId}");
            parts.Add($"模式: {agent.DefaultMode}, 并发: {agent.MaxConcurrent}");

            sb.AppendLine($"- run_{agent.Id}: {string.Join(" | ", parts)}");
        }

        return sb.ToString();
    }

    private static string BuildGitSection(GitStepService? git)
    {
        if (git is null || !git.IsRepoAvailable)
        {
            return "(非 git 仓库, 无回滚保护; 建议先 git init)";
        }

        var branch = git.CurrentBranch() ?? "?";
        var pending = git.PendingReview().ToList();
        var last = git.LastCommitShort() ?? "";
        var dirty = git.HasUncommittedChanges() ? "有未提交变更" : "干净";

        return $"分支: {branch} | {dirty} | 最近提交: {last}" +
               (pending.Count > 0 ? $"\n待合并步骤: {string.Join(", ", pending.Select(p => $"{p.StepId}({p.Label})"))} " : "");
    }

    private static string? LoadTemplate()
    {
        try
        {
            return File.Exists(AppPaths.RosterTemplatePath)
                ? File.ReadAllText(AppPaths.RosterTemplatePath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteDefaultTemplate()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        if (!File.Exists(AppPaths.RosterTemplatePath))
        {
            File.WriteAllText(AppPaths.RosterTemplatePath, DefaultTemplate);
        }
    }
}
