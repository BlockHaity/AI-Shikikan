using System;
using System.Collections.Generic;
using System.Linq;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.Services;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIShikikan.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    /// <summary>热力图展示窗口(周数, 周一对齐, 结束于本周)。</summary>
    private const int HeatmapWeeksCount = 26;

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

    /// <summary>与折线数组下标对齐的每日统计(悬浮详情数据源)。</summary>
    public IReadOnlyList<DailyUsageStat> ChartStats { get; private set; } = [];

    /// <summary>趋势图自定义图例(色块 + 名称 + 说明)。</summary>
    public IReadOnlyList<ChartLegendItemVm> LegendItems { get; private set; } = [];

    public bool HasChartData => ChartDates.Length > 0;

    /// <summary>热力图周列(每列为周一→周日 7 个格子)。</summary>
    public IReadOnlyList<HeatmapWeekVm> HeatmapWeeks { get; private set; } = [];

    /// <summary>图例示例格(等级 0..4)。</summary>
    public IReadOnlyList<HeatmapLegendVm> HeatLegend { get; private set; } = [];

    /// <summary>星期纵栏标签(与 7 行对齐, 仅标一/三/五)。</summary>
    public IReadOnlyList<string> HeatGutter { get; } = ["一", "", "三", "", "五", "", ""];

    /// <summary>热力图覆盖的日期范围文本。</summary>
    [ObservableProperty]
    private string _heatRangeText = string.Empty;

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
        BuildLegend();

        // 主题明暗或动态调色板变化时重绘热力图与图例配色(折线图由视图层自行监听)
        ThemeService.ThemeChanged += (_, _) => { RebuildHeatmap(); BuildLegend(); };
        DynamicThemeService.PaletteApplied += (_, _) => { RebuildHeatmap(); BuildLegend(); };
        AppShell.Instance.DataChanged += RefreshUsage;
    }

    /// <summary>构建趋势图自定义图例: 色块随主题, 名称 + 总量/命中/未命中语义说明。</summary>
    private void BuildLegend()
    {
        LegendItems =
        [
            new ChartLegendItemVm(
                HeatmapBrush.ResolveResource("Primary", "#6750A4"),
                Strings.Home_UsageInput,
                Strings.Home_Legend_InputNote),
            new ChartLegendItemVm(
                HeatmapBrush.ResolveResource("Secondary", "#625B71"),
                Strings.Home_UsageOutput,
                Strings.Home_Legend_OutputNote),
            new ChartLegendItemVm(
                HeatmapBrush.ResolveResource("Tertiary", "#7D5260"),
                Strings.Home_UsageCacheHit,
                Strings.Home_Legend_CacheNote)
        ];
        OnPropertyChanged(nameof(LegendItems));
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
        RebuildHeatmap();
    }

    /// <summary>
    /// 构建近 26 周活跃热力图: 以本周一收尾向前对齐, 无记录的天补零;
    /// 活跃度按全天 token 总量以非零值的 33/66/85 分位数分为 0..4 五档。
    /// </summary>
    private void RebuildHeatmap()
    {
        var today = DateTime.Today;

        // 本周一(周一为一周之首)
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = thisMonday.AddDays(-(HeatmapWeeksCount - 1) * 7);

        // 窗口内每日聚合(全量历史分档, 窗口内取值)
        var statMap = new Dictionary<DateTime, DailyUsageStat>();
        foreach (var d in UsageSnapshot.DailyStats)
        {
            statMap[d.Date] = d;
        }

        var allNonZero = UsageSnapshot.DailyStats
            .Select(d => (double)(d.InputTokens + d.OutputTokens))
            .Where(v => v > 0)
            .OrderBy(v => v)
            .ToList();

        var weeks = new List<HeatmapWeekVm>(HeatmapWeeksCount);
        for (var w = 0; w < HeatmapWeeksCount; w++)
        {
            var days = new List<HeatmapCellVm>(7);
            for (var row = 0; row < 7; row++)
            {
                var date = start.AddDays(w * 7 + row);
                var future = date > today;
                double total = 0;
                DailyUsageStat? stat = null;
                if (!future && statMap.TryGetValue(date, out stat))
                {
                    total = stat.InputTokens + stat.OutputTokens;
                }

                var level = LevelOf(total, allNonZero);
                days.Add(new HeatmapCellVm(
                    date,
                    future,
                    level,
                    BuildCellTip(date, future, stat)));
            }

            weeks.Add(new HeatmapWeekVm(days));
        }

        HeatmapWeeks = weeks;
        OnPropertyChanged(nameof(HeatmapWeeks));

        HeatLegend = Enumerable.Range(0, 5).Select(i => new HeatmapLegendVm(HeatmapBrush.ForLevel(i))).ToList();
        OnPropertyChanged(nameof(HeatLegend));

        HeatRangeText = $"{start:yyyy/M/d} – {today:yyyy/M/d}";
    }

    /// <summary>按全量非零值分位数将活跃度映射到 0..4 档。</summary>
    private static int LevelOf(double value, List<double> sortedNonZero)
    {
        if (value <= 0 || sortedNonZero.Count == 0) return 0;
        if (sortedNonZero.Count == 1) return 4;

        var t1 = Percentile(sortedNonZero, 0.33);
        var t2 = Percentile(sortedNonZero, 0.66);
        var t3 = Percentile(sortedNonZero, 0.85);

        if (value <= t1) return 1;
        if (value <= t2) return 2;
        if (value <= t3) return 3;
        return 4;
    }

    /// <summary>线性插值取有序序列的 p 分位数。</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];

        var idx = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        var frac = idx - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }

    private static string BuildCellTip(DateTime date, bool future, DailyUsageStat? stat)
    {
        if (future) return string.Empty;

        var calls = stat?.Calls ?? 0;
        return $"{date:M月d日}\n{Strings.Home_UsageInput} {Format((int)(stat?.InputTokens ?? 0))} · " +
               $"{Strings.Home_UsageOutput} {Format((int)(stat?.OutputTokens ?? 0))} · " +
               $"{string.Format(Strings.Home_TimesFmt, calls)}";
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
            ChartStats = [];
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
        ChartStats = filtered.ToList();
        OnPropertyChanged(nameof(HasChartData));
    }

    private static string Format(int value) => value.ToString("N0");
}

