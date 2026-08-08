using AgentCommander.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentCommander.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public string Title { get; } = $"{AppInfo.Describe()} (GUI)";

    [ObservableProperty]
    private bool _isRail;

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private ViewModelBase _currentPage;

    private readonly HomePageViewModel _homePage;
    private readonly SettingsPageViewModel _settingsPage;

    public MainWindowViewModel()
    {
        _homePage = new HomePageViewModel();
        _settingsPage = new SettingsPageViewModel();
        _currentPage = _homePage;
        _selectedIndex = 0;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            0 => _homePage,
            2 => _settingsPage,
            _ => _homePage
        };
    }
}
