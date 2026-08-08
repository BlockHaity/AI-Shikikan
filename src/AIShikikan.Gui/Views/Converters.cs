using System;
using System.Globalization;
using Avalonia.Data.Converters;

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