/// <summary>热力图单格。</summary>
public sealed class HeatmapCellVm
{
    public DateTime Date { get; }
    public bool IsFuture { get; }

    /// <summary>活跃度档位(0..4)。</summary>
    public int Level { get; }

    public string Tip { get; }

    /// <summary>按当前主题解析的格底色(未来日期为透明占位)。</summary>
    public IBrush Brush { get; }

    public HeatmapCellVm(DateTime date, bool isFuture, int level, string tip)
    {
        Date = date;
        IsFuture = isFuture;
        Level = level;
        Tip = tip;
        Brush = isFuture ? Brushes.Transparent : HeatmapBrush.ForLevel(level);
    }
}

/// <summary>热力图一列(一周, 周一→周日)。</summary>
public sealed class HeatmapWeekVm
{
    public IReadOnlyList<HeatmapCellVm> Days { get; }

    public HeatmapWeekVm(IReadOnlyList<HeatmapCellVm> days) => Days = days;
}

/// <summary>图例示例格。</summary>
public sealed class HeatmapLegendVm
{
    public IBrush Brush { get; }

    public HeatmapLegendVm(IBrush brush) => Brush = brush;
}

/// <summary>趋势图图例条目(色块 + 名称 + 语义说明)。</summary>
public sealed class ChartLegendItemVm
{
    public IBrush Swatch { get; }
    public string Name { get; }
    public string Note { get; }

    public ChartLegendItemVm(IBrush swatch, string name, string note)
    {
        Swatch = swatch;
        Name = name;
        Note = note;
    }
}

/// <summary>从应用主题资源解析热力图各档配色; 取不到资源时回退到 Material 默认值。</summary>
public static class HeatmapBrush
{
    /// <summary>按键解析主题画刷资源, 失败时回退到给定 hex。</summary>
    public static IBrush ResolveResource(string key, string fallbackHex)
    {
        var app = Application.Current;
        if (app is not null &&
            app.TryGetResource(key, app.RequestedThemeVariant, out var value) &&
            value is ISolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>等级 → 颜色: 空档用中性面, 其余用主色不同透明度阶梯。</summary>
    public static IBrush ForLevel(int level)
    {
        if (level <= 0)
        {
            var surface = ResolveResource("SurfaceVariant", "#49454F");
            return surface is ISolidColorBrush sc
                ? new SolidColorBrush(sc.Color, 0.45)
                : surface;
        }

        var primary = ResolveResource("Primary", "#6750A4");
        if (primary is ISolidColorBrush solid)
        {
            // 档位越高越实: 1→40%, 2→60%, 3→80%, 4→100%
            var alpha = level switch
            {
                1 => 0.40,
                2 => 0.60,
                3 => 0.80,
                _ => 1.0
            };
            return new SolidColorBrush(solid.Color, alpha);
        }

        return primary;
    }
}
