using System;
using System.Linq;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Gui.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIShikikan.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    public string WelcomeText { get; } = "欢迎使用 AI-Shikikan";
    public string DescriptionText { get; } = "一个强大的 Agent 管理与指挥工具";
    public string VersionText { get; } = $"版本 {AppInfo.Version}";

    public ThemeService ThemeService { get; }

    /// <summary>折线图时间范围选项(索引对应 7/14/30/全部 天)。</summary>
    public string[] RangeOptions { get; } =
        [Strings.Home_Range7, Strings.Home_Range14, Strings.Home_Range30, Strings.Home_RangeAll];

    [ObservableProperty]
    private UsageSnapshot _usageSnapshot = new();

    [ObservableProperty]
    private int _selectedRangeIndex = 1;

    [ObservableProperty]
    private DateTime[] _chartDates = [];

    [ObservableProperty]
    private double[] _chartInputTokens = [];

    [ObservableProperty]
    private double[] _chartOutputTokens = [];

    [ObservableProperty]
    private double[] _chartCachedTokens = [];

    public bool HasChartData => ChartDates.Length > 0;

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

    partial void OnSelectedRangeIndexChanged(int value)
    {
        RebuildChart();
    }

    private void RefreshUsage()
    {
        UsageSnapshot = UsageStatsService.GetSnapshot();

        OnPropertyChanged(nameof(TodayUsageText));
        OnPropertyChanged(nameof(TotalUsageText));
        OnPropertyChanged(nameof(LlmCallsText));
        OnPropertyChanged(nameof(AgentCallsText));

        RebuildChart();
    }

    /// <summary>按所选时间范围过滤按天聚合数据, 生成折线图序列。</summary>
    private void RebuildChart()
    {
        var daily = UsageSnapshot.DailyStats;
        if (daily.Count == 0)
        {
            ChartDates = [];
            ChartInputTokens = [];
            ChartOutputTokens = [];
            ChartCachedTokens = [];
            OnPropertyChanged(nameof(HasChartData));
            return;
        }

        var limit = SelectedRangeIndex switch
        {
            0 => 7,
            1 => 14,
            2 => 30,
            _ => int.MaxValue
        };

        var from = limit == int.MaxValue
            ? DateTime.MinValue
            : DateTime.Today.AddDays(-(limit - 1));
        var filtered = daily.Where(d => d.Date >= from).ToList();

        ChartDates = filtered.Select(d => d.Date).ToArray();
        ChartInputTokens = filtered.Select(d => (double)d.InputTokens).ToArray();
        ChartOutputTokens = filtered.Select(d => (double)d.OutputTokens).ToArray();
        ChartCachedTokens = filtered.Select(d => (double)d.CachedTokens).ToArray();
        OnPropertyChanged(nameof(HasChartData));
    }

    private static string Format(int value) => value.ToString("N0");
}
