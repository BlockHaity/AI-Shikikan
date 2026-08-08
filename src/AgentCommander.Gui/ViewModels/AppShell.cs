using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using AgentCommander.Core.Services;
using AgentCommander.Core.Services.Agents;
using AgentCommander.Core.Services.Personas;
using AgentCommander.Core.Services.Templates;

namespace AgentCommander.Gui.ViewModels;

/// <summary>GUI 全局外壳: 单一 CommanderRuntime 与可刷新的展示集合, 供各页共享。</summary>
public sealed class AppShell
{
    public static AppShell Instance { get; } = new();

    public CommanderRuntime Runtime { get; }

    public ObservableCollection<Persona> Personas { get; } = [];

    public ObservableCollection<AgentTemplate> Templates { get; } = [];

    public ObservableCollection<CliAgentDefinition> Agents { get; } = [];

    public event Action? DataChanged;

    private AppShell()
    {
        Runtime = CommanderRuntime.Boot(Directory.GetCurrentDirectory());
        ReloadPersonas();
        ReloadTemplates();
        ReloadAgents();
    }

    public void ReloadPersonas()
    {
        Sync(Personas, PersonaService.LoadAll());
        DataChanged?.Invoke();
    }

    public void ReloadTemplates()
    {
        Sync(Templates, AgentTemplateService.LoadAll());
        DataChanged?.Invoke();
    }

    public void ReloadAgents()
    {
        AgentConfigService.Refresh();
        Sync(Agents, AgentConfigService.LoadAll());
        DataChanged?.Invoke();
    }

    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}