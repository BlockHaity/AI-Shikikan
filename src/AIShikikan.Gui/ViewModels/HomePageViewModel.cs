using System;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Usage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIShikikan.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    public string WelcomeText { get; } = "欢迎使用 AI-Shikikan";
    public string DescriptionText { get; } = "一个强大的 Agent 管理与指挥工具";
    public string VersionText { get; } = $"版本 {AppInfo.Version}";

    public ThemeService ThemeService { get; }

    [ObservableProperty]
    private UsageSnapshot _usageSnapshot = new();

    public string TodayUsageText =>
        $"输入 {Format(UsageSnapshot.TodayInputTokens)} · 输出 {Format(UsageSnapshot.TodayOutputTokens)}";

    public string TotalUsageText =>
        $"输入 {Format(UsageSnapshot.TotalInputTokens)} · 输出 {Format(UsageSnapshot.TotalOutputTokens)}";

    public string LlmCallsText => $"{UsageSnapshot.TotalLlmCalls} 次";

    public string AgentCallsText => $"{UsageSnapshot.TotalAgentCalls} 次";

    public HomePageViewModel(ThemeService themeService)
    {
        ThemeService = themeService;
        RefreshUsage();
        AppShell.Instance.DataChanged += RefreshUsage;
    }

    private void RefreshUsage()
    {
        UsageSnapshot = UsageStatsService.GetSnapshot();

        OnPropertyChanged(nameof(TodayUsageText));
        OnPropertyChanged(nameof(TotalUsageText));
        OnPropertyChanged(nameof(LlmCallsText));
        OnPropertyChanged(nameof(AgentCallsText));
    }

    private static string Format(int value) => value.ToString("N0");
}
