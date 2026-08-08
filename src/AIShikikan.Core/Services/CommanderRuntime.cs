using AIShikikan.Core.Models;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using AIShikikan.Core.Services.Templates;
using AIShikikan.Core.Services.Tools;

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
    public required IReadOnlyList<Persona> Personas { get; init; }
    public required IReadOnlyList<AgentTemplate> Templates { get; init; }
    public required IReadOnlyList<CliAgentDefinition> Agents { get; init; }

    public string? CurrentPersonaText { get; private set; }

    public IReadOnlyList<AgentRosterEntry> CurrentRosterEntries { get; private set; } = [];

    public static void Boot(string? workspaceRoot = null, string? personaId = null)
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

        Instance = new CommanderRuntime
        {
            WorkspaceRoot = root,
            Llm = llm,
            Git = git,
            Assignments = assignments,
            Registry = registry,
            Engine = engine,
            Personas = personasList,
            Templates = templatesList,
            Agents = agentsList,
            CurrentPersonaText = personaText,
            CurrentRosterEntries = []
        };
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
