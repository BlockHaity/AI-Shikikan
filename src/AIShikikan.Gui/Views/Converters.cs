using System;
using System.Globalization;
using Avalonia.Data.Converters;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Gui.Resources;

namespace AIShikikan.Gui.Views;

/// <summary>Git 文件行按钮文案: 已暂存 → "撤销暂存", 未暂存 → "暂存"。</summary>
public class StagedToActionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Strings.GitPanel_Unstage : Strings.GitPanel_Stage;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>Git porcelain 状态 → 当前语言的可读状态。</summary>
public class GitStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GitFileStatus file) return string.Empty;
        return (file.IndexStatus, file.WorkTreeStatus) switch
        {
            ('?', _) => Strings.Git_StatusUntracked,
            ('A', _) => Strings.Git_StatusAddedStaged,
            ('M', ' ') => Strings.Git_StatusModifiedStaged,
            ('M', _) => Strings.Git_StatusModified,
            ('D', ' ') => Strings.Git_StatusDeletedStaged,
            ('D', _) => Strings.Git_StatusDeleted,
            ('R', _) => Strings.Git_StatusRenamed,
            ('C', _) => Strings.Git_StatusCopied,
            (_, 'M') => Strings.Git_StatusModified,
            (_, 'D') => Strings.Git_StatusDeleted,
            _ => Strings.Git_StatusChanged
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>object? → bool: null 时隐藏, 非 null 时显示。</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>string? → bool: 非空字符串时显示。</summary>
public class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>int → bool: 值等于 parameter 时显示, 否则隐藏。</summary>
public class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int v || parameter is null) return false;
        return parameter is int p
            ? v == p
            : int.TryParse(parameter.ToString(), out var p2) && v == p2;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>bool → 文案: 获取中/获取模型列表。</summary>
public class FetchingToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Strings.Settings_Fetching : Strings.Settings_FetchModels;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>集合数量(int) → bool: 数量大于 0 时显示。</summary>
public class ModelListVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 同时接受计数(int)与集合(ICollection): 设置页的"候选模型"区绑定的是
        // ObservableCollection<string> 而不是 Count, 两者都要能判空
        if (value is int count) return count > 0;
        if (value is System.Collections.ICollection collection) return collection.Count > 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// ThinkingLevel → 当前语言的档位文案。
/// </summary>
/// <remarks>
/// <see cref="ThinkingLevels.DisplayName"/> 现返回稳定标识(off/auto/low/medium/high/xhigh/max)而非中文,
/// Core 层不能反向引用 GUI 的 Strings, 因此展示文案必须在这里按标识映射。
/// 依赖 7 个新键: Thinking_Level_Off / Auto / Low / Medium / High / XHigh / Max(两个 resx 都要加)。
/// </remarks>
public class ThinkingLevelToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ThinkingLevel level) return string.Empty;

        return ThinkingLevels.DisplayName(level) switch
        {
            "off" => Strings.Thinking_Level_Off,
            "auto" => Strings.Thinking_Level_Auto,
            "low" => Strings.Thinking_Level_Low,
            "medium" => Strings.Thinking_Level_Medium,
            "high" => Strings.Thinking_Level_High,
            "xhigh" => Strings.Thinking_Level_XHigh,
            _ => Strings.Thinking_Level_Max
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}