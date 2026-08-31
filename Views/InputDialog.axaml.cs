using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AIShikikan.Gui.Views;

/// <summary>通用输入弹窗: 展示问题文本 + 多行输入框, 确认返回文本, 取消/关闭返回 null。</summary>
public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.KeyDown += OnInputKeyDown;
        };
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter 提交(Shift+Enter 换行)
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            Close(InputBox.Text);
            e.Handled = true;
        }
    }

    /// <summary>模态展示输入弹窗(owner 为宿主窗口)。</summary>
    public static Task<string?> ShowAsync(
        Window owner, string title, string message, string placeholder, string okText, string cancelText)
    {
        var dialog = new InputDialog
        {
            Title = title,
            MessageText = { Text = message },
            InputBox = { PlaceholderText = placeholder },
            OkButton = { Content = okText },
            CancelButton = { Content = cancelText }
        };

        return dialog.ShowDialog<string?>(owner);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(InputBox.Text);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
