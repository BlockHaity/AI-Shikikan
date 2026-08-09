using System;
using System.ComponentModel;
using Avalonia.Controls;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.ViewModels;
using ScottPlot;

namespace AIShikikan.Gui.Views;

public partial class HomePageView : UserControl
{
    private HomePageViewModel? _vm;

    public HomePageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as HomePageViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            RenderChart();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomePageViewModel.ChartDates)
            or nameof(HomePageViewModel.SelectedRangeIndex))
        {
            RenderChart();
        }
    }

    /// <summary>按当前数据重建用量趋势折线图(输入/输出/缓存命中 三条线)。</summary>
    private void RenderChart()
    {
        if (_vm is null) return;

        var plot = UsageChart.Plot;
        plot.Clear();

        var dark = _vm.ThemeService.IsDarkTheme;
        var textColor = new ScottPlot.Color(dark ? "#C9C9C9" : "#1C1B1F");
        var gridColor = new ScottPlot.Color(dark ? "#3A3A3A" : "#E4E1E0");

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Color(textColor);
        plot.Grid.MajorLineColor = gridColor;

        var dates = _vm.ChartDates;
        if (dates.Length > 0)
        {
            var input = plot.Add.Scatter(dates, _vm.ChartInputTokens);
            input.LegendText = Strings.Home_UsageInput;
            input.Color = new ScottPlot.Color("#6750A4");
            input.LineWidth = 2;
            input.MarkerSize = 0;

            var output = plot.Add.Scatter(dates, _vm.ChartOutputTokens);
            output.LegendText = Strings.Home_UsageOutput;
            output.Color = new ScottPlot.Color("#00897B");
            output.LineWidth = 2;
            output.MarkerSize = 0;

            var cached = plot.Add.Scatter(dates, _vm.ChartCachedTokens);
            cached.LegendText = Strings.Home_UsageCacheHit;
            cached.Color = new ScottPlot.Color("#E91E63");
            cached.LineWidth = 2;
            cached.MarkerSize = 0;

            plot.ShowLegend(Alignment.UpperRight);
        }

        UsageChart.Refresh();
    }
}
