using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>「全部」范围且无任何数据时热力图的默认展示周数。</summary>
    private const int DefaultHeatmapWeeks = 26;

    // 版本号展示复用既有 Settings_Version 键("版本" / "Version"), 不新增 Home_ 专用键。
    public string VersionText { get; } = $"{Strings.Settings_Version} {AppInfo.Version}";

    public ThemeService ThemeService { get; }

    /// <summary>主页全局时间范围选项(索引对应 7/14/30/全部 天), 趋势图与热力图共用。</summary>
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

    /// <summary>热力图全部格子(行优先展开: 第 r 行 = 各周的第 r 天, 周一→周日), 由 HeatmapPanel 均分拉伸填满宽度。</summary>
    public IReadOnlyList<HeatmapCellVm> HeatCells { get; private set; } = [];

    /// <summary>热力图网格列数(周数), 随全局时间范围变化。</summary>
    [ObservableProperty]
    private int _heatmapColumns = 1;

    /// <summary>热力图网格最小总宽(星期栏 + 各列按最小格宽): 窄视口时由 ScrollViewer 横向滚动。</summary>
    [ObservableProperty]
    private double _heatmapMinWidth;

    /// <summary>图例示例格(等级 0..4)。</summary>
    public IReadOnlyList<HeatmapLegendVm> HeatLegend { get; private set; } = [];

    /// <summary>星期纵栏标签(与 7 行对齐, 仅标一/三/五)。
    /// 刻意保留中文缩写: 这是热力图"星期坐标轴"的内容标识而非可翻译文案, 且 GutterWidth=18px
    /// 只够放下 1 个汉字宽度——改用 CultureInfo 的 AbbreviatedDayNames 会输出 Mon/Wed/Fri 这类
    /// 3 字母串并被裁剪。要本地化需同时放大 GutterWidth, 见报告。</summary>
    public IReadOnlyList<string> HeatGutter { get; } = ["一", "", "三", "", "五", "", ""];

    /// <summary>热力图覆盖的日期范围文本。</summary>
    [ObservableProperty]
    private string _heatRangeText = string.Empty;

    public string TodayUsageText =>
        $"{Strings.Home_UsageInput} {Format(UsageSnapshot.TodayInputTokens)} · " +
        $"{Strings.Home_UsageOutput} {Format(UsageSnapshot.TodayOutputTokens)}";

    public string TotalUsageText =>
        $"{Strings.Home_UsageInput} {Format(UsageSnapshot.TotalInputTokens)} · " +
        $"{Strings.Home_UsageOutput} {Format(UsageSnapshot.TotalOutputTokens)}";

    public string LlmCallsText => string.Format(Strings.Home_TimesFmt, UsageSnapshot.TotalLlmCalls);

    public string AgentCallsText => string.Format(Strings.Home_TimesFmt, UsageSnapshot.TotalAgentCalls);

    public HomePageViewModel(ThemeService themeService)
    {
        ThemeService = themeService;
        RefreshUsage();
        BuildLegend();

        // 主题明暗或动态调色板变化时重绘热力图与图例配色(折线图由视图层自行监听)
        // 注意: 这三个订阅都不解绑。当前 VM 由 MainWindowViewModel 持有到进程结束, 而 view 每次切页重建,
        // "VM 长寿 / view 短命" 下无害; 但若将来改成 VM 也随页面重建, 必须同步补上解绑。
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
        RebuildHeatmap();
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

    /// <summary>主页全局时间范围起点(近7/14/30天; 「全部」取首个使用日, 无数据回退近 26 周)。</summary>
    private DateTime RangeFrom(DateTime today)
    {
        return SelectedRangeIndex switch
        {
            0 => today.AddDays(-6),
            1 => today.AddDays(-13),
            2 => today.AddDays(-29),
            _ => UsageSnapshot.DailyStats.Count > 0
                ? UsageSnapshot.DailyStats.Min(d => d.Date)
                : today.AddDays(-(DefaultHeatmapWeeks - 1) * 7)
        };
    }

    /// <summary>
    /// 按主页全局时间范围构建活跃热力图: 起始日向前对齐到周一, 结束于本周日;
    /// 无记录/未来的天补零(未来为透明占位)。格子平铺, 由视图层网格均分拉伸填满卡片宽度。
    /// 活跃度按全天 token 总量以非零值的 33/66/85 分位数分为 0..4 五档。
    /// 代价说明: 每次都全量排序 + 3 次线性插值分位数, 但输入被 UsageStatsService 的
    /// MaxEntries=5000 封顶(每日聚合后条目数远小于此), 且格子数 = 列数×7 受时间范围约束,
    /// 量级完全可接受, 不做增量缓存——缓存反而会引入"排序结果与快照失效"的一致性坑。
    /// </summary>
    private void RebuildHeatmap()
    {
        var today = DateTime.Today;

        // 全局时间范围起点
        var from = RangeFrom(today);

        // 对齐到周一为一周之首; 结束于本周日(末周含未来日期透明占位)
        var start = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        var thisMonday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var cols = Math.Max(1, (int)Math.Ceiling(((thisMonday.AddDays(6)) - start).TotalDays / 7));

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

        // 行优先展开(网格按行填充): 第 r 行 = 各周的第 r 天(周一→周日)
        var cells = new List<HeatmapCellVm>(cols * 7);
        for (var row = 0; row < 7; row++)
        {
            for (var w = 0; w < cols; w++)
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
                cells.Add(new HeatmapCellVm(
                    date,
                    future,
                    level,
                    BuildCellTip(date, future, stat)));
            }
        }

        HeatCells = cells;
        OnPropertyChanged(nameof(HeatCells));

        HeatmapColumns = cols;
        // 最小总宽 = HeatmapGridPanel.MinTotalWidth 的同一条公式(栏宽 + 间距 + 每列(最小格 + 间距) - 间距)。
        // 两处目前一致(XAML 传入 GutterWidth=18 / CellSpacing=3 / MinCellSize=8, 值为 18 + 11*cols)。
        // 本该只留面板一处, 但 MinTotalWidth 是普通 CLR 属性无法绑定, 要去重得把它改成只读
        // AvaloniaProperty 并让 XAML 绑自身——那是自绘控件的改动, 收益不抵风险, 故在此标注需同步修改。
        HeatmapMinWidth = 18 + 3 + cols * (8 + 3) - 3;

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
        // 日期头必须走 CultureInfo: "M月d日" 里的 月/日 是自定义格式串中的普通字面量
        // (不是 .NET 的标准说明符), 英文界面会原样输出 "Oct月1日"。MMM/d 才是本地化说明符,
        // 中文得到 "10月1"、英文得到 "Oct 1"。
        var dateText = date.ToString("MMM d", CultureInfo.CurrentUICulture);
        return $"{dateText}\n{Strings.Home_UsageInput} {Format((int)(stat?.InputTokens ?? 0))} · " +
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
