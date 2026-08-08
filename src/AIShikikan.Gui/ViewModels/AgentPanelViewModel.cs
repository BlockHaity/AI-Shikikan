using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

public sealed record PersonaOption(Persona? Value, string Label);

/// <summary>子 Agent 列表项: 全局定义(agents.json)为基准, 会话覆盖(roster.json)优先。</summary>
public partial class SubAgentItemViewModel : ViewModelBase
{
    public CliAgentDefinition Agent { get; }
    public string AgentId => Agent.Id;
    public string Display => Agent.Display;
    public bool IsBuiltIn { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private bool _hasSessionOverride;

    [ObservableProperty]
    private PersonaOption? _selectedOption;

    public SubAgentItemViewModel(CliAgentDefinition agent, bool isBuiltIn)
    {
        Agent = agent;
        IsBuiltIn = isBuiltIn;
        _enabled = true;
        _description = agent.Description;
    }

    partial void OnSelectedOptionChanged(PersonaOption? value)
    {
        OnPropertyChanged(nameof(PersonaName));
    }

    public string PersonaName => SelectedOption?.Value?.Display ?? string.Empty;
}

/// <summary>侧栏"分配工具": 子 Agent 列表(增删改、专家绑定) + 快速分配 + 分派列表。</summary>
public partial class AgentPanelViewModel : ViewModelBase
{
    private readonly AppShell _shell = AppShell.Instance;
    private readonly CommanderRuntime _runtime = AppShell.Instance.Runtime;

    public ObservableCollection<SubAgentItemViewModel> SubAgents { get; } = [];

    public ObservableCollection<Assignment> Assignments { get; } = [];

    [ObservableProperty]
    private bool _isAssigning;

    [ObservableProperty]
    private bool _rosterEnabled;

    [ObservableProperty]
    private bool _addFormVisible;

    [ObservableProperty]
    private bool _hasNoAgents;

    [ObservableProperty]
    private string _newAgentName = string.Empty;

    [ObservableProperty]
    private string _newAgentExecutable = string.Empty;

    [ObservableProperty]
    private string _newAgentDescription = string.Empty;

    private IReadOnlyList<Persona> _personas = [];
    public IReadOnlyList<Persona> Personas
    {
        get => _personas;
        private set => SetProperty(ref _personas, value);
    }

    private IReadOnlyList<PersonaOption> _personaOptions = [];
    public IReadOnlyList<PersonaOption> PersonaOptions
    {
        get => _personaOptions;
        private set => SetProperty(ref _personaOptions, value);
    }

    private IReadOnlyList<CliAgentDefinition> _agents = [];
    public IReadOnlyList<CliAgentDefinition> Agents
    {
        get => _agents;
        private set => SetProperty(ref _agents, value);
    }

    private CliAgentDefinition? _selectedAgent;
    public CliAgentDefinition? SelectedAgent
    {
        get => _selectedAgent;
        set => SetProperty(ref _selectedAgent, value);
    }

    private string _sessionId = string.Empty;

    [ObservableProperty]
    private string _assignTaskText = string.Empty;

    [ObservableProperty]
    private bool _isAsyncAssign;

    [ObservableProperty]
    private Persona? _commanderPersona;

    [ObservableProperty]
    private bool _useCommanderPersonaForAgents;

    public AgentPanelViewModel()
    {
        _rosterEnabled = true;
        RefreshAll();
        _shell.DataChanged += () => Dispatcher.UIThread.Post(RefreshAll);
        _runtime.Assignments.AssignmentChanged += _ =>
            Dispatcher.UIThread.Post(RefreshAssignments);
    }

    partial void OnRosterEnabledChanged(bool value) => PushRoster();

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        RefreshSubAgents();
    }

    [RelayCommand]
    public void RefreshAll()
    {
        Personas = _shell.Personas.ToList();
        PersonaOptions = new[] { new PersonaOption(null, "(无)") }
            .Concat(Personas.Select(p => new PersonaOption(p, p.Display)))
            .ToList();
        Agents = _shell.Agents.ToList();
        if (SelectedAgent is null && Agents.Count > 0)
        {
            SelectedAgent = Agents[0];
        }

        RefreshSubAgents();
        RefreshAssignments();
    }

