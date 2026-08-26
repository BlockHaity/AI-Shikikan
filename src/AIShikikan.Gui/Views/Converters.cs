using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using AIShikikan.Core.Models;

namespace AIShikikan.Gui.Views;

/// <summary>Git 文件行按钮文案: 已暂存 → "撤销暂存", 未暂存 → "暂存"。</summary>
public class StagedToActionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "撤销暂存" : "暂存";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
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
        return value is true ? "获取中…" : "获取模型列表";
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
        return value is int count && count > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>MessageRole → 聊天消息气泡背景色。</summary>
public class MessageBgConverter : IValueConverter
{
    private static readonly SolidColorBrush UserBrush = new(Color.Parse("#3B5998"));
    private static readonly SolidColorBrush AssistantBrush = new(Color.Parse("#424242"));
    private static readonly SolidColorBrush SystemBrush = new(Color.Parse("#5C4033"));
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MessageRole role)
        {
            var app = Application.Current;
            if (app is not null)
            {
                return role switch
                {
                    MessageRole.User => app.TryFindResource("SecondaryContainer", null, out var br1) == true ? br1! : UserBrush,
                    MessageRole.Assistant => app.TryFindResource("SurfaceContainerHigh", null, out var br2) == true ? br2! : AssistantBrush,
                    _ => app.TryFindResource("SurfaceContainer", null, out var br3) == true ? br3! : SystemBrush,
                };
            }
            return role switch
            {
                MessageRole.User => UserBrush,
                MessageRole.Assistant => AssistantBrush,
                _ => SystemBrush,
            };
        }
        return TransparentBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>MessageRole → 水平对齐方式: User 右对齐, 其余左对齐。</summary>
public class MessageAlignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is MessageRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}