using System.Collections.Specialized;
using System.Text.Json;
using AIShikikan.Core.Models;
using AIShikikan.Gui.Resources;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

public enum ToolStatusKind
{
    Running,
    Success,
    Error
}

/// <summary>单条消息的显示模型。</summary>
/// <remarks>
/// 持有分段(可能含解码出的图片位图)的所有权。消息列表被整体换掉/清空时, 外层应对旧列表里的
/// 每个实例调用 <see cref="Deactivate"/>(或 <see cref="Dispose"/>) 归还原生内存。
/// </remarks>
public partial class ChatItemViewModel : ViewModelBase, IDisposable
{
    public ChatItemViewModel(MessageRole role)
    {
        Role = role;
        // 分段被移除/清空时同步归还其位图
        Segments.CollectionChanged += OnSegmentsCollectionChanged;
    }

    public MessageRole Role { get; }

    /// <summary>持久化的消息 Id(历史重建时赋值; 流式期间的临时分段为 null)。</summary>
    public string? MessageId { get; private set; }

    /// <summary>消息关联的自动检查点 ID(用户消息发送前由 Core 标记)。</summary>
    public string? CheckpointId { get; private set; }

    public bool HasCheckpoint => !string.IsNullOrWhiteSpace(CheckpointId);

    public void SetCheckpointId(string? checkpointId)
    {
        CheckpointId = checkpointId;
        OnPropertyChanged(nameof(HasCheckpoint));
    }

    public ObservableRange<SegmentItemViewModel> Segments { get; } = [];

    // 已物化、需随本消息一起释放的分段。
    // 自行记账而不是直接遍历 Segments: ObservableRange.Clear() 之后旧项已无从枚举,
    // 有了这份快照, Reset 与 Deactivate 才能拿到完整集合。
    private readonly HashSet<SegmentItemViewModel> _ownedSegments = [];

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
            case NotifyCollectionChangedAction.Replace:
                TrackSegments(e.NewItems);
                ReleaseSegments(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                ReleaseSegments(e.OldItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                // Clear(): 旧项不在集合里, 只能靠记账的快照
                foreach (var seg in _ownedSegments) seg.Dispose();
                _ownedSegments.Clear();
                break;
            // Move 的 NewItems/OldItems 是同一个元素, 不能当作替换处理
        }
    }

    private void TrackSegments(System.Collections.IList? items)
    {
        if (items is null) return;

        foreach (SegmentItemViewModel seg in items) _ownedSegments.Add(seg);
    }

    private void ReleaseSegments(System.Collections.IList? items)
    {
        if (items is null) return;

        foreach (SegmentItemViewModel seg in items)
        {
            if (_ownedSegments.Remove(seg)) seg.Dispose();
        }
    }

    /// <summary>归还原生内存并阻止后续懒物化: 本消息已确定不会再被显示时由外层调用
    /// (消息列表整体换实例/清空时, 对旧列表里的每个消息项调用)。</summary>
    /// <remarks>
    /// 调用时机要求: 该消息项必须已离开可见区域, 否则缩略图会变空白。
    /// </remarks>
    public void Deactivate()
    {
        // 阻断懒物化: 已释放的分段不应在再次访问 Segments 时被重新构建出来
        _pendingSource = null;
        foreach (var seg in _ownedSegments) seg.Dispose();
        _ownedSegments.Clear();
    }

