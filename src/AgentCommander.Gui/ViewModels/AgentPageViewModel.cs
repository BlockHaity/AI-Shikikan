using System.Collections.Generic;
using AgentCommander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentCommander.Gui.ViewModels;

public partial class AgentPageViewModel : ViewModelBase
{
    private readonly AgentService _agentService;

    [ObservableProperty]
    private IReadOnlyList<AgentInfo> _agents = [];

    [ObservableProperty]
    private string _newAgentName = string.Empty;

    [ObservableProperty]
    private int _agentCount;

    public AgentPageViewModel()
    {
        _agentService = new AgentService();
        RefreshAgents();
    }

    [RelayCommand]
    private void AddAgent()
    {
        if (string.IsNullOrWhiteSpace(NewAgentName)) return;
        _agentService.Create(NewAgentName);
        NewAgentName = string.Empty;
        RefreshAgents();
    }

    [RelayCommand]
    private void RemoveAgent(string id)
    {
        _agentService.Remove(id);
        RefreshAgents();
    }

    private void RefreshAgents()
    {
        Agents = _agentService.GetAll();
        AgentCount = _agentService.Count;
    }
}
