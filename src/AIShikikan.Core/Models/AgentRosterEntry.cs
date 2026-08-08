using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIShikikan.Core.Models;

/// <summary>Agent 编目条目: 指挥官可调用的子 Agent 配置, session 级别。</summary>
public class AgentRosterEntry : INotifyPropertyChanged
{
    private string _agentId = string.Empty;
    private string _display = string.Empty;
    private string _description = string.Empty;
    private string? _personaId;
    private bool _enabled = true;
    private bool _isExpanded = false;
    private string _personaDisplayName = string.Empty;

    public string AgentId
    {
        get => _agentId;
        set { _agentId = value; OnPropertyChanged(); }
    }

    public string Display
    {
        get => _display;
        set { _display = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public string? PersonaId
    {
        get => _personaId;
        set { _personaId = value; OnPropertyChanged(); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsCollapsed => !_isExpanded;

    public string PersonaDisplayName
    {
        get => _personaDisplayName;
        set { _personaDisplayName = value; OnPropertyChanged(); }
    }

    public string DisplayText => string.IsNullOrEmpty(Display) ? AgentId : Display;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