    public void Dispose()
    {
        Segments.CollectionChanged -= OnSegmentsCollectionChanged;
        Deactivate();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 兜底终结器: 显式 <see cref="Deactivate"/>/<see cref="Dispose"/> 才是主路径。
    /// 这一层是必需的: 父消息项只要还被引用, 其分段就不会变成垃圾, 分段自己的终结器
    /// 永远等不到 —— 而外层消息列表换实例(会话切换)时旧消息项没有 Dispose 时机。
    /// </summary>
    /// <remarks>
    /// 这里只断开引用而不直接释放: 消息项随即整体成为垃圾, 各分段由自己的终结器释放。
    /// 终结器线程上不去碰 Dispatcher(会取其内部锁), 也不去释放可能仍在合成的位图。
    /// </remarks>
    ~ChatItemViewModel()
    {
        _ownedSegments.Clear();
    }

    // 懒加载: 历史消息的分段 VM 延迟到首次访问(即容器被 realized)时才构建;
    // 配合外层虚拟化, 屏幕外的消息完全不构建分段。
    private ChatMessage? _pendingSource;

    public bool IsUser => Role == MessageRole.User;

    /// <summary>用户消息正文(用户消息仅含单个文本分段)。</summary>
    public string UserBody
    {
        get
        {
            MaterializeSegments();
            var seg = Segments.FirstOrDefault(s => s.Kind == MessageSegmentKind.Text);
            return seg?.BodyContent ?? string.Empty;
        }
    }

    /// <summary>用户消息附带的图片分段(气泡内缩略图展示)。</summary>
    public IReadOnlyList<SegmentItemViewModel> UserImageSegments
    {
        get
        {
            MaterializeSegments();
            return Segments.Where(s => s.Kind == MessageSegmentKind.Image).ToList();
        }
    }

    /// <summary>用户消息是否含图片附件。</summary>
    public bool HasUserImages
    {
        get
        {
            MaterializeSegments();
            return Segments.Any(s => s.Kind == MessageSegmentKind.Image);
        }
    }

    public static ChatItemViewModel From(ChatMessage m)
    {
        // 不在此处构建分段 VM, 仅记录源消息; 视图绑定 Segments/UserBody 时再物化
        return new ChatItemViewModel(m.Role)
        {
            MessageId = m.Id,
            CheckpointId = m.CheckpointId,
            _pendingSource = m
        };
    }

    /// <summary>首次访问分段集合时物化延迟的历史分段。</summary>
    public void MaterializeSegments()
    {
        var src = _pendingSource;
        if (src is null) return;
        _pendingSource = null;

        foreach (var seg in src.Segments)
        {
            Segments.Add(SegmentItemViewModel.From(seg));
        }
    }

    /// <summary>视图绑定入口: 访问即物化(容器 realized 时才会绑定到这里)。</summary>
    public ObservableRange<SegmentItemViewModel> VisibleSegments
    {
        get
        {
            MaterializeSegments();
            return Segments;
        }
    }

    /// <summary>fork 编辑确认后原地更新用户消息正文(避免整列表重建触发容器回收级联)。</summary>
    public void SetUserBodyInPlace(string text)
    {
        MaterializeSegments();
        var seg = Segments.FirstOrDefault(s => s.Kind == MessageSegmentKind.Text);
        if (seg is not null)
        {
            seg.SetBody(text);
        }
        else
        {
            Segments.Add(SegmentItemViewModel.From(new MessageSegment
            {
                Kind = MessageSegmentKind.Text, Content = text
            }));
        }
    }

    // ---- 用户消息: fork 编辑 / 删除(两段式确认) ----
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    [ObservableProperty]
    private bool _isDeleteConfirming;

    public string DeleteText => IsDeleteConfirming ? Strings.Chat_MsgDeleteConfirm : Strings.Chat_MsgDelete;

    partial void OnIsDeleteConfirmingChanged(bool value) => OnPropertyChanged(nameof(DeleteText));

    public void BeginEdit(string text)
    {
        IsDeleteConfirming = false;
        EditText = text;
        IsEditing = true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditText = string.Empty;
    }

    /// <summary>结束编辑并返回编辑后文本(空文本返回 null 且保持编辑态交由调用方处理)。</summary>
    public string? ConfirmEdit()
    {
        var text = EditText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        IsEditing = false;
        EditText = string.Empty;
        return text;
    }
}

/// <summary>一条消息中的一个分段(正文 markdown / 思考卡片 / 工具调用卡片)。</summary>
public partial class SegmentItemViewModel : ViewModelBase, IDisposable
{
    private static int s_toolSeq;

    private readonly int _toolIndex;
    private string? _thinkingContent;
    private string? _bodyContent;

    public SegmentItemViewModel(MessageSegmentKind kind)
    {
        Kind = kind;
        if (kind == MessageSegmentKind.Tool)
        {
            _toolIndex = ++s_toolSeq;
        }
    }

    public MessageSegmentKind Kind { get; }

