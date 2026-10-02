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
    // VM 的 PropertyChanged 处理器必须持有引用: ViewLocator 每次切页都会 new ChatPageView(),
    // 而 ChatPageViewModel 是长生命周期的单例 —— 用匿名 lambda 订阅就永远解绑不掉,
    // 反复进出聊天页会在 VM 上堆积 N 个处理器(各自持有已废弃的 view)→ view 泄漏。
    private PropertyChangedEventHandler? _vmPropertyChanged;
    // 已订阅的 VM(DataContext 换掉后靠它摘旧订阅; base.OnDataContextChanged 调用后 DataContext 已是新值)
    private ChatPageViewModel? _boundVm;

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
        // DataContext 在 base 调用后已是**新** VM, 故必须靠 _boundVm 摘旧订阅再重挂。
        DetachFromViewModel();
        AttachToViewModel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 兜底重挂: 少数宿主场景会"脱离 → 重新挂回"同一个 view 实例, 此时 OnDataContextChanged
        // 不会再触发, 不补挂的话视图会静默地不再响应 VM 事件。
        AttachToViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 离开视觉树即视为可能被丢弃(ViewLocator 每次切页都 new 一个 view):
        // 立即解绑, 不等 GC 找机会 —— 否则长生命周期的 ChatPageViewModel 会一直持有指向旧 view 的委托。
        DetachFromViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>把本 view 挂到当前 DataContext 上(幂等: 已挂在同一个 VM 上则无操作)。</summary>
    private void AttachToViewModel()
    {
        if (DataContext is not ChatPageViewModel vm || ReferenceEquals(vm, _boundVm)) return;

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

        _vmPropertyChanged = (_, args) =>
        {
            // 流式期间在同一个集合上 Add, 仅靠 PropertyChanged 不够; 集合变化也要触发
            if (args.PropertyName == nameof(ChatPageViewModel.Messages))
            {
                AttachMessages();
                ScheduleScroll();
            }
        };
        _boundVm = vm;
        vm.PropertyChanged += _vmPropertyChanged;
        AttachMessages();
    }

    /// <summary>
    /// 从本 view 订阅过的 VM 上摘掉全部订阅(属性、消息集合、尾部分段)。
    /// 视图被丢弃前必须调用, 否则长生命周期的 ChatPageViewModel 会一直持有指向旧 view 的委托。
    /// </summary>
    private void DetachFromViewModel()
    {
        if (_boundVm is { } vm && _vmPropertyChanged is not null)
        {
            vm.PropertyChanged -= _vmPropertyChanged;
        }

        _boundVm = null;
        _vmPropertyChanged = null;
        if (_messagesCollection is not null)
        {
            _messagesCollection.CollectionChanged -= OnMessagesChanged;
            _messagesCollection = null;
        }

        DetachTail();
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

        // 旧尾部已被移出集合时必须解绑: 下面的 ReferenceEquals 早退只能覆盖"切到另一个尾部",
        // 覆盖不了"旧尾部消失"这条路径(RemoveMessagesAfter / DeleteUserMessage / 整集合替换),
        // 那些情况下旧分段仍持有指向本 view 的委托 → view 泄漏。
        if (_tailItem is not null && !ReferenceEquals(_tailItem, tail) && !vm.Messages.Contains(_tailItem))
        {
            DetachTail();
        }

        if (ReferenceEquals(tail, _tailItem)) return;

        DetachTail();
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

    /// <summary>解除尾部消息与其全部分段的订阅(必须与 AttachTail 成对)。</summary>
    private void DetachTail()
    {
        if (_tailItem is not null)
        {
            _tailItem.Segments.CollectionChanged -= OnTailSegmentsChanged;
            foreach (var seg in _tailItem.Segments)
            {
                seg.PropertyChanged -= OnSegmentPropChanged;
            }
        }

        _tailItem = null;
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

    // ⚠ 已知取舍(D10, 勿改): 分段任意属性变化都会排一次滚动, _scrollScheduled 只去重到同一 tick。
    // 分段在流式期间每 ~60ms 就整体刷新一次, 若改成按属性过滤(如只关心 BodyContent),
    // 会漏掉工具卡展开/折叠等引起的高度变化, 贴底跟随会失准 —— 滚动行为对聊天页观感敏感, 保持现状。
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
                // AddImageFromBitmap 不接管所有权(它只读来降采样/编码, 附件自带副本),
                // 所以释放责任在调用方。附件已满时它会直接 early-return, 那时 bmp 更是
                // 完全没有引用者 —— 不释放就是纯泄漏, 且是粘贴路径上最容易发生的那种。
                try
                {
                    vm.AddImageFromBitmap(bmp, "clipboard-image");
                    e.Handled = true;
                }
                finally
                {
                    bmp.Dispose();
                }
            }
        }
        catch
        {
            // 剪贴板不可用/格式异常: 不拦截, 回退默认文本粘贴
        }
    }
}
