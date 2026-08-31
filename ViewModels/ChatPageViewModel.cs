using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

public enum RightPanelMode
{
    Assignment,
    Git,
    Status
}

public partial class ChatPageViewModel : ViewModelBase
{
    private readonly ChatService _chatService;
    private readonly CommanderRuntime _runtime;
    private string? _currentSessionId;

    public ThemeService ThemeService { get; }

    [ObservableProperty]
    private bool _isRightPanelVisible = true;

    /// <summary>右侧栏开关联动 AI 工具列表: 关闭时移除子代理工具与 Roster 注入(下一回合生效), 打开时恢复。</summary>
    partial void OnIsRightPanelVisibleChanged(bool value)
    {
        _runtime.SetSubagentToolsVisible(value);
        if (value)
        {
            AgentPanel.PushRoster();
        }
    }

    [ObservableProperty]
    private RightPanelMode _panelMode = RightPanelMode.Assignment;

    [ObservableProperty]
    private IReadOnlyList<ChatSession> _sessions = [];

    [ObservableProperty]
    private ChatSession? _currentSession;

    /// <summary>显示层消息项(会话切换/Append 时更新)。</summary>
    [ObservableProperty]
    private ObservableRange<ChatItemViewModel> _messages = [];

    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>待发送的图片附件(输入区缩略图条带)。</summary>
    public ObservableCollection<PendingImageAttachment> PendingAttachments { get; } = [];

    /// <summary>是否存在待发送附件(控制附件条带可见性)。</summary>
    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    [ObservableProperty]
    private bool _isSending;

    /// <summary>手动终止当前回合的取消源。</summary>
    private CancellationTokenSource? _turnCts;

    /// <summary>上一轮被中断后可继续输出。</summary>
    [ObservableProperty]
    private bool _canContinue;

    [ObservableProperty]
    private string _activeModelText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ProviderConfig> _availableProviders = [];

    [ObservableProperty]
    private ProviderConfig? _selectedProvider;

    [ObservableProperty]
    private IReadOnlyList<string> _availableModels = [];

    [ObservableProperty]
    private string _selectedModel = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ThinkingLevel> _thinkingOptions = [ThinkingLevel.Auto, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High];

    [ObservableProperty]
    private ThinkingLevel _selectedThinking = ThinkingLevel.Auto;

    [ObservableProperty]
    private string _workDir = string.Empty;

    [ObservableProperty]
    private bool _isPlanMode;

    /// <summary>当前模式文案(Plan / Build), 用于单按钮显示。</summary>
    public string ModeLabel => IsPlanMode ? Strings.Chat_ModePlan : Strings.Chat_ModeBuild;

    public string ModeTip => IsPlanMode ? Strings.Chat_ModePlanTip : Strings.Chat_ModeBuildTip;

