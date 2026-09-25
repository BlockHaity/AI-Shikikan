using AIShikikan.Core.Logging;
using AIShikikan.Core.Models;
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

    /// <summary>旧步骤服务，仅保留给待迁移的兼容工具和旧数据读取。</summary>
    public required GitStepService Git { get; init; }

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
    public IReadOnlyList<AgentRosterEntry> CurrentRosterEntries =>
        Sessions.Active?.Engine.RosterEntries ?? [];

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
        var git = new GitStepService(root);
        var checkpoints = new GitCheckpointStore();
        var gitService = new GitService(checkpoints);
        var assignments = new AssignmentManager(git);
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
        foreach (var tool in AgentToolFactory.CreateCoreTools(git))
        {
            registry.Register(tool);
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(agentsList, personasList, templatesList, git, assignments, llm))
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
            Git = git,
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

    /// <summary>创建会话引擎(注册表工厂回调): 新会话继承当前 Plan 模式与人格设置;
    /// Roster 由 GUI 选定会话后单独推送, 不跨会话继承。</summary>
    private AgentEngine CreateEngine(string sessionId, string sessionTitle,
        ISessionEngineHost host, EngineEventHub hub)
        => new AgentEngine(
            llm: Llm,
            registry: Registry,
            git: Git,
            assignments: Assignments,
            personas: Personas,
            templates: Templates,
            agents: Agents,
            workspaceRoot: WorkspaceRoot,
            personaText: CurrentPersonaText,
            options: new EngineOptions { IsPlanMode = _isPlanMode },
            sessionId: sessionId,
            sessionTitle: sessionTitle,
            host: host,
            hub: hub);

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
        Engine.Options.IsPlanMode = planMode;
        // 指挥官人格按新模式重新解析提示词(通用正文 + 对应模式专属段)
        RefreshCommanderPersonaText();
        RebuildSubagentTools();
        Log.Info("Engine", $"Plan 模式切换为 {planMode}, 当前工具数={Registry.All.Count}");
    }

    /// <summary>Plan 模式下允许注册/执行的子代理: 当前会话 Roster 条目开启 UseInPlanMode,
    /// 且 Agent 配置了 plan_args 或开启了"无 plan 参数也可在 Plan 模式使用"。</summary>
    public bool IsAgentAllowedInPlanMode(CliAgentDefinition agent)
    {
        return (agent.PlanArgs is { Count: > 0 } || agent.AllowPlanModeWithoutArgs) &&
               CurrentRosterEntries.Any(e =>
                   string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase) && e.UseInPlanMode);
    }

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
            agents = agents.Where(IsAgentAllowedInPlanMode).ToList();
            if (agents.Count == 0)
            {
                // Plan 模式下无任何授权子代理: 不注册组工具, AI 无法分派任务
                Log.Info("Engine", "Plan 模式: 无授权子代理, 子代理工具全部移除");
                return;
            }
        }

        foreach (var tool in AgentToolFactory.CreateSubagentTools(
                     agents, personas, templates, Git, Assignments, Llm))
        {
            Registry.Register(tool);
        }
    }

    /// <summary>右侧栏可见性联动: 关闭时从注册表移除子代理工具(AI 下一回合不再可见), 打开时重新注册。
    /// Roster 注入由 GUI 在打开后调用 SetRosterEntries 恢复。</summary>
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
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Config", ex, $"删除配置文件失败: {path}");
        }
    }

    public void SetRosterEntries(IReadOnlyList<AgentRosterEntry> entries)
    {
        // 仅写入当前活动会话的 Roster 快照: 其它会话的运行中回合不受影响, 下一回合各按自己的快照
        Sessions.ActiveEngine.SetRosterEntries(entries);

        // Plan 模式下 Roster 变化(如切换"在 Plan 模式中使用")需同步重建工具注册
        if (_isPlanMode && _subagentToolsVisible)
        {
            RebuildSubagentTools();
        }
    }
}
