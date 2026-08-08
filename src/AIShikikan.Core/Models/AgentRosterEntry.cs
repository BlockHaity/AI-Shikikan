using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIShikikan.Core.Models;

/// <summary>Agent 编目条目: 指挥官可调用的子 Agent 配置, session 级别。</summary>
public partial class AgentRosterEntry : ObservableObject
{
    [ObservableProperty]
    private string _agentId = string.Empty;

    [ObservableProperty]
    private string _display = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string? _personaId;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private bool _isExpanded = false;

    [ObservableProperty]
    private bool _isCollapsed => !IsExpanded;

    public string DisplayText => string.IsNullOrEmpty(Display) ? AgentId : Display;

    public string PersonaDisplayName { get; set; } = string.Empty;
}
