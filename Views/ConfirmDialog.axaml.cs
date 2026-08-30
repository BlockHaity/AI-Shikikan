using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AIShikikan.Gui.Views;

/// <summary>通用二次确认弹窗: 经 ShowAsync 模态展示, 用户点击确认返回 true, 取消/关闭返回 false。</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>模态展示确认弹窗(owner 为宿主窗口)。</summary>
    public static async Task<bool> ShowAsync(
        Window owner, string title, string message, string confirmText, string cancelText)
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
            MessageText = { Text = message },
            ConfirmButton = { Content = confirmText },
            CancelButton = { Content = cancelText }
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
