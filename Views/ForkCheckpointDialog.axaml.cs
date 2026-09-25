using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AIShikikan.Gui.Resources;

namespace AIShikikan.Gui.Views;

public enum ForkSessionMode
{
    SwitchCurrentSession,
    CopyNewSession
}

public sealed record ForkDialogResult(string BranchName, ForkSessionMode SessionMode);

/// <summary>检查点 Fork 对话框；每次打开都要求明确目标分支和会话处理方式。</summary>
public partial class ForkCheckpointDialog : Window
{
    public ForkCheckpointDialog()
    {
        InitializeComponent();
        BranchBox.TextChanged += (_, _) => UpdateValidity();
    }

    public static Task<ForkDialogResult?> ShowAsync(
        Window owner,
        string heading,
        string description,
        string suggestedBranch,
        bool copyNewSessionByDefault,
        bool occupied,
        string occupiedReason)
    {
        var dialog = new ForkCheckpointDialog
        {
            Title = Strings.Fork_Title,
            HeadingText = { Text = heading },
            DescriptionText = { Text = description },
            OccupiedText = { Text = occupiedReason }
        };
        dialog.BranchBox.Text = suggestedBranch;
        dialog.CopySessionOption.IsChecked = copyNewSessionByDefault;
        dialog.SwitchSessionOption.IsChecked = !copyNewSessionByDefault;
        dialog.OccupiedBorder.IsVisible = occupied;
        dialog.CopySessionOption.IsEnabled = !occupied;
        dialog.SwitchSessionOption.IsEnabled = !occupied;
        dialog.ForkButton.IsEnabled = !occupied;
        dialog.UpdateValidity();
        return dialog.ShowDialog<ForkDialogResult?>(owner);
    }

    private void UpdateValidity()
    {
        if (ForkButton is null) return;
        ForkButton.IsEnabled = !string.IsNullOrWhiteSpace(BranchBox.Text) &&
                               (SwitchSessionOption.IsChecked == true || CopySessionOption.IsChecked == true) &&
                               OccupiedBorder.IsVisible == false;
    }

    private void OnBranchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ForkButton.IsEnabled == false) return;
        AcceptFork();
        e.Handled = true;
    }

    private void OnForkClick(object? sender, RoutedEventArgs e) => AcceptFork();

    private void AcceptFork()
    {
        var branch = BranchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(branch) || OccupiedBorder.IsVisible) return;
        var mode = CopySessionOption.IsChecked == true
            ? ForkSessionMode.CopyNewSession
            : ForkSessionMode.SwitchCurrentSession;
        Close(new ForkDialogResult(branch, mode));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
