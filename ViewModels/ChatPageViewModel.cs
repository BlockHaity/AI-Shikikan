using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Gui.Resources;
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
            AgentPanel.SetSession(_currentSessionId ?? string.Empty);
            StatusPanel.SetSession(_currentSessionId ?? string.Empty);
        };
        _chatService.MessageAdded += (_, _) =>
        {
            if (IsSending) return; // 流式期间由 RespondAsync 维护, 避免重建列表
            RefreshMessages();
            Sessions = _chatService.Sessions;
        };

        _runtime.Assignments.AssignmentChanged += _ =>
            Dispatcher.UIThread.Post(() => AgentPanel.RefreshAssignments());

        _runtime.Engine.OnEvent += OnEngineUsageRecorded;

        AppShell.Instance.DataChanged += OnShellDataChanged;

        RefreshActiveModel();
        RefreshProviders();
        RefreshThinkingOptions();
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

    /// <summary>按当前模型上限重建思考菜单(自动 + 不超过上限的档位), 并夹紧当前选中值。</summary>
    private void RefreshThinkingOptions()
    {
        var cap = CurrentMaxThinking();
        var options = new List<ThinkingLevel> { ThinkingLevel.Auto };
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
        if (e is not EngineUsageRecorded usage) return;

        var title = CurrentSession?.DisplayTitle ?? "未知会话";
        UsageStatsService.RecordLlmUsage(
            _currentSessionId ?? "unknown", title,
            usage.Provider, usage.Model,
            usage.Usage.InputTokens, usage.Usage.OutputTokens,
            usage.Usage.CachedInputTokens);
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
        RefreshActiveModel();
        RefreshProviders();
        RefreshThinkingOptions();
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

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;
        if (CurrentSession is null)
        {
            NewSession();
        }

        var content = InputText;
        InputText = string.Empty;

        var isFirstMessage = CurrentSession!.Messages.Count == 0;
        _chatService.AddMessage(CurrentSession!.Id, MessageRole.User, content);
        RefreshMessages();
        Sessions = _chatService.Sessions;

        if (isFirstMessage)
        {
            _ = AutoGenerateTitleAsync(CurrentSession, content);
        }

        CanContinue = false;
        IsSending = true;
        _ = RespondAsync(content);
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

    private async Task RespondAsync(string userMessage)
    {
        await RunTurnCoreAsync(userMessage);
    }

    /// <summary>驱动一轮引擎调用: 流式呈现、取消处理与分段持久化。</summary>
    private async Task RunTurnCoreAsync(string engineMessage)
    {
        var assistantItem = new ChatItemViewModel(MessageRole.Assistant);
        Messages.Add(assistantItem);

        // 线性时间线: 分段按事件到达顺序排列(思考/正文/工具交替), UI 顺序 = 实际发生顺序
        var entries = new List<TimelineEntry>();
        var toolData = new List<(string Id, string Name, string Args)>();       // 工具调用, 按调用顺序
        var toolOutputs = new Dictionary<string, StringBuilder>();
        var toolFinished = new Dictionary<string, (string Result, bool IsError, string? StepId)>();
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
                    toolFinished[f.ToolCallId] = (f.Result.Content, f.Result.IsError, f.Result.StepId);
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

            var reply = await _runtime.Engine.RunTurnAsync(engineMessage, _turnCts.Token);
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
                            StepId = seg.StepId
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