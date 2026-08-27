using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Mcp;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
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
    public required GitStepService Git { get; init; }
    public required AssignmentManager Assignments { get; init; }
    public required ToolRegistry Registry { get; init; }
    public required AgentEngine Engine { get; init; }
    public required McpService Mcp { get; init; }
    public required IReadOnlyList<Persona> Personas { get; init; }
    public required IReadOnlyList<AgentTemplate> Templates { get; init; }
    public required IReadOnlyList<CliAgentDefinition> Agents { get; init; }

    public string? CurrentPersonaText { get; private set; }

    public IReadOnlyList<AgentRosterEntry> CurrentRosterEntries { get; private set; } = [];

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
        var assignments = new AssignmentManager(git);
        var mcp = new McpService();

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
        foreach (var tool in AgentToolFactory.Create(agentsList, personasList, templatesList, git, assignments, llm))
        {
            registry.Register(tool);
        }

        var persona = personasList.FirstOrDefault(p =>
            string.Equals(p.Id, personaId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, personaId, StringComparison.OrdinalIgnoreCase));
        var personaText = persona is null
            ? personasList.FirstOrDefault()?.SystemPrompt
            : persona.SystemPrompt;

        var engine = new AgentEngine(
            llm: llm,
            registry: registry,
            git: git,
            assignments: assignments,
            personas: personasList,
            templates: templatesList,
            agents: agentsList,
            workspaceRoot: root,
            personaText: personaText);

        Instance = new CommanderRuntime
        {
            WorkspaceRoot = root,
            Llm = llm,
            Git = git,
            Assignments = assignments,
            Registry = registry,
            Engine = engine,
            Mcp = mcp,
            Personas = personasList,
            Templates = templatesList,
            Agents = agentsList,
            CurrentPersonaText = personaText,
            CurrentRosterEntries = []
        };

        // 后台连接 MCP 服务器并注册桥接工具(不阻塞启动)
        var runtime = Instance;
        _ = Task.Run(async () => await runtime.RefreshMcpToolsAsync().ConfigureAwait(false));

        return Instance;
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

        messages.Add($"[MCP] 已连接 {Mcp.ConnectedCount} 个服务器, 注册 {Registry.All.Count(t => t is McpProxyTool)} 个工具");
        return messages;
    }

    public void SetPersonaText(string? text)
    {
        CurrentPersonaText = text;
        Engine.SetPersonaText(text);
    }

    public void SetRosterEntries(IReadOnlyList<AgentRosterEntry> entries)
    {
        CurrentRosterEntries = entries;
        Engine.SetRosterEntries(entries);
    }
}
