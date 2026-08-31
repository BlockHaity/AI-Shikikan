using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.Services;
using AIShikikan.Gui.ViewModels;

namespace AIShikikan.Gui.Views;

public partial class ChatPageView : UserControl
{
    private INotifyCollectionChanged? _messagesCollection;
    private ChatItemViewModel? _tailItem;
    private ScrollViewer? _listScroller;
    private bool _atBottom = true; // 用户滚上去看历史时暂停自动滚动
    private bool _scrollScheduled;
    private DateTime _lastEscUtc; // 双击 ESC 判定窗口

    public ChatPageView()
    {
        InitializeComponent();

        // 隧道方式捕获 ESC(无论焦点在哪个控件上), 双击终止生成
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);

        // 输入框 Ctrl+V 隧道阶段: 剪贴板图片转附件(先于 TextBox 默认粘贴)
        InputBox.AddHandler(KeyDownEvent, OnInputPreviewKeyDown, RoutingStrategies.Tunnel);

        // 模板应用后拿到 ListBox 内部 ScrollViewer, 用于贴底滚动
        MessageList.TemplateApplied += (_, _) => AttachScroller();
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
        if (DataContext is not ChatPageViewModel vm) return;

        void AttachMessages()
        {
            if (_messagesCollection is not null)
            {
                _messagesCollection.CollectionChanged -= OnMessagesChanged;
                _messagesCollection = null;
            }

            if (vm.Messages is { } col)
            {
                _messagesCollection = col;
                col.CollectionChanged += OnMessagesChanged;
            }

            AttachTail(vm);
        }

