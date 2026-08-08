using AgentCommander.Core.Services.Agents;
using AgentCommander.Core.Services.Engine;
using AgentCommander.Core.Services.Git;
using AgentCommander.Core.Services.Llm;
using AgentCommander.Core.Services.Personas;
using AgentCommander.Core.Services.Runtime;
using AgentCommander.Core.Services.Templates;
using AgentCommander.Core.Services.Tools;

namespace AgentCommander.Core.Services;

/// <summary>应用启动外观: 初始化配置目录、示例文件、所有服务与工具集, 供 CLI / GUI 复用。</summary>
public sealed class CommanderRuntime
{
    public required string WorkspaceRoot { get; init; }
    public required LlmService Llm { get; init; }
    public required GitStepService Git { get; init; }
    public required AssignmentManager Assignments { get; init; }
    public required ToolRegistry Registry { get; init; }
    public required AgentEngine Engine { get; init; }
    public required IReadOnlyList<Persona> Personas { get; init; }
    public required IReadOnlyList<AgentTemplate> Templates { get; init; }
    public required IReadOnlyList<CliAgentDefinition> Agents { get; init; }

    public static CommanderRuntime Boot(string? workspaceRoot = null, string? personaId = null)
    {
        AppPaths.EnsureDirectoriesExist();

        PersonaService.WriteSampleFiles();
        AgentTemplateService.EnsureSamplesExist();
        RosterBuilder.WriteDefaultTemplate();
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

        var registry = new ToolRegistry();
        foreach (var tool in AgentToolFactory.Create(agentsList, personasList, templatesList, git, assignments))
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

        return new CommanderRuntime
        {
            WorkspaceRoot = root,
            Llm = llm,
            Git = git,
            Assignments = assignments,
            Registry = registry,
            Engine = engine,
            Personas = personasList,
            Templates = templatesList,
            Agents = agentsList
        };
    }
}