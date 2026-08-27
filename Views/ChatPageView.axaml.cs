using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.ViewModels;

namespace AIShikikan.Gui.Views;

public partial class ChatPageView : UserControl
{
    private INotifyCollectionChanged? _messagesCollection;

    private DateTime _lastEscUtc; // 双击 ESC 判定窗口

    public ChatPageView()
    {
        InitializeComponent();

        // 隧道方式捕获 ESC(无论焦点在哪个控件上), 双击终止生成
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not ChatPageViewModel vm || !vm.IsSending) return;

        var now = DateTime.UtcNow;
        if ((now - _lastEscUtc).TotalMilliseconds <= 600)
        {
            vm.StopGenerationCommand.Execute(null);
            _lastEscUtc = default;
            e.Handled = true;
        }
        else
        {
            _lastEscUtc = now;
        }
    }

    private void OnModeIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        // Build(未勾选)高亮为 Filled, Plan(勾选)普通为 Outlined
        SetModeHighlight(highlightBuild: ModeToggle.IsChecked != true);
    }

    private void SetModeHighlight(bool highlightBuild)
    {
        if (ModeToggle is null) return;
        if (highlightBuild)
        {
            ModeToggle.Classes.Add("Filled");
            ModeToggle.Classes.Remove("Outlined");
        }
        else
        {
            ModeToggle.Classes.Add("Outlined");
            ModeToggle.Classes.Remove("Filled");
        }
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ChatPageViewModel vm)
        {
            void Scroll() => Dispatcher.UIThread.Post(() => MessageScroller?.ScrollToEnd(),
                DispatcherPriority.Background);

            void AttachMessages()
            {
                if (_messagesCollection is not null)
                {
                    _messagesCollection.CollectionChanged -= M;
                    _messagesCollection = null;
                }

                if (vm.Messages is { } col)
                {
                    _messagesCollection = col;
                    col.CollectionChanged += M;
                }
            }

            void M(object? s, NotifyCollectionChangedEventArgs a) => Scroll();

            vm.PropertyChanged += (_, args) =>
            {
                // 流式期间在同一个集合上 Add, 仅靠 PropertyChanged 不够; 集合变化也要触发
                if (args.PropertyName == nameof(ChatPageViewModel.Messages))
                {
                    AttachMessages();
                    Scroll();
                }
            };
            AttachMessages();
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

    private void OnThinkingSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        if (sender is ListBox { SelectedItem: ThinkingLevel level } && vm.SelectedThinking != level)
        {
            vm.SelectedThinking = level;
        }

        ThinkingButton.Flyout?.Hide();
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
