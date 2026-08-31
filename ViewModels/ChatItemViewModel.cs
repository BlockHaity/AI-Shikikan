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
public partial class ChatItemViewModel : ViewModelBase
{
    public ChatItemViewModel(MessageRole role)
    {
        Role = role;
    }

    public MessageRole Role { get; }

    /// <summary>持久化的消息 Id(历史重建时赋值; 流式期间的临时分段为 null)。</summary>
    public string? MessageId { get; private set; }

    public ObservableRange<SegmentItemViewModel> Segments { get; } = [];

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
        return new ChatItemViewModel(m.Role) { MessageId = m.Id, _pendingSource = m };
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
public partial class SegmentItemViewModel : ViewModelBase
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

    public bool HasFileRead => FileRead is not null;
    public bool HasDirectoryList => DirectoryList is not null;
    public bool HasGlob => Glob is not null;
    public bool HasGrep => Grep is not null;
    public bool HasSubagents => Subagents is not null;
    public bool HasNoDetail => ToolCardDetail is null;

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
        OnPropertyChanged(nameof(HasFileRead));
        OnPropertyChanged(nameof(HasDirectoryList));
        OnPropertyChanged(nameof(HasGlob));
        OnPropertyChanged(nameof(HasGrep));
        OnPropertyChanged(nameof(HasSubagents));
        OnPropertyChanged(nameof(HasNoDetail));
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

    /// <summary>关联的 git 检查点步骤 ID(非空时展示回滚按钮)。</summary>
    [ObservableProperty]
    private string? _stepId;

    /// <summary>回滚二次确认状态。</summary>
    [ObservableProperty]
    private bool _isRollbackConfirming;

    /// <summary>已成功回滚(按钮隐藏, 显示状态)。</summary>
    [ObservableProperty]
    private bool _isRolledBack;

    partial void OnStepIdChanged(string? value) => OnPropertyChanged(nameof(CanRollback));

    partial void OnIsToolDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(IsStatusRunning));
        OnPropertyChanged(nameof(IsStatusSuccess));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>回滚失败信息(空表示无错误)。</summary>
    [ObservableProperty]
    private string? _rollbackError;

    /// <summary>是否显示回滚按钮: 有关联检查点、未回滚过、工具已完成。</summary>
    public bool CanRollback => !string.IsNullOrEmpty(StepId) && !IsRolledBack && IsToolDone;

    /// <summary>回滚按钮文案: 二次确认阶段变为"确认回滚?"。</summary>
    public string RollbackText => IsRollbackConfirming ? Strings.ToolCard_RollbackConfirm : Strings.ToolCard_Rollback;

    /// <summary>回滚区提示文本: 已回滚 / 错误信息。</summary>
    public string? RollbackNote => IsRolledBack ? Strings.ToolCard_RolledBack : RollbackError;

    public bool HasRollbackNote => !string.IsNullOrEmpty(RollbackNote);

    /// <summary>回滚动作: 第一次点击进入确认态, 再次点击执行。</summary>
    [RelayCommand]
    private void Rollback()
    {
        if (!IsRollbackConfirming)
        {
            IsRollbackConfirming = true;
            return;
        }

        IsRollbackConfirming = false;
        if (string.IsNullOrEmpty(StepId))
        {
            return;
        }

        try
        {
            var git = AppShell.Instance.Runtime.Git;
            var result = git.RollbackStep(StepId);
            if (result.Succeeded)
            {
                IsRolledBack = true;
                AppShell.Instance.NotifyDataChanged(); // Git 面板等刷新
            }
            else
            {
                RollbackError = string.Format(Strings.ToolCard_RollbackFailed, result.Stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            // 常见: 工作区有未提交变更 / 步骤已回滚(消息已带前缀时避免重复)
            var prefix = Strings.ToolCard_RollbackFailed.Replace("{0}", string.Empty).TrimEnd();
            RollbackError = ex.Message.StartsWith(prefix, StringComparison.Ordinal)
                ? ex.Message
                : string.Format(Strings.ToolCard_RollbackFailed, ex.Message);
        }
    }

    partial void OnIsRollbackConfirmingChanged(bool value) => OnPropertyChanged(nameof(RollbackText));
    partial void OnIsRolledBackChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(RollbackNote));
        OnPropertyChanged(nameof(HasRollbackNote));
    }
    partial void OnRollbackErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(RollbackNote));
        OnPropertyChanged(nameof(HasRollbackNote));
    }

    public string CardTitle => Kind == MessageSegmentKind.Thinking
        ? Strings.ToolCard_Thinking
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