using System.Text;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;
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
        string rules, GitService? git = null, string? workDir = null,
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
        var gitText = BuildGitSection(git, workDir);

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

            // Plan 模式下仅列出被授权(开启 UseInPlanMode, 且有 plan_args 或开启无参数放行开关)的子代理,
            // 与工具注册过滤保持一致: AI 无法发现未授权的子代理
            if (planMode && !(entry.UseInPlanMode &&
                              (agent.PlanArgs is { Count: > 0 } || agent.AllowPlanModeWithoutArgs)))
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
            if (entry.UseInPlanMode &&
                (agent.PlanArgs is { Count: > 0 } || agent.AllowPlanModeWithoutArgs))
            {
                parts.Add("Plan 模式可用");
            }
            parts.Add($"模式: {agent.DefaultMode}");

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
            parts.Add($"模式: {agent.DefaultMode}");

            sb.AppendLine($"- run_{agent.Id}: {string.Join(" | ", parts)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建 {git} 段落。基于 <see cref="GitService"/> 的显式上下文 API,
    /// 需要调用方提供 <paramref name="workDir"/> 才能定位仓库。
    ///
    /// <para>保持轻量: 每回合调用一次, 只用 <c>IsClean</c> 与 <c>GetHeadSha</c>
    /// 两个只读命令(ResolveContext 内部的分支/空仓库探测除外), 不拉取提交图谱。</para>
    /// </summary>
    private static string BuildGitSection(GitService? git, string? workDir)
    {
        if (git is null || string.IsNullOrWhiteSpace(workDir))
        {
            return "(未确定工作目录, 无 git 信息)";
        }

        var ctx = git.ResolveContext(workDir);
        if (!ctx.IsValidRepo)
        {
            return "(非 git 仓库, 无检查点回滚保护; 建议先 git init)";
        }

        var branch = string.IsNullOrEmpty(ctx.BranchName) ? "(detached HEAD)" : ctx.BranchName;

        // 空仓库(unborn HEAD)没有 sha 可取, 跳过该次 git 调用
        var head = ctx.IsEmptyRepo ? null : git.GetHeadSha(ctx.RepositoryRoot);
        var shortSha = string.IsNullOrEmpty(head) ? "" : head[..Math.Min(7, head.Length)];

        var dirty = git.IsClean(ctx).Succeeded ? "干净" : "有未提交变更";

        return $"分支: {branch} | {dirty}" + (shortSha.Length > 0 ? $" | 最近提交: {shortSha}" : "");
    }

    /// <summary>读取用户自定义的 roster 模板; 缺失或损坏(可回退 .bak)时返回 null。</summary>
    private static string? LoadTemplate()
    {
        return AtomicFile.TryReadText(AppPaths.RosterTemplatePath, out var text)
            ? text
            : null;
    }

    /// <summary>首次启动时写入默认 roster 模板(原子写, 不覆盖用户已自定义的模板)。</summary>
    public static void WriteDefaultTemplate()
    {
        if (!File.Exists(AppPaths.RosterTemplatePath))
        {
            // AtomicFile 会自动创建父目录, 并把上一份内容留作 .bak
            AtomicFile.TryWriteAllText(AppPaths.RosterTemplatePath, DefaultTemplate, "roster.prompt");
        }
    }
}