        vm.PropertyChanged += (_, args) =>
        {
            // 流式期间在同一个集合上 Add, 仅靠 PropertyChanged 不够; 集合变化也要触发
            if (args.PropertyName == nameof(ChatPageViewModel.Messages))
            {
                AttachMessages();
                ScheduleScroll();
            }
        };
        AttachMessages();
    }

    /// <summary>获取 ListBox 模板内部的 ScrollViewer 并观察滚动偏移(判断用户是否贴底)。</summary>
    private void AttachScroller()
    {
        if (_listScroller is not null)
        {
            _listScroller.PropertyChanged -= OnScrollerPropertyChanged;
        }

        _listScroller = MessageList.FindDescendantOfType<ScrollViewer>();
        if (_listScroller is not null)
        {
            _listScroller.PropertyChanged += OnScrollerPropertyChanged;
        }

        UpdateAtBottom();
    }

    private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 只观察 Offset: 内容增长不改 offset, 避免把"贴底跟随"误判为"已离开底部"
        if (e.Property == ScrollViewer.OffsetProperty)
        {
            UpdateAtBottom();
        }
    }

    private void UpdateAtBottom()
    {
        if (_listScroller is not { } s) return;
        // 距底部 48px 内视为贴底; 用户向上翻阅历史时自动暂停跟随
        var distance = s.Extent.Height - s.Offset.Y - s.Viewport.Height;
        _atBottom = distance <= 48;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is ChatPageViewModel vm)
        {
            AttachTail(vm);
        }

        // 新消息到达强制回到底部跟随
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            _atBottom = true;
        }

        ScheduleScroll();
    }

    /// <summary>切换尾部消息订阅: 流式输出只改尾部消息的分段, 外层集合不变。</summary>
    private void AttachTail(ChatPageViewModel vm)
    {
        var tail = vm.Messages.Count > 0 ? vm.Messages[^1] : null;
        if (ReferenceEquals(tail, _tailItem)) return;

        if (_tailItem is not null)
        {
            _tailItem.Segments.CollectionChanged -= OnTailSegmentsChanged;
            foreach (var seg in _tailItem.Segments)
            {
                seg.PropertyChanged -= OnSegmentPropChanged;
            }
        }

        _tailItem = tail;

        if (_tailItem is not null)
        {
            _tailItem.Segments.CollectionChanged += OnTailSegmentsChanged;
            foreach (var seg in _tailItem.Segments)
            {
                seg.PropertyChanged += OnSegmentPropChanged;
            }
        }
    }

    private void OnTailSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 分段内容(正文/思考/工具输出)增长只触发分段 VM 的 PropertyChanged, 需逐个订阅
        if (e.NewItems is not null)
        {
            foreach (SegmentItemViewModel seg in e.NewItems)
            {
                seg.PropertyChanged += OnSegmentPropChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (SegmentItemViewModel seg in e.OldItems)
            {
                seg.PropertyChanged -= OnSegmentPropChanged;
            }
        }

        ScheduleScroll();
    }

    private void OnSegmentPropChanged(object? sender, PropertyChangedEventArgs e) => ScheduleScroll();

    /// <summary>渲染优先级延后滚动, 确保新增分段/文本增长已完成布局测量。</summary>
    private void ScheduleScroll()
    {
        if (_scrollScheduled) return;
        _scrollScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollScheduled = false;
            if (!_atBottom) return;
            if (_tailItem is not null)
            {
                MessageList.ScrollIntoView(_tailItem);
            }

            // 布局完成后再贴底一次: 尾部项首次 realize 时 extent 估算可能偏小
            Dispatcher.UIThread.Post(() =>
            {
                if (_atBottom) _listScroller?.ScrollToEnd();
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Render);
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
            return;
        }

        // Bash 风格输入历史: ↑ 上一条 / ↓ 下一条 / ESC 取消恢复草稿
        if (sender is not TextBox box || DataContext is not ChatPageViewModel v) return;

        switch (e.Key)
        {
            case Key.Up when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (v.HistoryPrevious())
                {
                    box.CaretIndex = box.Text?.Length ?? 0;
                }

                e.Handled = true;
                break;
            case Key.Down when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                if (v.HistoryNext())
                {
                    box.CaretIndex = box.Text?.Length ?? 0;
                }

                e.Handled = true;
                break;
            case Key.Escape when v.IsBrowsingHistory:
                v.HistoryCancel();
                e.Handled = true;
                break;
        }
    }

    /// <summary>气泡内确认压缩上下文: 关气泡后执行压缩命令。</summary>
    private void OnContextCompactConfirm(object? sender, RoutedEventArgs e)
    {
        HideContextFlyout();
        if (DataContext is ChatPageViewModel vm)
        {
            vm.CompactContextCommand.Execute(null);
        }
    }

    private void OnContextCompactCancel(object? sender, RoutedEventArgs e) => HideContextFlyout();

    private void HideContextFlyout()
    {
        if (ContextRingButton.Flyout is { } flyout)
        {
            flyout.Hide();
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

    /// <summary>回形针按钮: 选择图片文件加入待发送附件。</summary>
    private async void OnAttachImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Chat_AttachImage,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"]
                }
            ]
        });

        var paths = files.Select(f => f.TryGetLocalPath())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Cast<string>();
        await vm.AddImageFilesAsync(paths);
    }

    private void OnInputDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? e.DragEffects & DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>拖放图片文件到输入区加入待发送附件。</summary>
    private async void OnInputDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;
        if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p) && ImageAttachmentService.IsSupportedImage(p!))
            .Cast<string>();
        await vm.AddImageFilesAsync(paths);
    }

    /// <summary>输入框 Ctrl+V(隧道阶段, 先于默认粘贴): 剪贴板含图片/文件时转为附件并拦截, 否则放行文本粘贴。</summary>
    private async void OnInputPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not ChatPageViewModel vm || vm.IsSending) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            // 文件优先(资源管理器复制的图片文件), 其次位图(截图工具)
            var files = await clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                var paths = files
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => !string.IsNullOrEmpty(p) && ImageAttachmentService.IsSupportedImage(p!))
                    .Cast<string>()
                    .ToList();
                if (paths.Count > 0)
                {
                    await vm.AddImageFilesAsync(paths);
                    e.Handled = true;
                    return;
                }
            }

            var bmp = await clipboard.TryGetBitmapAsync();
            if (bmp is not null)
            {
                vm.AddImageFromBitmap(bmp, "clipboard-image");
                e.Handled = true;
            }
        }
        catch
        {
            // 剪贴板不可用/格式异常: 不拦截, 回退默认文本粘贴
        }
    }
}