    partial void OnIsPlanModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ModeTip));
        // 联动子代理工具注册: Plan 模式下 AI 只能发现/调用开启"在 Plan 模式中使用"的子代理
        _runtime.SetPlanMode(value);
    }

    public AgentPanelViewModel AgentPanel { get; }

    public GitPanelViewModel GitPanel { get; }

    public StatusPanelViewModel StatusPanel { get; }

    public SessionPanelViewModel SessionPanel { get; }

    public bool IsAssignmentMode => PanelMode == RightPanelMode.Assignment;

    public bool IsGitMode => PanelMode == RightPanelMode.Git;

    public bool IsStatusMode => PanelMode == RightPanelMode.Status;

    public ChatPageViewModel(ThemeService themeService)
    {
        ThemeService = themeService;
        _chatService = new ChatService();
        _runtime = AppShell.Instance.Runtime;
        Sessions = _chatService.Sessions;
        CurrentSession = _chatService.CurrentSession;
        RefreshMessages();
        _currentSessionId = CurrentSession?.Id;

        AgentPanel = new AgentPanelViewModel();
        GitPanel = new GitPanelViewModel();
        StatusPanel = new StatusPanelViewModel();
        SessionPanel = new SessionPanelViewModel(_chatService);
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        StatusPanel.SetSession(_currentSessionId ?? string.Empty);

        _chatService.CurrentSessionChanged += (_, session) =>
        {
            CurrentSession = session;
            RefreshMessages();
            _currentSessionId = session?.Id;
            CanContinue = false;
            // 切换会话时恢复该会话绑定的工作目录(未绑定的新会话保留当前选择)
            if (!string.IsNullOrWhiteSpace(session?.WorkDir))
            {
                WorkDir = session!.WorkDir;
            }
            AgentPanel.SetSession(_currentSessionId ?? string.Empty);
            StatusPanel.SetSession(_currentSessionId ?? string.Empty);
            RefreshContextUsage();
        };
        _chatService.MessageAdded += (_, msg) =>
        {
            if (IsSending) return; // 流式期间由 RespondAsync 维护, 避免重建列表

            // 增量追加优先: 整集合替换会大规模回收容器, 触发 Material 主题过渡 NRE
            if (CurrentSession is { } s && s.Messages.Count == Messages.Count + 1 &&
                s.Messages[^1].Id == msg.Id)
            {
                Messages.Add(ChatItemViewModel.From(msg));
            }
            else
            {
                RefreshMessages();
            }

            Sessions = _chatService.Sessions;
        };

        _runtime.Assignments.AssignmentChanged += _ =>
            Dispatcher.UIThread.Post(() => AgentPanel.RefreshAssignments());

        _runtime.Engine.OnEvent += OnEngineUsageRecorded;

        AppShell.Instance.DataChanged += OnShellDataChanged;

        RefreshActiveModel();
        RefreshProviders();
        RefreshThinkingOptions();
        RefreshContextUsage();
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var settings = _runtime.Llm.Settings;
        if (string.Equals(settings.ActiveModel, value, StringComparison.Ordinal)) return;

        settings.ActiveModel = value;
        AIShikikan.Core.Services.Llm.ProviderSettingsService.Save(settings);
        RefreshActiveModel();
        RefreshThinkingOptions();
        RefreshContextUsage(); // 上下文窗口总量随模型变化
    }

    partial void OnPanelModeChanged(RightPanelMode value)
    {
        OnPropertyChanged(nameof(IsAssignmentMode));
        OnPropertyChanged(nameof(IsGitMode));
        OnPropertyChanged(nameof(IsStatusMode));
        if (value == RightPanelMode.Git)
        {
            GitPanel.Refresh();
        }
    }

    private void RefreshActiveModel()
    {
        ActiveModelText = $"{_runtime.Llm.ResolveModel()} @ {_runtime.Llm.GetProvider()?.Id ?? "-"}";
    }

    /// <summary>思考按钮文案: 当前思考档位。</summary>
    public string SelectedThinkingText =>
        $"{Strings.Chat_Thinking}: {ThinkingLevels.DisplayName(SelectedThinking)}";

    partial void OnSelectedThinkingChanged(ThinkingLevel value)
    {
        OnPropertyChanged(nameof(SelectedThinkingText));
    }

    /// <summary>当前激活模型的思考等级上限。</summary>
    private ThinkingLevel CurrentMaxThinking()
    {
        var provider = _runtime.Llm.GetProvider();
        var model = _runtime.Llm.ResolveModel();
        return provider?.GetMaxThinking(model) ?? ThinkingLevel.Max;
    }

    /// <summary>按当前模型上限重建思考菜单(关闭/自动 + 不超过上限的档位), 并夹紧当前选中值。</summary>
    private void RefreshThinkingOptions()
    {
        var cap = CurrentMaxThinking();
        var options = new List<ThinkingLevel> { ThinkingLevel.Off, ThinkingLevel.Auto };
        for (var l = ThinkingLevel.Low; l <= cap; l++)
        {
            options.Add(l);
        }

        ThinkingOptions = options;
        if (SelectedThinking > cap)
        {
            SelectedThinking = cap;
        }

        OnPropertyChanged(nameof(SelectedThinkingText));
    }

    private void RefreshProviders()
    {
        var settings = _runtime.Llm.Settings;
        AvailableProviders = settings.Providers.ToList();

        var current = settings.ActiveProvider;
        SelectedProvider = AvailableProviders.FirstOrDefault(p =>
            p.Id.Equals(current?.Id, StringComparison.OrdinalIgnoreCase))
            ?? AvailableProviders.FirstOrDefault();
    }

    private void RefreshModels()
    {
        var settings = _runtime.Llm.Settings;
        var provider = SelectedProvider;
        if (provider is null)
        {
            AvailableModels = [];
            return;
        }

        var list = provider.EnabledModels is { Count: > 0 }
            ? provider.EnabledModels
            : [provider.DefaultModel];

        AvailableModels = list
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = !string.IsNullOrEmpty(settings.ActiveModel)
            ? settings.ActiveModel
            : provider.DefaultModel;
        SelectedModel = AvailableModels.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? AvailableModels.First(m => m.Equals(current, StringComparison.OrdinalIgnoreCase))
            : AvailableModels.FirstOrDefault() ?? string.Empty;

        if (!string.Equals(settings.ActiveModel, SelectedModel, StringComparison.Ordinal))
        {
            settings.ActiveModel = SelectedModel;
            ProviderSettingsService.Save(settings);
        }

        RefreshActiveModel();
    }

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        var settings = _runtime.Llm.Settings;
        if (value is not null &&
            !string.Equals(settings.ActiveProviderId, value.Id, StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveProviderId = value.Id;
            ProviderSettingsService.Save(settings);
            RefreshActiveModel();
        }

        RefreshModels();
        RefreshThinkingOptions();
    }

    private void OnShellDataChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshShellDataChanged);
            return;
        }

        RefreshShellDataChanged();
    }

    private void OnEngineUsageRecorded(AgentEngineEvent e)
    {
        if (e is EngineContextCompacted)
        {
            // 自动压缩发生: 圆环占用清零(ContextUsed 变化自带派生通知), 待下一次真实用量刷新
            Dispatcher.UIThread.Post(() => ContextUsed = 0);
            return;
        }

        // AI 反问(ask_user): 切 UI 线程弹输入框, 回答经 TCS 回传引擎
        if (e is EngineQuestionRequested question)
        {
            Dispatcher.UIThread.Post(() => _ = AnswerQuestionAsync(question));
            return;
        }

        // 工具审批: 弹确认框应答(此前 GUI 无订阅者会导致引擎永久挂起)
        if (e is EngineApprovalRequested approval)
        {
            Dispatcher.UIThread.Post(() => _ = DecideApprovalAsync(approval));
            return;
        }

        if (e is not EngineUsageRecorded usage) return;

        var title = CurrentSession?.DisplayTitle ?? "未知会话";
        UsageStatsService.RecordLlmUsage(
            _currentSessionId ?? "unknown", title,
            usage.Provider, usage.Model,
            usage.Usage.InputTokens, usage.Usage.OutputTokens,
            usage.Usage.CachedInputTokens);

        // 顶栏圆环: 每次记录用量后刷新上下文占用
        Dispatcher.UIThread.Post(RefreshContextUsage);
    }

    /// <summary>弹输入框回答 AI 提问; 主窗口不可用时以 null 结束(工具侧视为跳过)。</summary>
    private static async Task AnswerQuestionAsync(EngineQuestionRequested q)
    {
        var owner = GetMainWindow();
        if (owner is null)
        {
            q.UserAnswer.TrySetResult(null);
            return;
        }

        try
        {
            var answer = await Views.InputDialog.ShowAsync(
                owner, Strings.Chat_AskUserTitle, q.Question,
                Strings.Chat_AskUserPlaceholder, Strings.Chat_Send, Strings.Settings_Cancel);
            q.UserAnswer.TrySetResult(answer);
        }
        catch
        {
            q.UserAnswer.TrySetResult(null);
        }
    }

    /// <summary>弹确认框决定工具审批; 主窗口不可用时默认拒绝, 避免未经确认执行。</summary>
    private static async Task DecideApprovalAsync(EngineApprovalRequested a)
    {
        var owner = GetMainWindow();
        if (owner is null)
        {
            a.UserDecision.TrySetResult(false);
            return;
        }

        try
        {
            var args = a.Arguments.Length > 400 ? a.Arguments[..400] + "…" : a.Arguments;
            var approved = await Views.ConfirmDialog.ShowAsync(
                owner, Strings.Chat_ApprovalTitle,
                $"{a.ToolName}\n{args}",
                Strings.Chat_ApprovalApprove, Strings.Chat_ApprovalReject);
            a.UserDecision.TrySetResult(approved);
        }
        catch
        {
            a.UserDecision.TrySetResult(false);
        }
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
            ? w
            : null;

    // ---- 顶栏上下文占用圆环 ----

    [ObservableProperty]
    private long _contextUsed;

    [ObservableProperty]
    private long _contextTotal;

    /// <summary>当前上下文占用百分比(0-100)。</summary>
    public double ContextPercent => ContextTotal > 0
        ? Math.Clamp(ContextUsed * 100.0 / ContextTotal, 0, 100)
        : 0;

    /// <summary>圆环进度弧扫过角度(0-360)。</summary>
    public double ContextArcAngle => ContextPercent * 3.6;

    public string ContextPercentText => ContextTotal > 0 ? $"{ContextPercent:0.#}%" : "-";

    public string ContextDetailText => ContextTotal > 0
        ? $"{ContextUsed:N0} / {ContextTotal:N0} tokens ({ContextPercent:0.#}%)"
        : Strings.Chat_CtxNoInfo;

    [ObservableProperty]
    private bool _isCompacting;

    partial void OnContextUsedChanged(long value) => NotifyContextDerived();

    partial void OnContextTotalChanged(long value) => NotifyContextDerived();

    private void NotifyContextDerived()
    {
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextArcAngle));
        OnPropertyChanged(nameof(ContextPercentText));
        OnPropertyChanged(nameof(ContextDetailText));
    }

    /// <summary>刷新上下文占用: 总量优先取模型设置手配值, 回退模型档案; 已用取最近一次输入 token。</summary>
    private void RefreshContextUsage()
    {
        var model = _runtime.Llm.ResolveModel();
        var provider = _runtime.Llm.GetProvider();
        ContextTotal = provider?.GetContextTokens(model)
            ?? ModelProfileService.Resolve(model, provider?.Id).ContextTokens;
        ContextUsed = UsageStatsService.GetLastContextTokens(_currentSessionId ?? string.Empty);
    }

    /// <summary>压缩当前会话上下文: 历史较早部分经 LLM 摘要替换, 保留最近消息。</summary>
    [RelayCommand]
    private async Task CompactContextAsync()
    {
        if (IsCompacting || IsSending) return;
        IsCompacting = true;
        try
        {
            var compacted = await _runtime.Engine.CompactConversationAsync();
            // 增量追加系统提示条(避免整集合重建触发容器回收级联)
            AppendNotice(compacted ? Strings.Chat_CtxCompacted : Strings.Chat_CtxCompactShort);
            if (compacted)
            {
                // 压缩只影响引擎内部对话, 下一回合的 input_tokens 才能反映新占用, 此处先清零估算
                ContextUsed = 0;
            }
        }
        finally
        {
            IsCompacting = false;
        }
    }

    /// <summary>向消息时间线追加一条提示文本(助手样式, 不持久化)。</summary>
    private void AppendNotice(string text)
    {
        var item = new ChatItemViewModel(MessageRole.Assistant);
        item.Segments.Add(SegmentItemViewModel.From(new MessageSegment
        {
            Kind = MessageSegmentKind.Text,
            Content = text
        }));
        Messages.Add(item);
    }

    /// <summary>从当前会话消息重建显示层消息列表。</summary>
    private void RefreshMessages()
    {
        var items = CurrentSession?.Messages.Select(ChatItemViewModel.From)
                   ?? System.Linq.Enumerable.Empty<ChatItemViewModel>();
        Messages = new ObservableRange<ChatItemViewModel>(items);
    }

    private void RefreshShellDataChanged()
    {
        RefreshProviders();
        // 必须显式刷新模型列表: 设置页与聊天页共享同一 ProviderConfig 实例,
        // 引用相同不会触发 OnSelectedProviderChanged, 勾选/拉取的新模型否则不进下拉
        RefreshModels();
        RefreshThinkingOptions();
        RefreshContextUsage(); // 模型设置里的上下文窗口大小可能已变化
        AgentPanel.RefreshAll();
        GitPanel.Refresh();
        StatusPanel.Refresh();
    }

    [RelayCommand]
    private void OpenAssignmentPanel()
    {
        if (PanelMode == RightPanelMode.Assignment)
        {
            ToggleRightPanel();
        }
        else
        {
            PanelMode = RightPanelMode.Assignment;
            IsRightPanelVisible = true;
        }
    }

    [RelayCommand]
    private void OpenGitPanel()
    {
        if (PanelMode == RightPanelMode.Git)
        {
            ToggleRightPanel();
        }
        else
        {
            PanelMode = RightPanelMode.Git;
            IsRightPanelVisible = true;
        }
    }

    [RelayCommand]
    private void OpenStatusPanel()
    {
        if (PanelMode == RightPanelMode.Status)
        {
            ToggleRightPanel();
        }
        else
        {
            PanelMode = RightPanelMode.Status;
            IsRightPanelVisible = true;
            StatusPanel.Refresh();
        }
    }

    [RelayCommand]
    private void ToggleRightPanel()
    {
        IsRightPanelVisible = !IsRightPanelVisible;
    }

    [RelayCommand]
    private void NewSession()
    {
        _chatService.CreateSession();
        Sessions = _chatService.Sessions;
        RefreshMessages();
        _currentSessionId = CurrentSession?.Id;
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        StatusPanel.SetSession(_currentSessionId ?? string.Empty);
    }

    [RelayCommand]
    private void DeleteSession(string sessionId)
    {
        _chatService.DeleteSession(sessionId);
        AppShell.Instance.NotifyDataChanged(); // 主页会话分布移除该会话
        Sessions = _chatService.Sessions;
        RefreshMessages();
        _currentSessionId = CurrentSession?.Id;
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        StatusPanel.SetSession(_currentSessionId ?? string.Empty);
    }

    [RelayCommand]
    private void SwitchSession(string sessionId)
    {
        _chatService.SwitchSession(sessionId);
    }

    [RelayCommand]
    private void ClearMessages()
    {
        if (CurrentSession is null) return;
        _chatService.ClearMessages(CurrentSession.Id);
        RefreshMessages();
        _runtime.Engine.ClearConversation();
        CanContinue = false;
    }

    /// <summary>进入用户消息 fork 编辑态。</summary>
    [RelayCommand]
    private void StartEditUserMessage(ChatItemViewModel item)
    {
        if (IsSending || !item.IsUser) return;
        item.BeginEdit(item.UserBody);
    }

    [RelayCommand]
    private void CancelEditUserMessage(ChatItemViewModel item) => item.CancelEdit();

    /// <summary>确认 fork 编辑: 截断该消息之后的回复并更新文本, 引擎历史重建到该消息之前后重发。</summary>
    [RelayCommand]
    private async Task ConfirmEditUserMessageAsync(ChatItemViewModel item)
    {
        if (IsSending || !item.IsUser) return;
        var newText = item.ConfirmEdit();
        if (string.IsNullOrWhiteSpace(newText)) return;
        if (CurrentSession is null || string.IsNullOrEmpty(item.MessageId)) return;

        var messageId = item.MessageId;
        if (!_chatService.EditUserMessage(CurrentSession.Id, messageId, newText)) return;

        // 原地精准更新: 正文改文本 + 移除该消息之后的条目,
        // 不整集合替换(容器大规模回收级联会触发 Material 主题过渡 NRE)
        item.SetUserBodyInPlace(newText);
        RemoveMessagesAfter(item);
        Sessions = _chatService.Sessions;
        CanContinue = false;

        // 引擎历史重建到该消息之前, 新文本由本轮引擎调用追加(原消息的图片附件随本轮重发)
        var session = CurrentSession;
        var idx = session.Messages.FindIndex(m => m.Id == messageId);
        _runtime.Engine.RebuildConversation(session.Messages.Take(idx).ToList());

        var resendImages = session.Messages[idx].Segments
            .Where(s => s.Kind == MessageSegmentKind.Image && !string.IsNullOrEmpty(s.ImageData))
            .Select(s => new ChatImagePart { Base64Data = s.ImageData!, MimeType = s.ImageMimeType ?? "image/png" })
            .ToList();

        AIShikikan.Core.Logging.Log.Info("Session", $"fork 编辑消息 {messageId}, 截断后重发");
        IsSending = true;
        await RunTurnCoreAsync(newText, resendImages.Count > 0 ? resendImages : null);
    }

    /// <summary>删除用户消息(两段式确认): 连同其后紧跟的助手回复一并移除, 并重建引擎历史。</summary>
    [RelayCommand]
    private void DeleteUserMessage(ChatItemViewModel item)
    {
        if (IsSending || !item.IsUser) return;

        if (!item.IsDeleteConfirming)
        {
            item.IsDeleteConfirming = true;
            return;
        }

        item.IsDeleteConfirming = false;
        if (CurrentSession is null || string.IsNullOrEmpty(item.MessageId)) return;

        if (_chatService.DeleteMessage(CurrentSession.Id, item.MessageId))
        {
            AIShikikan.Core.Logging.Log.Info("Session", $"删除消息 {item.MessageId} 及其回复");

            // 原地精准移除(含其后回复), 避免整集合替换
            var pos = Messages.IndexOf(item);
            if (pos >= 0)
            {
                RemoveMessagesAfter(item);
                Messages.RemoveAt(pos);
            }

            Sessions = _chatService.Sessions;
            _runtime.Engine.RebuildConversation(CurrentSession.Messages);
            CanContinue = false;
        }
    }

    /// <summary>移除显示列表中该条目之后的全部消息(自尾向前逐项移除)。</summary>
    private void RemoveMessagesAfter(ChatItemViewModel item)
    {
        var pos = Messages.IndexOf(item);
        if (pos < 0) return;

        for (var i = Messages.Count - 1; i > pos; i--)
        {
            Messages.RemoveAt(i);
        }
    }

    // ---- 输入历史浏览(↑/↓, Bash 风格) ----

    [ObservableProperty]
    private bool _isBrowsingHistory;

    [ObservableProperty]
    private string _historyHint = string.Empty;

    private int _historyIndex = -1; // 浏览位置(-1 = 未在浏览)
    private string _historyDraft = string.Empty; // 浏览期间暂存的当前输入
    private bool _applyingHistory; // 程序写入 InputText 时抑制"用户编辑"判定

    /// <summary>输入框文本变化: 非程序化回填(即用户手动编辑)时结束浏览提示。</summary>
    partial void OnInputTextChanged(string value)
    {
        if (!_applyingHistory)
        {
            HistoryUserEdited();
        }
    }

    /// <summary>输入历史: 当前会话的用户消息(按时间顺序), 按需派生以与删除/编辑保持同步。</summary>
    private List<string> CurrentInputHistory()
    {
        return CurrentSession?.Messages
                   .Where(m => m.Role == MessageRole.User)
                   .Select(m => m.Segments.FirstOrDefault(s => s.Kind == MessageSegmentKind.Text)?.Content ?? string.Empty)
                   .Where(t => !string.IsNullOrWhiteSpace(t))
                   .ToList()
               ?? [];
    }

    /// <summary>↑ 调出上一条历史; 已是最早一条时给出边界反馈。</summary>
    public bool HistoryPrevious()
    {
        var hist = CurrentInputHistory();
        if (hist.Count == 0) return false;

        if (_historyIndex < 0)
        {
            _historyDraft = InputText; // 进入浏览前暂存当前输入
            _historyIndex = hist.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        else
        {
            HistoryHint = Strings.Chat_HistoryTop;
            return true;
        }

        ApplyHistoryEntry(hist);
        return true;
    }

    /// <summary>↓ 调出下一条历史; 越过最新一条时恢复暂存草稿并结束浏览。</summary>
    public bool HistoryNext()
    {
        if (_historyIndex < 0) return false;

        var hist = CurrentInputHistory();
        if (_historyIndex >= hist.Count - 1)
        {
            return HistoryCancel();
        }

        _historyIndex++;
        ApplyHistoryEntry(hist);
        return true;
    }

    private void ApplyHistoryEntry(List<string> hist)
    {
        if (hist.Count == 0)
        {
            HistoryReset();
            return;
        }

        if (_historyIndex >= hist.Count) _historyIndex = hist.Count - 1; // 消息被删除后钳制
        _applyingHistory = true;
        InputText = hist[_historyIndex];
        _applyingHistory = false;
        IsBrowsingHistory = true;
        HistoryHint = string.Format(Strings.Chat_HistoryPosition, _historyIndex + 1, hist.Count);
    }

    /// <summary>ESC 取消浏览, 恢复浏览前暂存的输入。</summary>
    public bool HistoryCancel()
    {
        if (_historyIndex < 0) return false;
        _applyingHistory = true;
        InputText = _historyDraft;
        _applyingHistory = false;
        HistoryReset();
        return true;
    }

    /// <summary>用户手动编辑输入: 结束浏览提示但保留历史位置(与 Bash 一致, 可继续 ↑/↓ 浏览)。</summary>
    public void HistoryUserEdited()
    {
        if (!IsBrowsingHistory) return;
        IsBrowsingHistory = false;
        HistoryHint = string.Empty;
    }

    /// <summary>结束浏览态并清理暂存(发送/取消后调用)。</summary>
    private void HistoryReset()
    {
        _historyIndex = -1;
        _historyDraft = string.Empty;
        IsBrowsingHistory = false;
        HistoryHint = string.Empty;
    }

    [RelayCommand]
    private void SendMessage()
    {
        var hasAttachments = PendingAttachments.Count > 0;
        if (string.IsNullOrWhiteSpace(InputText) && !hasAttachments) return;
        if (CurrentSession is null)
        {
            NewSession();
        }

        var content = InputText;
        InputText = string.Empty;
        HistoryReset(); // 发送后该条已进入会话历史, 退出浏览态

        // 附件转图片分段(与引擎侧 ChatImagePart 同源), 发送后清空待发送条带
        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();

        var isFirstMessage = CurrentSession!.MessageCount == 0;
        var segments = new List<MessageSegment>();
        foreach (var att in attachments)
        {
            segments.Add(new MessageSegment
            {
                Kind = MessageSegmentKind.Image,
                ImageData = att.Base64,
                ImageMimeType = att.MimeType,
                ImageName = att.Name
            });
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            segments.Add(new MessageSegment { Kind = MessageSegmentKind.Text, Content = content });
        }

        // AddMessage 同步触发 MessageAdded 处理器完成列表重建, 无需在此重复 RefreshMessages
        _chatService.AddMessage(CurrentSession!.Id, MessageRole.User, segments);
        // 记录会话绑定的工作目录(用于按目录整理会话与切换会话时恢复)
        _chatService.SetSessionWorkDir(CurrentSession.Id, WorkDir);

        if (isFirstMessage && !string.IsNullOrWhiteSpace(content))
        {
            _ = AutoGenerateTitleAsync(CurrentSession, content);
        }

        CanContinue = false;
        IsSending = true;
        var images = attachments
            .Select(a => new ChatImagePart { Base64Data = a.Base64, MimeType = a.MimeType })
            .ToList();
        _ = RespondAsync(content, images.Count > 0 ? images : null);
    }

    /// <summary>把本地图片文件加入待发送附件(去重、限制数量; 全程 UI 线程访问集合)。</summary>
    public async Task AddImageFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (PendingAttachments.Count >= ImageAttachmentService.MaxAttachments) break;

            var att = await ImageAttachmentService.FromFileAsync(path);
            if (att is null) continue;

            if (PendingAttachments.Any(a => a.Name == att.Name && a.Base64 == att.Base64)) continue;

            PendingAttachments.Add(att);
            OnPropertyChanged(nameof(HasPendingAttachments));
        }
    }

    /// <summary>把剪贴板位图加入待发送附件。</summary>
    public void AddImageFromBitmap(Avalonia.Media.Imaging.Bitmap bmp, string name)
    {
        if (PendingAttachments.Count >= ImageAttachmentService.MaxAttachments) return;

        var att = ImageAttachmentService.FromBitmap(bmp, name);
        if (att is null) return;

        PendingAttachments.Add(att);
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>移除一个待发送附件。</summary>
    [RelayCommand]
    private void RemoveAttachment(PendingImageAttachment attachment)
    {
        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>手动终止当前生成(发送/工具循环均会收到取消信号)。</summary>
    [RelayCommand]
    private void StopGeneration()
    {
        if (!IsSending) return;
        _turnCts?.Cancel();
    }

    /// <summary>继续输出: 以固定指令驱动引擎从中断处续写(不新增用户气泡)。</summary>
    [RelayCommand]
    private void ContinueOutput()
    {
        if (!CanContinue || IsSending) return;
        CanContinue = false;
        IsSending = true;
        _ = RunTurnCoreAsync(Strings.Chat_ContinuePrompt);
    }

    /// <summary>首条消息后调用 LLM 为会话生成简洁标题(异步, 失败时静默保留默认标题)。</summary>
    private async Task AutoGenerateTitleAsync(ChatSession session, string userMessage)
    {
        var title = await ChatTitleService.GenerateAsync(_runtime.Llm, userMessage);
        if (!string.IsNullOrEmpty(title))
        {
            _chatService.RenameSession(session.Id, title);
        }
    }

    private async Task RespondAsync(string userMessage, IReadOnlyList<ChatImagePart>? images = null)
    {
        await RunTurnCoreAsync(userMessage, images);
    }

    /// <summary>驱动一轮引擎调用: 流式呈现、取消处理与分段持久化。images 为用户本轮附带的多模态图片。</summary>
    private async Task RunTurnCoreAsync(string engineMessage, IReadOnlyList<ChatImagePart>? images = null)
    {
        var assistantItem = new ChatItemViewModel(MessageRole.Assistant);
        Messages.Add(assistantItem);

        // 线性时间线: 分段按事件到达顺序排列(思考/正文/工具交替), UI 顺序 = 实际发生顺序
        var entries = new List<TimelineEntry>();
        var toolData = new List<(string Id, string Name, string Args)>();       // 工具调用, 按调用顺序
        var toolOutputs = new Dictionary<string, StringBuilder>();
        var toolFinished = new Dictionary<string, (string Result, bool IsError, string? StepId, ToolCardDetail? Detail)>();
        var flushPending = false;

        // 后台线程累积数据, 节流同步到 UI 线程重建分段
        void OnEngineEvent(AgentEngineEvent e)
        {
            switch (e)
            {
                case EngineThinkingDelta t:
                    if (entries.Count == 0 || entries[^1].Kind != MessageSegmentKind.Thinking)
                    {
                        entries.Add(new TimelineEntry(MessageSegmentKind.Thinking));
                    }
                    entries[^1].Sb.Append(t.Thinking);
                    break;
                case EngineTextDelta t:
                    if (entries.Count == 0 || entries[^1].Kind != MessageSegmentKind.Text)
                    {
                        entries.Add(new TimelineEntry(MessageSegmentKind.Text));
                    }
                    entries[^1].Sb.Append(t.Text);
                    break;
                case EngineToolStarted s:
                    toolData.Add((s.ToolCallId, s.ToolName, s.Arguments));
                    toolOutputs[s.ToolCallId] = new StringBuilder();
                    entries.Add(new TimelineEntry(MessageSegmentKind.Tool) { ToolIndex = toolData.Count - 1 });
                    break;
                case EngineToolOutput o:
                    if (toolOutputs.TryGetValue(o.ToolCallId, out var osb))
                    {
                        osb.AppendLine(o.Line);
                    }

                    break;
                case EngineToolFinished f:
                    toolFinished[f.ToolCallId] = (f.Result.Content, f.Result.IsError, f.Result.StepId, f.Result.Detail);
                    break;
                case EngineContextCompacted:
                    // 自动压缩发生: 在时间线当前位置追加提示文本段
                    entries.Add(new TimelineEntry(MessageSegmentKind.Text));
                    entries[^1].Sb.Append(Strings.Chat_CtxAutoCompacted);
                    break;
            }

            if (flushPending) return;
            flushPending = true;
            Dispatcher.UIThread.Post(FlushUi);
        }

        // UI 线程同步: 按 entries 线性顺序补齐缺失分段(仅尾部追加)并覆盖最新内容
        void FlushUi()
        {
            flushPending = false;

            while (assistantItem.Segments.Count < entries.Count)
            {
                var en = entries[assistantItem.Segments.Count];
                SegmentItemViewModel vm;
                if (en.Kind == MessageSegmentKind.Tool)
                {
                    var td = toolData[en.ToolIndex];
                    vm = new SegmentItemViewModel(MessageSegmentKind.Tool)
                    {
                        ToolName = td.Name,
                        ArgumentsRaw = td.Args
                    };
                }
                else
                {
                    vm = new SegmentItemViewModel(en.Kind);
                }

                assistantItem.Segments.Add(vm);
                en.Vm = vm;
            }

            foreach (var en in entries)
            {
                switch (en.Kind)
                {
                    case MessageSegmentKind.Text:
                        en.Vm!.SetBody(en.Sb.ToString());
                        break;
                    case MessageSegmentKind.Thinking:
                        en.Vm!.SetThinking(en.Sb.ToString());
                        break;
                    case MessageSegmentKind.Tool:
                        var id = toolData[en.ToolIndex].Id;
                        if (toolOutputs.TryGetValue(id, out var osb))
                        {
                            en.Vm!.SetToolOutput(osb.ToString().TrimEnd());
                        }

                        if (toolFinished.TryGetValue(id, out var fin))
                        {
                            en.Vm!.ToolResult = fin.Result;
                            en.Vm.IsToolDone = true;
                            en.Vm.ToolStatus = fin.IsError ? ToolStatusKind.Error : ToolStatusKind.Success;
                            en.Vm.ToolCardDetail = fin.Detail;
                            if (!string.IsNullOrEmpty(fin.StepId))
                            {
                                en.Vm.StepId = fin.StepId;
                            }
                        }

                        break;
                }
            }
        }

        _runtime.Engine.OnEvent += OnEngineEvent;
        _turnCts?.Dispose();
        _turnCts = new CancellationTokenSource();
        try
        {
            _runtime.Engine.Options.Thinking = SelectedThinking;
            _runtime.Engine.Options.WorkDir = string.IsNullOrWhiteSpace(WorkDir) ? null : WorkDir;
            _runtime.Engine.Options.IsPlanMode = IsPlanMode;

            var reply = await _runtime.Engine.RunTurnAsync(engineMessage, images, _turnCts.Token);
            FlushUi(); // 兜底同步一次, 确保最终增量已呈现

            // 全程无流式文本时(如纯最终回复), 将整体回复作为正文分段补到时间线末尾
            if (!string.IsNullOrWhiteSpace(reply) &&
                assistantItem.Segments.All(s => s.Kind != MessageSegmentKind.Text))
            {
                var fallback = new TimelineEntry(MessageSegmentKind.Text);
                fallback.Sb.Append(reply);
                entries.Add(fallback);
                FlushUi();
            }
        }
        catch (OperationCanceledException)
        {
            // 用户主动终止: 保留已生成的部分内容并允许继续输出
            FlushUi();
            var note = new SegmentItemViewModel(MessageSegmentKind.Text);
            note.SetBody(Strings.Chat_StoppedNote);
            assistantItem.Segments.Add(note);
            CanContinue = true;
        }
        catch (Exception ex)
        {
            var err = new SegmentItemViewModel(MessageSegmentKind.Text);
            err.SetBody($"⚠ 发生错误: {ex.Message}");
            assistantItem.Segments.Add(err);
        }
        finally
        {
            _runtime.Engine.OnEvent -= OnEngineEvent;
        }

        // 持久化为结构化分段
        var segments = new List<MessageSegment>();
        foreach (var seg in assistantItem.Segments)
        {
            switch (seg.Kind)
            {
                case MessageSegmentKind.Text when !string.IsNullOrEmpty(seg.BodyContent):
                    segments.Add(new MessageSegment { Kind = MessageSegmentKind.Text, Content = seg.BodyContent });
                    break;
                case MessageSegmentKind.Thinking:
                    segments.Add(new MessageSegment { Kind = MessageSegmentKind.Thinking, Content = seg.ThinkingContent });
                    break;
                case MessageSegmentKind.Tool:
                    segments.Add(new MessageSegment
                    {
                        Kind = MessageSegmentKind.Tool,
                        Tool = new ToolSegment
                        {
                            Name = seg.ToolName,
                            Arguments = seg.ArgumentsRaw,
                            OutputLines = SplitToolOutput(seg.ToolOutput),
                            Result = seg.ToolResult,
                            IsError = seg.ToolStatus == ToolStatusKind.Error,
                            IsDone = seg.IsToolDone,
                            StepId = seg.StepId,
                            Detail = seg.ToolCardDetail
                        }
                    });
                    break;
            }
        }

        if (segments.Count > 0)
        {
            _chatService.AddMessage(CurrentSession!.Id, MessageRole.Assistant, segments);
        }

        IsSending = false;
    }

    /// <summary>时间线条目: 按引擎事件顺序累积的显示分段(工具调用与文本交替呈现)。</summary>
    private sealed class TimelineEntry
    {
        public TimelineEntry(MessageSegmentKind kind) => Kind = kind;

        public MessageSegmentKind Kind { get; }

        /// <summary>工具条目对应 toolData 的下标; 非工具为 -1。</summary>
        public int ToolIndex { get; init; } = -1;

        public StringBuilder Sb { get; } = new();

        public SegmentItemViewModel? Vm { get; set; }
    }

    private static List<string> SplitToolOutput(string value)
    {
        return string.IsNullOrEmpty(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}