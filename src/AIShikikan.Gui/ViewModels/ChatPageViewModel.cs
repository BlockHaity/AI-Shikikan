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
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

public enum RightPanelMode
{
    Assignment,
    Git
}

public partial class ChatPageViewModel : ViewModelBase
{
    private readonly ChatService _chatService;
    private readonly CommanderRuntime _runtime;
    private readonly StringBuilder _toolLog = new();
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

    [ObservableProperty]
    private IReadOnlyList<ChatMessage> _messages = [];

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isSending;

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

    public AgentPanelViewModel AgentPanel { get; }

    public GitPanelViewModel GitPanel { get; }

    public SessionPanelViewModel SessionPanel { get; }

    public bool IsAssignmentMode => PanelMode == RightPanelMode.Assignment;

    public bool IsGitMode => PanelMode == RightPanelMode.Git;

    public ChatPageViewModel(ThemeService themeService)
    {
        ThemeService = themeService;
        _chatService = new ChatService();
        _runtime = AppShell.Instance.Runtime;
        Sessions = _chatService.Sessions;
        CurrentSession = _chatService.CurrentSession;
        Messages = CurrentSession?.Messages ?? [];
        _currentSessionId = CurrentSession?.Id;

        AgentPanel = new AgentPanelViewModel();
        GitPanel = new GitPanelViewModel();
        SessionPanel = new SessionPanelViewModel(_chatService);
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);

        _chatService.CurrentSessionChanged += (_, session) =>
        {
            CurrentSession = session;
            Messages = session?.Messages ?? [];
            _currentSessionId = session?.Id;
            AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        };
        _chatService.MessageAdded += (_, _) =>
        {
            Messages = CurrentSession?.Messages ?? [];
            Sessions = _chatService.Sessions;
        };

        _runtime.Assignments.AssignmentChanged += _ =>
            Dispatcher.UIThread.Post(() => AgentPanel.RefreshAssignments());

        _runtime.Engine.OnEvent += OnEngineUsageRecorded;

        AppShell.Instance.DataChanged += OnShellDataChanged;

        RefreshActiveModel();
        RefreshProviders();
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var settings = _runtime.Llm.Settings;
        if (string.Equals(settings.ActiveModel, value, StringComparison.Ordinal)) return;

        settings.ActiveModel = value;
        AIShikikan.Core.Services.Llm.ProviderSettingsService.Save(settings);
        RefreshActiveModel();
    }

    partial void OnPanelModeChanged(RightPanelMode value)
    {
        OnPropertyChanged(nameof(IsAssignmentMode));
        OnPropertyChanged(nameof(IsGitMode));
        if (value == RightPanelMode.Git)
        {
            GitPanel.Refresh();
        }
    }

    private void RefreshActiveModel()
    {
        ActiveModelText = $"{_runtime.Llm.ResolveModel()} @ {_runtime.Llm.GetProvider()?.Id ?? "-"}";
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

    private void RefreshShellDataChanged()
    {
        RefreshActiveModel();
        RefreshProviders();
        AgentPanel.RefreshAll();
        GitPanel.Refresh();
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
    private void ToggleRightPanel()
    {
        IsRightPanelVisible = !IsRightPanelVisible;
    }

    [RelayCommand]
    private void NewSession()
    {
        _chatService.CreateSession();
        Sessions = _chatService.Sessions;
        Messages = CurrentSession?.Messages ?? [];
        _currentSessionId = CurrentSession?.Id;
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
    }

    [RelayCommand]
    private void DeleteSession(string sessionId)
    {
        _chatService.DeleteSession(sessionId);
        Sessions = _chatService.Sessions;
        Messages = CurrentSession?.Messages ?? [];
        _currentSessionId = CurrentSession?.Id;
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
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
        Messages = CurrentSession.Messages;
        _runtime.Engine.ClearConversation();
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
        Messages = CurrentSession.Messages;
        Sessions = _chatService.Sessions;

        if (isFirstMessage)
        {
            _ = AutoGenerateTitleAsync(CurrentSession, content);
        }

        IsSending = true;
        _ = RespondAsync(content);
    }

    /// <summary>首条消息后调用 LLM 为会话生成简洁标题(异步, 失败时静默保留默认标题)。</summary>
    private async Task AutoGenerateTitleAsync(ChatSession session, string userMessage)
    {
        try
        {
            var provider = _runtime.Llm.GetProvider();
            if (provider is null) return;

            var request = new ChatRequest
            {
                Model = _runtime.Llm.ResolveModel(),
                MaxTokens = 32,
                Temperature = 0.3,
                System = "你是一个会话标题生成助手。根据用户的消息生成一个简洁的中文标题(不超过20个字符), 只输出标题本身, 不要引号、不要标点、不要多余说明。",
                Messages =
                [
                    new ChatTurnMessage
                    {
                        Role = ChatMsgRole.User,
                        Content = userMessage.Length > 200 ? userMessage[..200] : userMessage
                    }
                ]
            };

            var response = await _runtime.Llm.GetClient(provider.Id).CompleteAsync(request);
            if (response.IsError || string.IsNullOrWhiteSpace(response.Content)) return;

            var title = CleanGeneratedTitle(response.Content);
            if (title.Length > 0)
            {
                _chatService.RenameSession(session.Id, title);
            }
        }
        catch
        {
            // 标题生成失败不影响主流程
        }
    }

    private static string CleanGeneratedTitle(string text)
    {
        var title = text.Trim().Trim('"', '\'', '“', '”', '「', '」', '【', '】', '。', '：', ':');
        if (title.Length <= 24) return title;
        return title[..24].TrimEnd('…', '.', '。') + "...";
    }

    private async Task RespondAsync(string userMessage)
    {
        var sb = new StringBuilder();

        void OnEngineEvent(AgentEngineEvent e)
        {
            switch (e)
            {
                case EngineToolStarted started:
                    _toolLog.AppendLine($"[tool] {started.ToolName} {started.Arguments}");
                    break;
                case EngineToolOutput output:
                    _toolLog.AppendLine($"  {output.Line}");
                    break;
                case EngineToolFinished finished:
                    _toolLog.AppendLine(finished.Result.IsError ? $"✘ {finished.Result.Content}" : "✔");
                    break;
            }
        }

        _runtime.Engine.OnEvent += OnEngineEvent;
        try
        {
            var reply = await _runtime.Engine.RunTurnAsync(userMessage);
            if (_toolLog.Length > 0)
            {
                sb.AppendLine(reply);
                sb.AppendLine();
                sb.AppendLine(_toolLog.ToString().TrimEnd());
            }
            else
            {
                sb.Append(reply);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"⚠ 发生错误: {ex.Message}");
        }
        finally
        {
            _runtime.Engine.OnEvent -= OnEngineEvent;
            _toolLog.Clear();
        }

        _chatService.AddMessage(CurrentSession!.Id, MessageRole.Assistant, sb.ToString());
        Messages = CurrentSession.Messages;
        IsSending = false;
    }
}