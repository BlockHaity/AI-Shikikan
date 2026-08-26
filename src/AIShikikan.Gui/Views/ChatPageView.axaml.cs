using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.ViewModels;

namespace AIShikikan.Gui.Views;

public partial class ChatPageView : UserControl
{
    public ChatPageView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ChatPageViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatPageViewModel.Messages))
                {
                    Dispatcher.UIThread.Post(() => MessageScroller?.ScrollToEnd(),
                        DispatcherPriority.Background);
                }
            };
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is ChatPageViewModel vm && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private void OnThinkingDepthClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is ChatPageViewModel vm
            && int.TryParse(button.CommandParameter?.ToString(), out var depth))
        {
            vm.ThinkingDepth = depth;
        }
    }

    private async void OnWorkDirClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Chat_WorkDir,
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            vm.WorkDir = path ?? string.Empty;
        }
    }
}
