using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
using AIShikikan.Core.Serialization;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Mcp;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using AIShikikan.Core.Services.Session;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Core.Services;

/// <summary>应用启动外观: 初始化配置目录、示例文件、所有服务与工具集, 供 CLI / GUI 复用。</summary>
public sealed class CommanderRuntime
{
    public static CommanderRuntime Instance { get; private set; } = null!;

    public required string WorkspaceRoot { get; init; }
    public required LlmService Llm { get; init; }

    /// <summary>显式工作区上下文的 Commit/tag 检查点服务。</summary>
    public required GitService GitService { get; init; }

    public required GitCheckpointStore Checkpoints { get; init; }
    public required AssignmentManager Assignments { get; init; }
    public required ToolRegistry Registry { get; init; }
    public required SessionRuntimeRegistry Sessions { get; init; }
    public required WorkspaceExecutionCoordinator Coordinator { get; init; }
    public required McpService Mcp { get; init; }
    public required IReadOnlyList<Persona> Personas { get; init; }
    public required IReadOnlyList<AgentTemplate> Templates { get; init; }
    public required IReadOnlyList<CliAgentDefinition> Agents { get; init; }

    /// <summary>当前活动会话引擎(兼容访问: GUI 现有调用点等价于 Sessions.ActiveEngine)。</summary>
    public AgentEngine Engine => Sessions.ActiveEngine;

    public string? CurrentPersonaText { get; private set; }

    /// <summary>当前指挥官人格(按 Plan/Build 模式解析出生效提示词)。</summary>
    private Persona? _commanderPersona;

    /// <summary>按当前模式重新解析指挥官人格提示词并同步到全部会话引擎。</summary>
    private void RefreshCommanderPersonaText()
    {
        var text = _commanderPersona?.ResolveForMode(_isPlanMode);
        CurrentPersonaText = string.IsNullOrWhiteSpace(text) ? null : text;
        ApplyPersonaTextToSessions();
    }

    /// <summary>指挥官人格是全局设置: 同步到所有已创建的会话引擎(下一回合生效)。</summary>
    private void ApplyPersonaTextToSessions()
    {
        foreach (var rt in Sessions.All)
        {
            rt.Engine.SetPersonaText(CurrentPersonaText);
        }
    }

    /// <summary>当前活动会话的 Roster 快照(未选定会话时为空列表)。</summary>
    ///
    /// <para><b>已知语义缺陷(未修, 需产品决策)</b>: 本属性读的是「活动会话」, 因此
    /// 后台会话 B 的子代理在用户切到会话 A 后, 会读到 A 的压缩开关与 Plan 授权,
    /// 即 Plan 授权可跨会话串味(既会误拒也会误放)。根因是 ToolContext 里没有会话身份,
    /// 取不到「本次工具调用属于哪个会话」。修复路径: 子代理工具改用
    /// <see cref="GetRosterEntriesFor"/> 按 <c>ToolContext.SessionId</c> 取,
    /// 不再经过本属性(AgentToolFactory 的 IsCompactEnabled / ShouldRunInPlanMode 需配合改造)。</para>
    ///
    /// <para><b>另一处待决策</b>: 未选定会话(Active 为 null)时本属性返回空表,
    /// 于是 <see cref="IsAgentRegisteredInPlanMode"/> 把所有 Agent 判为未授权,
    /// Plan 模式下会导致子代理工具一个都不注册。当前保持该行为不变。</para>
    public IReadOnlyList<AgentRosterEntry> CurrentRosterEntries =>
        Sessions.Active?.Engine.RosterEntries ?? [];

    /// <summary>
    /// 按会话 ID 取该会话引擎的 Roster 快照; 会话运行时未加载或从未被 GUI 推送过时返回空表。
    ///
    /// <para>供子代理工具按"本次调用归属的会话"解析会话级开关(压缩 / Plan 授权)使用,
    /// 以避免 <see cref="CurrentRosterEntries"/> 读活动会话造成的跨会话串味。</para>
    ///
    /// <para>刻意返回空表而非 null: 空表与 null 在下游语义一致(都表示"没有会话级授权/配置"),
    /// 而 <c>RosterBuilder</c> 的"null = 无限制"语义不应泄漏到工具侧, 否则会退化成无限制。</para>
    /// </summary>
    public IReadOnlyList<AgentRosterEntry> GetRosterEntriesFor(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return [];
        return Sessions.TryGet(sessionId)?.Engine.RosterEntries ?? [];
    }