    public bool IsText => Kind == MessageSegmentKind.Text;
    public bool IsThinking => Kind == MessageSegmentKind.Thinking;
    public bool IsTool => Kind == MessageSegmentKind.Tool;
    public bool IsImage => Kind == MessageSegmentKind.Image;
    public bool IsCard => Kind is MessageSegmentKind.Thinking or MessageSegmentKind.Tool;

    /// <summary>图片分段: 解码后的位图(供气泡缩略图)。</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _imageBitmap;

    // 本分段"拥有所有权"的位图: 赋值即接管, 被替换/被释放时归还。
    // 独立于生成属性的后备字段, 便于用 Interlocked 一次性交接所有权 ——
    // 显式 Dispose 与终结器可能先后到达, 只能有一个赢家, 否则重复释放原生句柄。
    private Avalonia.Media.Imaging.Bitmap? _ownedBitmap;

    partial void OnImageBitmapChanged(Avalonia.Media.Imaging.Bitmap? value)
    {
        // 同一实例被重复赋值: 不是替换, 不动所有权
        if (ReferenceEquals(_ownedBitmap, value)) return;

        var old = _ownedBitmap;
        _ownedBitmap = value;
        if (old is not null) ReleaseDetached(old);
    }

    /// <summary>归还一张已与绑定解耦的位图。
    /// 换绑(<see cref="ImageBitmap"/> 置空/换实例)是同步的, 但当前合成帧可能仍持有旧位图,
    /// 故延后一帧再释放, 避免渲染线程拿到已释放的原生句柄。</summary>
    private static void ReleaseDetached(Avalonia.Media.Imaging.Bitmap bitmap) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            bitmap.Dispose, Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>释放本分段持有的缩略图位图(所属消息被丢弃 / 分段被移出集合时由 ChatItemViewModel 调用)。</summary>
    public void Dispose()
    {
        var bitmap = Interlocked.Exchange(ref _ownedBitmap, null);
        if (bitmap is null) return;

        // 先清空绑定让 Image.Source 立即脱离该位图, 再延后一帧释放
        if (ReferenceEquals(ImageBitmap, bitmap))
        {
            ImageBitmap = null;
        }

        ReleaseDetached(bitmap);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 兜底终结器: 显式 <see cref="Dispose"/> 才是主路径, 这里只保证原生句柄最终归还。
    /// 取舍: 托管资源不该在终结器里 Dispose, 但位图持有的是非托管内存; 而消息列表
    /// 整体换实例(会话切换)时旧 ChatItemViewModel 直接变成垃圾, 没有任何 Dispose 时机 ——
    /// 没有这层兜底, 图片分段会随会话切换持续泄漏。
    /// </summary>
    ~SegmentItemViewModel()
    {
        // 终结器线程上释放是最后手段(正常路径都已走 Dispose)
        Interlocked.Exchange(ref _ownedBitmap, null)?.Dispose();
    }

    /// <summary>图片分段: 原始文件名(悬浮提示)。</summary>
    [ObservableProperty]
    private string _imageName = string.Empty;

    public string ArgumentsToggleText => IsArgumentsJsonView ? "Markdown" : "JSON";

    public static SegmentItemViewModel From(MessageSegment seg)
    {
        var vm = new SegmentItemViewModel(seg.Kind);
        switch (seg.Kind)
        {
            case MessageSegmentKind.Text:
                vm.SetBody(seg.Content);
                break;
            case MessageSegmentKind.Thinking:
                vm.AppendThinking(seg.Content);
                break;
            case MessageSegmentKind.Tool when seg.Tool is { } t:
                vm.ToolName = t.Name;
                vm.ArgumentsRaw = t.Arguments;
                vm.SetToolOutput(string.Join("\n", t.OutputLines));
                vm.ToolResult = t.Result;
                vm.IsToolDone = t.IsDone;
                vm.ToolStatus = t.IsError ? ToolStatusKind.Error : ToolStatusKind.Success;
                vm.CheckpointId = t.CheckpointId;
                vm.StepId = t.StepId;
                vm.ToolCardDetail = t.Detail;
                break;
            case MessageSegmentKind.Image:
                vm.ImageName = seg.ImageName ?? string.Empty;
                try
                {
                    if (!string.IsNullOrEmpty(seg.ImageData))
                    {
                        vm.ImageBitmap = new Avalonia.Media.Imaging.Bitmap(
                            new System.IO.MemoryStream(System.Convert.FromBase64String(seg.ImageData)));
                    }
                }
                catch
                {
                    // 解码失败: 气泡显示占位(无位图)
                }

                break;
        }

        return vm;
    }

    public void SetToolOutput(string value)
    {
        ToolOutput = value;
    }

    [ObservableProperty]
    private bool _isExpanded;

    // ---- 正文(Text) ----
    public string BodyContent => _bodyContent ?? string.Empty;

    // ---- 思考(Thinking) ----
    public bool HasThinking => !string.IsNullOrEmpty(_thinkingContent);

    public string ThinkingContent => _thinkingContent ?? string.Empty;

    // ---- 工具(Tool) ----
    [ObservableProperty]
    private string _argumentsRaw = string.Empty;

    [ObservableProperty]
    private bool _isArgumentsJsonView;

    [ObservableProperty]
    private string _toolOutput = string.Empty;

    [ObservableProperty]
    private string _toolResult = string.Empty;

    [ObservableProperty]
    private ToolStatusKind _toolStatus;

    [ObservableProperty]
    private bool _isToolDone;

    public string ToolName
    {
        get => _toolName;
        set
        {
            _toolName = value;
            OnPropertyChanged(nameof(CardTitle));
        }
    }

    private string _toolName = string.Empty;

    /// <summary>结构化卡片展示数据(按工具类型渲染专属卡体), 为空时回退通用文本展示。</summary>
    [ObservableProperty]
    private ToolCardDetail? _toolCardDetail;

    /// <summary>结构化数据分类视图(XAML 按 HasXxx 切换卡体)。</summary>
    public FileReadDetail? FileRead => ToolCardDetail as FileReadDetail;

    public DirectoryListDetail? DirectoryList => ToolCardDetail as DirectoryListDetail;

    public GlobDetail? Glob => ToolCardDetail as GlobDetail;

    public GrepDetail? Grep => ToolCardDetail as GrepDetail;

    public SubagentsDetail? Subagents => ToolCardDetail as SubagentsDetail;

    public CheckpointDetail? Checkpoint => ToolCardDetail as CheckpointDetail;

    public bool HasFileRead => FileRead is not null;
    public bool HasDirectoryList => DirectoryList is not null;
    public bool HasGlob => Glob is not null;
    public bool HasGrep => Grep is not null;
    public bool HasSubagents => Subagents is not null;
    public bool HasCheckpoint => Checkpoint is not null;
    public bool CanUseCheckpoint => HasCheckpoint && IsToolDone && !IsCheckpointRolledBack;
    public bool HasNoDetail => ToolCardDetail is null;

    public string CheckpointTagText => Checkpoint is null
        ? string.Empty
        : $"ai-shikikan/checkpoint/{Checkpoint.CheckpointId}";

    public string CheckpointSourceText => Checkpoint?.Source switch
    {
        GitCheckpointSource.AutoUserMessage => Strings.Checkpoint_SourceAuto,
        GitCheckpointSource.AiTool => Strings.Checkpoint_SourceAi,
        GitCheckpointSource.Manual => Strings.Checkpoint_SourceManual,
        _ => Strings.Checkpoint_SourceUnknown
    };

    [ObservableProperty]
    private bool _isCheckpointRolledBack;

    [ObservableProperty]
    private string? _checkpointActionMessage;

    public bool HasCheckpointActionMessage => !string.IsNullOrWhiteSpace(CheckpointActionMessage);

    public void MarkCheckpointRolledBack(CheckpointRollbackMode mode)
    {
        IsCheckpointRolledBack = true;
        CheckpointActionMessage = mode == CheckpointRollbackMode.ResetHard
            ? Strings.Checkpoint_ResetDone
            : Strings.Checkpoint_RevertDone;
    }

    public void SetCheckpointActionError(string message)
    {
        CheckpointActionMessage = string.Format(Strings.Checkpoint_ActionFailed, message);
    }

    public void SetCheckpointActionSuccess(string message)
    {
        CheckpointActionMessage = message;
    }

    partial void OnIsCheckpointRolledBackChanged(bool value) => OnPropertyChanged(nameof(CanUseCheckpoint));

    partial void OnCheckpointActionMessageChanged(string? value) => OnPropertyChanged(nameof(HasCheckpointActionMessage));

    public string FileReadMeta => FileRead is null
        ? string.Empty
        : string.Format(Strings.ToolCard_FileMeta, FileRead.TotalLines, FileRead.StartLine,
            FileRead.StartLine + FileRead.LinesShown - 1);

    public string GlobQueryText => Glob is null ? string.Empty : string.Format(Strings.ToolCard_Query, Glob.Pattern);

    public string GlobCountText => Glob is null ? string.Empty : string.Format(Strings.ToolCard_MatchCount, Glob.Matches.Count);

    public string GrepQueryText => Grep is null ? string.Empty : string.Format(Strings.ToolCard_Query, Grep.Pattern);

    public string GrepCountText => Grep is null ? string.Empty : string.Format(Strings.ToolCard_MatchCount, Grep.Matches.Count);

    partial void OnToolCardDetailChanged(ToolCardDetail? value)
    {
        OnPropertyChanged(nameof(FileRead));
        OnPropertyChanged(nameof(DirectoryList));
        OnPropertyChanged(nameof(Glob));
        OnPropertyChanged(nameof(Grep));
        OnPropertyChanged(nameof(Subagents));
        OnPropertyChanged(nameof(Checkpoint));
        OnPropertyChanged(nameof(HasFileRead));
        OnPropertyChanged(nameof(HasDirectoryList));
        OnPropertyChanged(nameof(HasGlob));
        OnPropertyChanged(nameof(HasGrep));
        OnPropertyChanged(nameof(HasSubagents));
        OnPropertyChanged(nameof(HasCheckpoint));
        OnPropertyChanged(nameof(CanUseCheckpoint));
        OnPropertyChanged(nameof(HasNoDetail));
        OnPropertyChanged(nameof(CheckpointTagText));
        OnPropertyChanged(nameof(CheckpointSourceText));
        OnPropertyChanged(nameof(CardTitle));
        if (value is CheckpointDetail) IsExpanded = true;
        OnPropertyChanged(nameof(FileReadMeta));
        OnPropertyChanged(nameof(GlobQueryText));
        OnPropertyChanged(nameof(GlobCountText));
        OnPropertyChanged(nameof(GrepQueryText));
        OnPropertyChanged(nameof(GrepCountText));
    }

    /// <summary>状态指示: 三态(运行中/成功/失败)由 UI 渲染为彩色圆点 + 文本。</summary>
    public bool IsStatusRunning => !IsToolDone;
    public bool IsStatusSuccess => IsToolDone && ToolStatus == ToolStatusKind.Success;
    public bool IsStatusError => IsToolDone && ToolStatus == ToolStatusKind.Error;

    public string StatusText => IsStatusRunning
        ? Strings.ToolCard_StatusRunning
        : IsStatusError ? Strings.ToolCard_StatusError : Strings.ToolCard_StatusSuccess;

    /// <summary>关联的新检查点 ID。</summary>
    [ObservableProperty]
    private string? _checkpointId;

    /// <summary>兼容旧步骤检查点的关联 ID(仅用于历史消息回填, 回滚入口已由检查点卡片承担)。</summary>
    [ObservableProperty]
    private string? _stepId;

    partial void OnIsToolDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseCheckpoint));
        OnPropertyChanged(nameof(IsStatusRunning));
        OnPropertyChanged(nameof(IsStatusSuccess));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(StatusText));
    }

    public string CardTitle => Kind == MessageSegmentKind.Thinking
        ? Strings.ToolCard_Thinking
        : HasCheckpoint ? Strings.ToolCard_CheckpointTitle
            : string.Format(Strings.ToolCard_Title, _toolIndex, ToolName);

    public string CardGlyph => Kind == MessageSegmentKind.Thinking
        ? "🪄"
        : ToolStatus switch
        {
            ToolStatusKind.Success => "✔",
            ToolStatusKind.Error => "✘",
            _ => "⏳"
        };

    /// <summary>参数展示文本: 默认整理为可读 Markdown, 可切换为 JSON 代码块。</summary>
    public string ArgumentsDisplay => IsArgumentsJsonView
        ? $"```json\n{ArgumentsRaw}\n```"
        : ArgsToReadableMarkdown(ArgumentsRaw);

    public bool HasToolResult => !string.IsNullOrEmpty(ToolResult);

    public bool HasToolOutput => !string.IsNullOrEmpty(ToolOutput);

    /// <summary>复制结果按钮: 复制后短暂显示"已复制"。</summary>
    [ObservableProperty]
    private bool _isCopied;

    public string CopyButtonText => IsCopied ? Strings.ToolCard_Copied : Strings.ToolCard_CopyResult;

    partial void OnIsCopiedChanged(bool value) => OnPropertyChanged(nameof(CopyButtonText));

    /// <summary>复制工具结果(优先最终结果, 其次流式输出)到剪贴板。</summary>
    [RelayCommand]
    private async Task CopyResultAsync()
    {
        var text = !string.IsNullOrEmpty(ToolResult) ? ToolResult : ToolOutput;
        if (string.IsNullOrEmpty(text)) return;

        // 超长结果截断, 避免剪贴板写入过大数据
        if (text.Length > 200_000)
        {
            text = text[..200_000];
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            && window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            IsCopied = true;
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                IsCopied = false;
                timer.Stop();
            };
            timer.Start();
        }
    }

    [RelayCommand]
    private void ToggleArgumentsView()
    {
        IsArgumentsJsonView = !IsArgumentsJsonView;
    }

    partial void OnIsArgumentsJsonViewChanged(bool value)
    {
        OnPropertyChanged(nameof(ArgumentsDisplay));
        OnPropertyChanged(nameof(ArgumentsToggleText));
    }

    partial void OnArgumentsRawChanged(string value) => OnPropertyChanged(nameof(ArgumentsDisplay));
    partial void OnToolStatusChanged(ToolStatusKind value)
    {
        OnPropertyChanged(nameof(CardGlyph));
        OnPropertyChanged(nameof(IsStatusSuccess));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(StatusText));
    }
    partial void OnToolOutputChanged(string value) => OnPropertyChanged(nameof(HasToolOutput));
    partial void OnToolResultChanged(string value) => OnPropertyChanged(nameof(HasToolResult));

    public void AppendBody(string text)
    {
        _bodyContent += text;
        OnPropertyChanged(nameof(BodyContent));
    }

    public void SetBody(string text)
    {
        _bodyContent = text;
        OnPropertyChanged(nameof(BodyContent));
    }

    public void AppendThinking(string text)
    {
        _thinkingContent += text;
        OnPropertyChanged(nameof(ThinkingContent));
        OnPropertyChanged(nameof(HasThinking));
    }

    public void SetThinking(string value)
    {
        _thinkingContent = value;
        OnPropertyChanged(nameof(ThinkingContent));
        OnPropertyChanged(nameof(HasThinking));
    }

    /// <summary>把扁平 JSON 参数整理为易读的 Markdown 列表/嵌套结构。</summary>
    private static string ArgsToReadableMarkdown(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "_无参数_";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return NodeToMarkdown(doc.RootElement, "  ");
        }
        catch
        {
            return $"```\n{json}\n```";
        }
    }

    private static string NodeToMarkdown(JsonElement el, string indent)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var p in el.EnumerateObject())
            {
                parts.Add($"{indent}- **{p.Name}**: {LeafOrNested(p.Value, indent + "  ")}");
            }

            return string.Join("\n", parts);
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                parts.Add($"{indent}- {LeafOrNested(item, indent + "  ")}");
            }

            return parts.Count == 0 ? "_(空数组)_" : string.Join("\n", parts);
        }

        return LeafOrNestedText(el);
    }

    private static string LeafOrNested(JsonElement el, string indent)
    {
        return el.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? "\n" + NodeToMarkdown(el, indent)
            : LeafOrNestedText(el);
    }

    private static string LeafOrNestedText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.True or JsonValueKind.False => el.GetBoolean().ToString().ToLowerInvariant(),
        JsonValueKind.Null => "null",
        JsonValueKind.Number => el.GetRawText(),
        _ => el.GetRawText()
    };
}

/// <summary>支持批量追加的原生 ObservableCollection 子类。</summary>
public class ObservableRange<T> : System.Collections.ObjectModel.ObservableCollection<T>
{
    public ObservableRange()
    {
    }

    public ObservableRange(IEnumerable<T> items) : base(items)
    {
    }

    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }
}