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

    [ObservableProperty]
    private bool _hasBackgroundImage;

    private readonly HomePageViewModel _homePage;
    private readonly ChatPageViewModel _chatPage;

    public ThemeService ThemeService { get; }

    public MainWindowViewModel(ThemeService themeService)
    {
        ThemeService = themeService;
        _homePage = new HomePageViewModel();
        _chatPage = new ChatPageViewModel();
        _currentPage = _homePage;
        _selectedIndex = 0;
        _hasBackgroundImage = themeService.BackgroundImagePath is not null;

        themeService.BackgroundChanged += (_, path) =>
        {
            HasBackgroundImage = path is not null;
        };
    }

    partial void OnSelectedIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            0 => _homePage,
            1 => _chatPage,
            _ => _homePage
        };
    }
}
