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

    /// <summary>
    /// 构建 Roster 注入文本。
    ///
    /// <para><paramref name="rosterEntries"/> 的语义(P0-2 修复后显式化, 判据只看 null 与否):
    /// <list type="bullet">
    /// <item><c>null</c> = 无会话 Roster 下发(会话从未被 GUI 推送过)→ 按全局配置列出全部
    /// <c>run_&lt;id&gt;</c>;</item>
    /// <item>非 null(含<em>空表</em>) = 以该表为准, 空表即"用户已清空全部子代理"→ agents 段落为空。</item>
    /// </list>
    /// 历史上用 <c>Count: &gt; 0</c> 同时表达"无限制"与"用户清空", 于是
    /// <c>SetSubagentToolsVisible(false)</c> 传入空表后反落到"列出全部 Agent"分支:
    /// 子代理工具已从 <c>ToolRegistry</c> 注销, system prompt 却仍向 AI 广告 run_&lt;id&gt;,
    /// AI 会持续尝试调用不存在的工具。</para>
    /// </summary>
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

        var agentsText = rosterEntries is null
            ? BuildAgentsSection(agents)
            : BuildAgentsSectionFromRoster(rosterEntries, agents, personas, planMode);

        // 显式占位: 空 agents 段落会让提示词只剩一个空标题, 且 AI 无从判断"是没配"还是"被禁用",
        // 仍可能去猜 run_<id> 名字。直接告知"没有可用子代理工具"以压住幻觉调用。
        if (rosterEntries is not null && agentsText.Length == 0)
        {
            agentsText = "(无: 当前会话没有可用的子代理工具, 不要尝试调用 run_*/assign_task/run_subagents)";
        }

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
            // 会话级描述默认就等于 agent.Description(GUI 的 PushRoster 会带上), 无条件再追加一次
            // 会让同一句话在提示词里出现两遍, 白占上下文。加条件去重。
            if (agent.Description.Length > 0 &&
                !string.Equals(agent.Description, entry.Description, StringComparison.Ordinal))
            {
                parts.Add(agent.Description);
            }
            if (entry.PersonaId is { Length: > 0 } && personaMap.TryGetValue(entry.PersonaId, out var persona))
            {
                parts.Add($"推荐专家: {persona.Display}");
            }
            if (entry.UseInPlanMode &&
                (agent.PlanArgs is { Count: > 0 } || agent.AllowPlanModeWithoutArgs))
            {
                parts.Add("Plan 模式可用");
            }
            // 不输出"模式: {default_mode}": AssignmentManager 只有 RunSyncAsync,
            // default_mode 的 async 分支没有任何实现, 写进提示词等于给 AI 一个选不了的选项
            // (P1-6)。配置项本身保留在 agents.toml, 由后续版本决定是实现还是移除。

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
            // 同 BuildAgentsSectionFromRoster: 省略"模式: {default_mode}"(P1-6, async 无实现)

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

    /// <summary>
    /// roster.prompt 的进程内缓存。Build 每回合调用, 原实现每回合一次读盘
    /// (且 TryReadText 在主文件解析失败时还会额外读 .bak)。roster.prompt 是用户可编辑文件,
    /// 故以 (mtime, 长度) 作失效判据: 任一变化即重新读盘, 无需调用方显式失效。
    ///
    /// <para>失效需外部干预的时机: 目前只有"还原默认设置"会删写 roster.prompt
    /// (CommanderRuntime.ResetSettingsToDefault → WriteDefaultTemplate), 已在写完后自行 ReloadTemplate。
    /// 若将来设置页新增"编辑 roster.prompt"的保存入口, 保存成功后必须调用 <see cref="ReloadTemplate"/>。</para>
    /// </summary>
    private static readonly object TemplateCacheLock = new();
    private static string? _cachedTemplate;
    private static long _cachedWriteTicks = -1;
    private static long _cachedLength = -1;

    /// <summary>失效 roster.prompt 内存缓存(文件写入方在写完后调用; 下次 Build 时重新读盘)。</summary>
    public static void ReloadTemplate()
    {
        lock (TemplateCacheLock)
        {
            _cachedTemplate = null;
            _cachedWriteTicks = -1;
            _cachedLength = -1;
        }
    }

    /// <summary>读取用户自定义的 roster 模板(带 mtime 缓存); 缺失或损坏(可回退 .bak)时返回 null。</summary>
    private static string? LoadTemplate()
    {
        var path = AppPaths.RosterTemplatePath;
        lock (TemplateCacheLock)
        {
            long writeTicks;
            long length;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _cachedTemplate = null;
                    _cachedWriteTicks = -1;
                    _cachedLength = -1;
                    return null;
                }
                writeTicks = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
            }
            catch
            {
                // 探测失败(如路径不可访问): 退化为继续用上次缓存, 避免每回合都读盘失败
                return _cachedTemplate;
            }

            if (_cachedWriteTicks == writeTicks && _cachedLength == length)
            {
                return _cachedTemplate;
            }

            var text = AtomicFile.TryReadText(path, out var content) ? content : null;
            _cachedTemplate = text;
            _cachedWriteTicks = writeTicks;
            _cachedLength = length;
            return text;
        }
    }

    /// <summary>首次启动时写入默认 roster 模板(原子写, 不覆盖用户已自定义的模板)。</summary>
    public static void WriteDefaultTemplate()
    {
        if (!File.Exists(AppPaths.RosterTemplatePath))
        {
            // AtomicFile 会自动创建父目录, 并把上一份内容留作 .bak
            AtomicFile.TryWriteAllText(AppPaths.RosterTemplatePath, DefaultTemplate, "roster.prompt");
        }

        // 本方法是 roster.prompt 唯一的写入口: 无论写没写都要失效缓存,
        // 否则"还原默认设置"后本回合仍注入旧模板内容
        ReloadTemplate();
    }
}
