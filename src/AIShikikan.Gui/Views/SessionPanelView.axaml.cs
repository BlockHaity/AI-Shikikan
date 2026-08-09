using Avalonia.Controls;
using Avalonia.Input;
using AIShikikan.Gui.ViewModels;

namespace AIShikikan.Gui.Views;

public partial class SessionPanelView : UserControl
{
    public SessionPanelView()
    {
        InitializeComponent();
    }

    /// <summary>点击会话项(非按钮区域)时切换会话; 右键交给 ContextFlyout。</summary>
    private void OnSessionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (DataContext is not SessionPanelViewModel vm
            || sender is not Control control
            || control.DataContext is not SessionItemViewModel item)
        {
            return;
        }

        vm.SwitchSessionCommand.Execute(item);
    }
}
