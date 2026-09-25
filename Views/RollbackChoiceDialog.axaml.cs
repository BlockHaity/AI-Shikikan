using Avalonia.Controls;
using Avalonia.Interactivity;
using AIShikikan.Core.Models;
using AIShikikan.Gui.Resources;

namespace AIShikikan.Gui.Views;

/// <summary>回滚方式选择窗口；选择结果由独立的二次确认窗口再次确认。</summary>
public partial class RollbackChoiceDialog : Window
{
    public RollbackChoiceDialog()
    {
        InitializeComponent();
    }

    public static Task<CheckpointRollbackMode?> ShowAsync(
        Window owner, string heading, string description, string reset, string revert)
    {
        var dialog = new RollbackChoiceDialog
        {
            Title = Strings.Checkpoint_RollbackChooseTitle,
            HeadingText = { Text = heading },
            DescriptionText = { Text = description },
            ResetTitle = { Text = Strings.Checkpoint_ResetHard },
            ResetDescription = { Text = Strings.Checkpoint_ResetHardDescription },
            ResetButton = { Content = reset },
            RevertTitle = { Text = Strings.Checkpoint_Revert },
            RevertDescription = { Text = Strings.Checkpoint_RevertDescription },
            RevertButton = { Content = revert }
        };

        return dialog.ShowDialog<CheckpointRollbackMode?>(owner);
    }

    private void OnResetClick(object? sender, RoutedEventArgs e) =>
        Close(CheckpointRollbackMode.ResetHard);

    private void OnRevertClick(object? sender, RoutedEventArgs e) =>
        Close(CheckpointRollbackMode.Revert);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
