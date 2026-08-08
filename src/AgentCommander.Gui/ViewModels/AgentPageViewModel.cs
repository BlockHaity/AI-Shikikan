using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AgentCommander.Core.Services.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentCommander.Gui.ViewModels;

public partial class AgentPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private IReadOnlyList<CliAgentDefinition> _agents = [];

    [ObservableProperty]
    private string _newAgentName = string.Empty;

    [ObservableProperty]
    private string _newAgentExecutable = string.Empty;

    public AgentPageViewModel()
    {
        AppShell.Instance.DataChanged += OnDataChanged;
        RefreshAgents();
    }

    [RelayCommand]
    private void AddAgent()
    {
        if (string.IsNullOrWhiteSpace(NewAgentName)) return;

        var name = NewAgentName.Trim();
        var id = Regex.Replace(name, @"[^a-zA-Z0-9\-_]", "-").ToLowerInvariant();
        if (id.Length == 0)
        {
            id = "agent-" + Guid.NewGuid().ToString("N")[..6];
        }

        AgentConfigService.SaveUserAgent(new CliAgentDefinition
        {
            Id = id,
            Name = name,
            Executable = string.IsNullOrWhiteSpace(NewAgentExecutable) ? id : NewAgentExecutable.Trim(),
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "GUI 创建的自定义 Agent"
        });
        NewAgentName = string.Empty;
        NewAgentExecutable = string.Empty;
        AppShell.Instance.ReloadAgents();
    }

    [RelayCommand]
    private void RemoveAgent(string id)
    {
        AgentConfigService.RemoveUserAgent(id);
        AppShell.Instance.ReloadAgents();
    }

    private void OnDataChanged() => RefreshAgents();

    private void RefreshAgents()
    {
        Agents = AppShell.Instance.Agents.ToList();
    }
}