    public static CommanderRuntime Boot(string? workspaceRoot = null, string? personaId = null)
    {
        AppPaths.EnsureDirectoriesExist();

        PersonaService.WriteSampleFiles();
        AgentTemplateService.EnsureSamplesExist();
        RosterBuilder.WriteDefaultTemplate();
        AgentConfigService.EnsureDefaultExists();
        ProviderSettingsService.EnsureDefaultExists();
        McpConfigService.EnsureDefaultExists();
        AgentConfigService.Refresh();

        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceRoot) ? "." : workspaceRoot);

        var personas = PersonaService.LoadAll();
        var personasList = personas.ToList();
        var templates = AgentTemplateService.LoadAll();
        var templatesList = templates.ToList();
        var agents = AgentConfigService.LoadAll();
        var agentsList = agents.ToList();

        var llm = new LlmService();
        var checkpoints = new GitCheckpointStore();
        var gitService = new GitService(checkpoints);
        // 记录被淘汰时同步回收对应 tag, 否则 tag 会一直残留, 被 doctor 的遗留产物扫描
        // 反复报成"可清理"(回滚只用 CommitSha, 不用 TagName, 所以删 tag 不影响回滚能力,
        // 反而让失去记录引用的旧 commit 可被 git gc 回收)
        checkpoints.TagDeleter = (repoRoot, tag) => gitService.DeleteCheckpointTag(repoRoot, tag);
        var assignments = new AssignmentManager();
        var mcp = new McpService();

        // 工作区执行协调器 + 会话事件 Hub: 同 worktree 同分支多会话并发, 跨分支互斥
        var coordinator = new WorkspaceExecutionCoordinator(new GitWorkspaceResolver(root));
        var hub = new EngineEventHub();
        CommanderRuntime? self = null;
        var sessions = new SessionRuntimeRegistry(hub, coordinator,
            (sessionId, sessionTitle, host) => self is null
                ? throw new InvalidOperationException("会话引擎工厂在运行时装配前被调用")
                : self.CreateEngine(sessionId, sessionTitle, host, hub));

        // 子 Agent 终态时记录调用统计(Completed 视为成功)
        assignments.AssignmentChanged += a =>
        {
            if (a.Status is SubagentStatus.Completed or SubagentStatus.Failed
                or SubagentStatus.Cancelled or SubagentStatus.TimedOut)
            {
                UsageStatsService.RecordAgentCall(a.AgentId, a.AgentName,
                    a.Status == SubagentStatus.Completed);
            }
        };

        var registry = new ToolRegistry();
        foreach (var tool in AgentToolFactory.CreateCoreTools(gitService))
        {
            registry.Register(tool);
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(agentsList, personasList, templatesList, gitService, assignments, llm))
        {
            registry.Register(tool);
        }

        var persona = personasList.FirstOrDefault(p =>
            string.Equals(p.Id, personaId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, personaId, StringComparison.OrdinalIgnoreCase))
            ?? personasList.FirstOrDefault();
        // 指挥官人格提示词按当前模式(默认 Build)解析: 通用正文 + 模式专属段
        var personaText = string.IsNullOrWhiteSpace(persona?.ResolveForMode(false))
            ? null
            : persona!.ResolveForMode(false);

        self = Instance = new CommanderRuntime
        {
            WorkspaceRoot = root,
            Llm = llm,
            GitService = gitService,
            Checkpoints = checkpoints,
            Assignments = assignments,
            Registry = registry,
            Sessions = sessions,
            Coordinator = coordinator,
            Mcp = mcp,
            Personas = personasList,
            Templates = templatesList,
            Agents = agentsList,
            CurrentPersonaText = personaText
        };
        Instance._commanderPersona = persona;

        // 分派状态经 Hub 广播一次(替代旧的每引擎订阅, 避免多会话重复触发)
        assignments.AssignmentChanged += a => hub.Publish(new EngineAssignmentChanged(a));

        // 后台连接 MCP 服务器并注册桥接工具(不阻塞启动)
        var runtime = Instance;
        _ = Task.Run(async () => await runtime.RefreshMcpToolsAsync().ConfigureAwait(false));

        Log.Info("Boot",
            $"装配完成: 工作区={Path.GetFullPath(root)}, Agent={agentsList.Count}, 专家={personasList.Count}, " +
            $"模板={templatesList.Count}, 工具={registry.All.Count}, 人格字符数={personaText?.Length ?? 0}");

        return Instance;
    }

    /// <summary>创建会话引擎(注册表工厂回调): 新会话继承当前 Plan 模式与全局人格设置;
    /// Roster 由本会话自己的 roster.json 合成, 不跨会话继承。</summary>
    ///
    /// <para><b>为什么要在引擎构造时就带上 roster</b>: 原实现不传, 引擎的 RosterEntries 为 null,
    /// <c>RosterBuilder</c> 把 null 当"无限制"从而向 AI 广告全部 run_&lt;id&gt;。
    /// GUI 的 AgentPanel 稍后才 PushRoster, 在此之前的第一个回合(或后台会话)就会
    /// 把用户在 AgentPanel 里关掉的子代理照旧广告出去, AI 持续尝试调用未注册的工具。</para>
    ///
    /// <para><b>不跨会话继承的决定与理由</b>: roster 是会话级持久状态(存在
    /// sessions/{id}/roster.json, 由用户在该会话的 AgentPanel 开关写盘)。若新会话继承
    /// 上一个会话的 roster, 会出现"磁盘上属于本会话的 roster 与引擎快照不一致"的瞬态:
    /// 工具注册过滤用 A 的条目、提示词用 B 的条目, 且一旦 PushRoster 落地就被覆盖回去。</para>
    private AgentEngine CreateEngine(string sessionId, string sessionTitle,
        ISessionEngineHost host, EngineEventHub hub)
        => new AgentEngine(
            llm: Llm,
            registry: Registry,
            git: GitService,
            assignments: Assignments,
            personas: Personas,
            templates: Templates,
            agents: Agents,
            workspaceRoot: WorkspaceRoot,
            personaText: CurrentPersonaText,
            options: new EngineOptions { IsPlanMode = _isPlanMode },
            rosterEntries: BuildInitialRosterEntries(sessionId),
            sessionId: sessionId,
            sessionTitle: sessionTitle,
            host: host,
            hub: hub);

    /// <summary>
    /// 合成该会话的初始 Roster 快照: 全局 Agent 列表 + 该会话 roster.json 逐项覆盖,
    /// 语义与 AgentPanel 的 PushRoster 对齐(默认全部启用, 会话条目覆盖
    /// Enabled / 描述 / 专家 / 压缩 / Plan 授权)。
    ///
    /// <para><b>必须"合成完整表"而不能直接用 roster.json 的条目</b>: AgentPanel 的开关只在用户
    /// 实际改动某一项时才写 roster.json, 因此文件里通常只有被改动过的少数几个 Agent。
    /// 直接把这份残表当快照, RosterBuilder 会只列出这几个而把其余可用 Agent 全部从提示词里抹掉。</para>
    ///
    /// <para>roster.json 从不存在时返回 null(= "无限制", 不臆造): 全新会话没有任何会话级记录,
    /// 由 GUI 首次 PushRoster 收敛到用户的实际开关状态。</para>
    /// </summary>
    private IReadOnlyList<AgentRosterEntry>? BuildInitialRosterEntries(string sessionId)
    {
        var overrides = RosterConfigService.LoadEntriesOrNull(sessionId);
        if (overrides is null)
        {
            return null;
        }

        // 用 Agents(而非重新 LoadAll): 它正是本引擎解析子代理所用的那份定义列表
        var result = new List<AgentRosterEntry>(Agents.Count);
        foreach (var agent in Agents)
        {
            var ov = overrides.FirstOrDefault(e =>
                string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase));

            result.Add(new AgentRosterEntry
            {
                AgentId = agent.Id,
                // Display 为空表示沿用 Agent 自带名(DisplayText 会回退)
                Display = string.IsNullOrEmpty(ov?.Display) ? agent.Display : ov!.Display,
                // 描述只取会话覆盖值: 为空时 RosterBuilder 会补上 agent.Description, 避免同一句出现两次
                Description = ov?.Description ?? string.Empty,
                // 与 AgentPanel 一致: 会话条目的专家为空视为"沿用推荐专家"
                PersonaId = string.IsNullOrWhiteSpace(ov?.PersonaId) ? agent.RecommendedPersonaId : ov!.PersonaId,
                Enabled = ov?.Enabled ?? true,
                CompactEnabled = ov?.CompactEnabled ?? false,
                UseInPlanMode = ov?.UseInPlanMode ?? false
            });
        }

        return result;
    }

    /// <summary>重建 MCP 桥接工具: 断开旧连接, 连接全部启用服务器并注册 mcp_* 工具。
    /// 返回状态消息列表(含连接失败说明), UI 可展示。</summary>
    public async Task<List<string>> RefreshMcpToolsAsync(CancellationToken ct = default)
    {
        var messages = new List<string>();

        await Mcp.DisposeAsync().ConfigureAwait(false);
        Registry.UnregisterWhere(t => t is McpProxyTool);

        messages.AddRange(await Mcp.ConnectAllAsync(ct).ConfigureAwait(false));

        foreach (var (_, serverId, descriptor) in Mcp.EnumerateTools())
        {
            Registry.Register(new McpProxyTool(Mcp, serverId, descriptor));
        }

        foreach (var m in messages.Where(m => !m.Contains("连接失败")))
        {
            Log.Info("MCP", m);
        }

        foreach (var m in messages.Where(m => m.Contains("连接失败")))
        {
            Log.Warn("MCP", m);
        }

        messages.Add($"[MCP] 已连接 {Mcp.ConnectedCount} 个服务器, 注册 {Registry.All.Count(t => t is McpProxyTool)} 个工具");
        return messages;
    }

    /// <summary>直接指定指挥官人格提示词(覆盖按模式解析的结果); 传 null 清除。</summary>
    public void SetPersonaText(string? text)
    {
        _commanderPersona = null; // 避免后续模式切换时用 persona 解析结果覆盖显式设定
        CurrentPersonaText = text;
        ApplyPersonaTextToSessions(); // 全局设置同步到所有会话
    }

    /// <summary>指定指挥官人格并按当前模式解析生效提示词(通用正文 + Plan/Build 专属段)。</summary>
    public void SetCommanderPersona(Persona? persona)
    {
        _commanderPersona = persona;
        RefreshCommanderPersonaText();
    }

    private bool _subagentToolsVisible = true;

    private bool _isPlanMode;

    /// <summary>当前是否处于 Plan 模式(由聊天页切换, 联动子代理工具注册与 Roster 注入过滤)。</summary>
    public bool IsPlanMode => _isPlanMode;

    /// <summary>切换 Plan 模式: 重建子代理工具注册(Plan 模式下仅保留开启"在 Plan 模式中使用"
    /// 且配置了 plan_args 的 Agent), 下一回合 AI 工具列表即不再包含未授权子代理。</summary>
    public void SetPlanMode(bool planMode)
    {
        if (_isPlanMode == planMode) return;
        _isPlanMode = planMode;

        // Plan 模式是全局开关: 子代理工具注册是全局的(ToolRegistry 单例), 若只把 IsPlanMode
        // 写进活动会话引擎, 后台会话的 Options.IsPlanMode 会停在上一次取值。切回 Build 后
        // 后台会话仍按 Plan 过滤 Roster(少列子代理)且执行兜底拒绝合法调用, 工具注册与
        // 提示词互相打架。与 ApplyPersonaTextToSessions 同一做法: 同步到全部已存在引擎。
        ApplyPlanModeToSessions();

        // 指挥官人格按新模式重新解析提示词(通用正文 + 对应模式专属段)
        RefreshCommanderPersonaText();
        RebuildSubagentTools();
        Log.Info("Engine", $"Plan 模式切换为 {planMode}, 当前工具数={Registry.All.Count}");
    }

    /// <summary>Plan 模式是全局设置: 同步到所有已创建的会话引擎(下一回合生效)。</summary>
    private void ApplyPlanModeToSessions()
    {
        foreach (var rt in Sessions.All)
        {
            rt.Engine.Options.IsPlanMode = _isPlanMode;
        }
    }

    /// <summary>
    /// Plan 模式授权的唯一权威判据(G5: 原先存在两份语义不同的实现)。
    ///
    /// <para>授权需要同时满足两个条件:
    /// (1) Agent 侧支持 Plan 模式 —— 配置了 plan_args, 或开启"无 plan 参数也可在 Plan 模式使用";
    /// (2) 会话侧授权 —— 该 Agent 在本会话 Roster 里开启了 UseInPlanMode。
    /// 两者与 <c>RosterBuilder</c> 的 Plan 过滤、以及工具注册过滤保持同一判据。</para>
    ///
    /// <para><paramref name="rosterEntries"/> 为 null 或空表都表示"无人授权"
    /// (前者是没下发过, 后者是用户清空)。要否追加 plan_args 由调用方再与
    /// "主对话确实处于 Plan 模式"相与(AgentToolFactory.ShouldRunInPlanMode)。</para>
    /// </summary>
    public static bool IsAgentPlanModeAuthorized(CliAgentDefinition agent,
        IReadOnlyList<AgentRosterEntry>? rosterEntries)
    {
        // 条件 (1): Agent 侧支持 Plan 模式
        if (agent.PlanArgs is not { Count: > 0 } && !agent.AllowPlanModeWithoutArgs)
        {
            return false;
        }

        // 条件 (2): 会话侧授权
        return rosterEntries is not null && rosterEntries.Any(e =>
            string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase) && e.UseInPlanMode);
    }

    /// <summary>Plan 模式下是否**注册/放行**该子代理(按<b>活动会话</b>的 Roster 判定;
    /// 未选定会话时无人授权 → 一律 false, 见 <see cref="CurrentRosterEntries"/> 的说明)。
    /// 工具注册过滤与执行兜底共用此判据。</summary>
    public bool IsAgentRegisteredInPlanMode(CliAgentDefinition agent)
        => IsAgentPlanModeAuthorized(agent, CurrentRosterEntries);

    /// <summary><see cref="IsAgentRegisteredInPlanMode"/> 的同义别名(执行兜底调用点沿用旧名)。</summary>
    public bool IsAgentAllowedInPlanMode(CliAgentDefinition agent)
        => IsAgentRegisteredInPlanMode(agent);

    /// <summary>按右侧栏可见性与 Plan 模式重建子代理工具注册。</summary>
    private void RebuildSubagentTools()
    {
        Registry.UnregisterWhere(t =>
            t is AgentExecutionTool or AssignTaskTool or SubagentGroupTool);

        if (!_subagentToolsVisible) return;

        // 从配置服务重新加载, 保证增删后的 Agent/专家/模板即时生效
        var agents = AgentConfigService.LoadAll();
        var personas = PersonaService.LoadAll();
        var templates = AgentTemplateService.LoadAll();

        if (_isPlanMode)
        {
            agents = agents.Where(IsAgentRegisteredInPlanMode).ToList();
            if (agents.Count == 0)
            {
                // 【待产品决策】Plan 模式 + 零授权 ⇒ 子代理工具(含 assign_task / run_subagents)全部不注册,
                // AI 在 Plan 模式下完全无法分派任何子代理。当前**保持此行为不变**:
                // 它的好处是 Plan 模式严格"只规划不执行", 代价是用户即使授权为 0 也拿不到
                // "让子代理帮忙出方案"的入口。另一种可选语义是仍注册 assign_task / run_subagents
                // (让 AI 能编排), 但在执行兜底处拒绝未授权 Agent —— 那会把"Plan 模式"从
                // "模式"变成"部分禁用", 语义更模糊, 因此暂不改。
                Log.Info("Engine", "Plan 模式: 无授权子代理, 子代理工具全部移除");
                return;
            }
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(
                     agents, personas, templates, GitService, Assignments, Llm))
        {
            Registry.Register(tool);
        }
    }

    /// <summary>右侧栏可见性联动: 关闭时从注册表移除子代理工具(AI 下一回合不再可见), 打开时重新注册。
    /// Roster 注入由 GUI 在打开后调用 SetRosterEntries 恢复。</summary>
    ///
    /// <para>关闭时必须往活动会话引擎写<b>空表</b>(而非置 null): RosterBuilder 的 null 语义是
    /// "无限制"、空表语义是"用户清空", 写 null 会让 prompt 反过来广告全部 run_&lt;id&gt;,
    /// 而此时工具已注销 —— AI 只会反复调用不存在的工具。重新打开时 GUI 的 PushRoster 写回真实条目。</para>
    ///
    /// <para>仅作用于活动会话: 后台会话的 Roster 若一并清空, 重新打开右侧栏时只有活动会话会被
    /// PushRoster 恢复, 其余会话的条目会永久丢失, 故保持会话级隔离。</para>
    public void SetSubagentToolsVisible(bool visible)
    {
        if (_subagentToolsVisible == visible) return;
        _subagentToolsVisible = visible;

        if (!visible)
        {
            SetRosterEntries([]);
        }

        RebuildSubagentTools();
        Log.Info("Engine", $"子代理工具{(visible ? "已注册" : "已移除")}, 当前工具数={Registry.All.Count}");
    }

    /// <summary>一键还原默认设置: 删除 providers/agents/mcp-servers/roster.prompt(含旧 JSON 兼容文件)后
    /// 按启动流程重新生成默认, 并让运行时即时生效(LLM client 缓存清空)。返回各步骤状态说明(失败带 ⚠)。
    /// 界面偏好(preferences.toml)由 GUI 层的 ThemeService.ResetToDefault 负责; 人格/模板示例与会话/统计数据不受影响。</summary>
    public List<string> ResetSettingsToDefault()
    {
        var messages = new List<string>();

        // LLM Provider: 删除后重新生成, 并替换 Settings 触发 _clients 缓存清空
        DeleteIfExists(AppPaths.ProvidersPath);
        DeleteIfExists(Path.ChangeExtension(AppPaths.ProvidersPath, ".json"));
        ProviderSettingsService.EnsureDefaultExists();
        Llm.Settings = ProviderSettingsService.Load();
        messages.Add(StepMessage("[Provider] 默认 LLM 配置", File.Exists(AppPaths.ProvidersPath)));

        // Agent
        DeleteIfExists(AppPaths.AgentsPath);
        DeleteIfExists(Path.ChangeExtension(AppPaths.AgentsPath, ".json"));
        AgentConfigService.EnsureDefaultExists();
        AgentConfigService.Refresh();
        messages.Add(StepMessage("[Agent] 默认 Agent 配置", File.Exists(AppPaths.AgentsPath)));

        // MCP 服务器
        DeleteIfExists(AppPaths.McpServersPath);
        McpConfigService.EnsureDefaultExists();
        McpConfigService.Refresh();
        messages.Add(StepMessage("[MCP] 默认 MCP 配置", File.Exists(AppPaths.McpServersPath)));

        // Roster 注入模板
        DeleteIfExists(AppPaths.RosterTemplatePath);
        RosterBuilder.WriteDefaultTemplate();
        messages.Add(StepMessage("[Roster] 默认 Roster 模板", File.Exists(AppPaths.RosterTemplatePath)));

        Log.Info("Config", $"已还原默认设置: {string.Join("; ", messages)}");
        return messages;
    }

    /// <summary>按生成结果组装步骤消息(生成失败标记 ⚠ 便于用户/日志定位)。</summary>
    private static string StepMessage(string label, bool generated)
        => generated ? $"{label} 已还原" : $"⚠ {label} 生成失败(见日志)";

    private static void DeleteIfExists(string path)
    {
        try
        {
            // 用 AtomicFile.Delete 而非 File.Delete: 必须连带清掉 .bak/.tmp。
            // 否则"还原默认设置"删掉 providers.toml 后, 旧 providers.toml.bak 仍留在磁盘上
            // 保存着旧 API Key; 且重建后的默认文件一旦解析异常, TryReadText 会回退到那个
            // 陈旧备份, 把用户刚清掉的 Provider/Agent 配置复活。
            AtomicFile.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Config", ex, $"删除配置文件失败: {path}");
        }
    }

    /// <summary>把会话 Roster 快照写入<b>活动会话</b>引擎(下一回合生效)。</summary>
    ///
    /// <para>参数语义与 <c>RosterBuilder.Build</c> 一致: <c>null</c> = 无限制(列出全部子代理),
    /// 非 null(含空表) = 以该表为准。空表表示"用户清空全部", 工具通常也已被注销。</para>
    ///
    /// <para>仅写入当前活动会话: 其它会话的运行中回合不受影响, 下一回合各按自己的快照。</para>
    public void SetRosterEntries(IReadOnlyList<AgentRosterEntry>? entries)
    {
        Sessions.ActiveEngine.SetRosterEntries(entries);

        // Plan 模式下 Roster 变化(如切换"在 Plan 模式中使用")需同步重建工具注册
        if (_isPlanMode && _subagentToolsVisible)
        {
            RebuildSubagentTools();
        }
    }
}