    /// <summary>以全局定义重建列表, 会话覆盖(roster.json 中描述/专家非空)优先。</summary>
    private void RefreshSubAgents()
    {
        var userIds = new HashSet<string>(
            AgentConfigService.LoadUserFile().Agents.Select(a => a.Id),
            StringComparer.OrdinalIgnoreCase);

        RosterConfig? sessionConfig = null;
        if (!string.IsNullOrEmpty(_sessionId))
        {
            sessionConfig = RosterConfigService.Load(_sessionId);
        }

        SubAgents.Clear();
        foreach (var agent in Agents)
        {
            var item = new SubAgentItemViewModel(agent, !userIds.Contains(agent.Id))
            {
                SelectedOption = PersonaOptionFor(agent.RecommendedPersonaId)
            };

            if (sessionConfig is not null)
            {
                var entry = sessionConfig.Entries.FirstOrDefault(e =>
                    string.Equals(e.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    item.Enabled = entry.Enabled;
                    item.HasSessionOverride = !string.IsNullOrWhiteSpace(entry.Description)
                                              || !string.IsNullOrWhiteSpace(entry.PersonaId);
                    if (item.HasSessionOverride)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Description))
                        {
                            item.Description = entry.Description;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.PersonaId))
                        {
                            item.SelectedOption = PersonaOptionFor(entry.PersonaId);
                        }
                    }
                }
            }

            SubAgents.Add(item);
        }

        HasNoAgents = SubAgents.Count == 0;
        PushRoster();
    }

    private PersonaOption? PersonaOptionFor(string? personaId)
    {
        if (string.IsNullOrWhiteSpace(personaId)) return null;
        return PersonaOptions.FirstOrDefault(o =>
            o.Value is not null && string.Equals(o.Value.Id, personaId, StringComparison.OrdinalIgnoreCase));
    }

    private void PushRoster()
    {
        if (!RosterEnabled)
        {
            _runtime.SetRosterEntries([]);
            return;
        }

        var entries = SubAgents.Where(e => e.Enabled)
            .Select(e => new AgentRosterEntry
            {
                AgentId = e.AgentId,
                Display = e.Display,
                Description = e.Description,
                PersonaId = e.SelectedOption?.Value?.Id,
                Enabled = true
            })
            .ToList();
        _runtime.SetRosterEntries(entries);
    }

    [RelayCommand]
    private void ToggleAddForm()
    {
        AddFormVisible = !AddFormVisible;
        if (!AddFormVisible)
        {
            NewAgentName = string.Empty;
            NewAgentExecutable = string.Empty;
            NewAgentDescription = string.Empty;
        }
    }

    [RelayCommand]
    private void AddAgent()
    {
        if (string.IsNullOrWhiteSpace(NewAgentName)) return;

        var name = NewAgentName.Trim();
        var id = name.Replace(" ", "-").ToLowerInvariant();
        var agent = new CliAgentDefinition
        {
            Id = id,
            Name = name,
            Executable = string.IsNullOrWhiteSpace(NewAgentExecutable) ? id : NewAgentExecutable.Trim(),
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = NewAgentDescription.Trim()
        };

        AgentConfigService.SaveUserAgent(agent);
        _shell.ReloadAgents();
        ToggleAddForm();
    }

    [RelayCommand]
    private void ToggleEntryExpanded(SubAgentItemViewModel item)
    {
        item.IsExpanded = !item.IsExpanded;
    }

    /// <summary>保存描述/专家: 会话覆盖(条目已有自定义) → 写会话; 否则 → 写全局(agents.json)。</summary>
    [RelayCommand]
    private void SaveEntry(SubAgentItemViewModel item)
    {
        var personaId = item.SelectedOption?.Value?.Id;
        if (item.HasSessionOverride)
        {
            RosterConfigService.AddEntry(_sessionId, item.AgentId, item.Display, item.Description, personaId);
        }
        else
        {
            RosterConfigService.RemoveIfNoOverride(_sessionId, item.AgentId);
            AgentConfigService.SaveUserAgent(Overlay(item.Agent, item.Description, personaId));
            _shell.ReloadAgents();
        }

        RefreshSubAgents();
    }

    /// <summary>把会话覆盖改回全局: 写全局并清除会话条目(若仅开关状态则保留)。</summary>
    [RelayCommand]
    private void MoveToGlobal(SubAgentItemViewModel item)
    {
        var wasEnabled = item.Enabled;
        AgentConfigService.SaveUserAgent(Overlay(item.Agent, item.Description, item.SelectedOption?.Value?.Id));
        RosterConfigService.RemoveEntry(_sessionId, item.AgentId);
        if (!wasEnabled)
        {
            RosterConfigService.SetEnabled(_sessionId, item.AgentId, false);
        }

        _shell.ReloadAgents();
        RefreshSubAgents();
    }

    [RelayCommand]
    private void RemoveAgent(SubAgentItemViewModel item)
    {
        AgentConfigService.RemoveUserAgent(item.AgentId);
        RosterConfigService.RemoveEntry(_sessionId, item.AgentId);
        _shell.ReloadAgents();
        RefreshSubAgents();
    }

    [RelayCommand]
    private void ToggleEntryEnabled(SubAgentItemViewModel item)
    {
        RosterConfigService.SetEnabled(_sessionId, item.AgentId, item.Enabled);
        PushRoster();
    }

    [RelayCommand]
    private void CancelAssignment(string assignmentId)
    {
        _shell.CancelDispatch(assignmentId);
        RefreshAssignments();
    }

    public void RefreshAssignments()
    {
        Assignments.Clear();
        foreach (var a in _shell.ProcessList)
        {
            Assignments.Add(a);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void Assign()
    {
        if (SelectedAgent is null || string.IsNullOrWhiteSpace(AssignTaskText)) return;

        var agent = SelectedAgent;
        var taskText = AssignTaskText.Trim();
        IsAssigning = true;

        _shell.Dispatch(agent, taskText, IsAsyncAssign ? "async" : "sync",
            useCommanderPersona: UseCommanderPersonaForAgents,
            onFinished: (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsAssigning = false;
                    AssignTaskText = string.Empty;
                    RefreshAssignments();
                });
            });
    }

    private bool CanAssign() => SelectedAgent is not null
                                && !string.IsNullOrWhiteSpace(AssignTaskText)
                                && !IsAssigning;

    private static CliAgentDefinition Overlay(CliAgentDefinition source, string description, string? personaId)
    {
        return new CliAgentDefinition
        {
            Id = source.Id,
            Name = source.Name,
            Executable = source.Executable,
            Args = source.Args.ToList(),
            MemoryFile = source.MemoryFile,
            Injection = source.Injection,
            DefaultMode = source.DefaultMode,
            MaxConcurrent = source.MaxConcurrent,
            RequireApproval = source.RequireApproval,
            TimeoutMinutes = source.TimeoutMinutes,
            Expertise = source.Expertise.ToList(),
            Description = description,
            RecommendedPersonaId = personaId,
            DefaultTemplateId = source.DefaultTemplateId,
            RosterEntry = source.RosterEntry,
            Environment = new Dictionary<string, string>(source.Environment)
        };
    }
}