using System.Text.Json;
using AIShikikan.Core.Models;
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

    public ObservableRange<SegmentItemViewModel> Segments { get; } = [];

    public bool IsUser => Role == MessageRole.User;

    /// <summary>用户消息正文(用户消息仅含单个文本分段)。</summary>
    public string UserBody
    {
        get
        {
            var seg = Segments.FirstOrDefault(s => s.Kind == MessageSegmentKind.Text);
            return seg?.BodyContent ?? string.Empty;
        }
    }

    public static ChatItemViewModel From(ChatMessage m)
    {
        var item = new ChatItemViewModel(m.Role);
        foreach (var seg in m.Segments)
        {
            item.Segments.Add(SegmentItemViewModel.From(seg));
        }

        return item;
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
    public bool IsCard => Kind != MessageSegmentKind.Text;

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

    public string ToolName { get; set; } = string.Empty;

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
    partial void OnIsToolDoneChanged(bool value) => OnPropertyChanged(nameof(CanRollback));

    /// <summary>回滚失败信息(空表示无错误)。</summary>
    [ObservableProperty]
    private string? _rollbackError;

    /// <summary>是否显示回滚按钮: 有关联检查点、未回滚过、工具已完成。</summary>
    public bool CanRollback => !string.IsNullOrEmpty(StepId) && !IsRolledBack && IsToolDone;

    /// <summary>回滚按钮文案: 二次确认阶段变为"确认回滚?"。</summary>
    public string RollbackText => IsRollbackConfirming ? "确认回滚?" : "回滚此检查点";

    /// <summary>回滚区提示文本: 已回滚 / 错误信息。</summary>
    public string? RollbackNote => IsRolledBack ? "已回滚到检查点之前" : RollbackError;

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
                RollbackError = $"回滚失败: {result.Stderr.Trim()}";
            }
        }
        catch (Exception ex)
        {
            // 常见: 工作区有未提交变更 / 步骤已回滚
            RollbackError = ex.Message.StartsWith("回滚失败") ? ex.Message : $"回滚失败: {ex.Message}";
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
        ? "思考过程"
        : $"工具调用 {_toolIndex} · {ToolName}";

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
    partial void OnToolStatusChanged(ToolStatusKind value) => OnPropertyChanged(nameof(CardGlyph));
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