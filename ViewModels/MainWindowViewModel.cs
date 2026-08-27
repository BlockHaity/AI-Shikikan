using AIShikikan.Core;
using AIShikikan.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIShikikan.Gui.ViewModels;

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
    private readonly ChatPageViewModel _chatPage;
    private readonly SettingsPageViewModel _settingsPage;

    public ThemeService ThemeService { get; }

    public MainWindowViewModel(ThemeService themeService)
    {
        ThemeService = themeService;
        _homePage = new HomePageViewModel(themeService);
        _chatPage = new ChatPageViewModel(themeService);
        _settingsPage = new SettingsPageViewModel(themeService);
        _currentPage = _homePage;
        _selectedIndex = 0;
    }

    partial void OnSelectedIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            0 => _homePage,
            1 => _chatPage,
            2 => _settingsPage,
            _ => _homePage
        };
    }
}
