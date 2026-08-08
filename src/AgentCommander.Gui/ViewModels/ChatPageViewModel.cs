using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AgentCommander.Core.Models;
using AgentCommander.Core.Services;
using AgentCommander.Core.Services.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentCommander.Gui.ViewModels;

public partial class ChatPageViewModel : ViewModelBase
{
    private readonly ChatService _chatService;
    private readonly CommanderRuntime _runtime;
    private readonly StringBuilder _toolLog = new();

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

    public ChatPageViewModel()
    {
        _chatService = new ChatService();
        _runtime = CommanderRuntime.Boot(Directory.GetCurrentDirectory());
        Sessions = _chatService.Sessions;
        CurrentSession = _chatService.CurrentSession;
        Messages = CurrentSession?.Messages ?? [];

        _chatService.CurrentSessionChanged += (_, session) =>
        {
            CurrentSession = session;
            Messages = session?.Messages ?? [];
        };
        _chatService.MessageAdded += (_, _) =>
        {
            Messages = CurrentSession?.Messages ?? [];
            Sessions = _chatService.Sessions;
        };
    }

    [RelayCommand]
    private void NewSession()
    {
        _chatService.CreateSession();
        Sessions = _chatService.Sessions;
        Messages = CurrentSession?.Messages ?? [];
    }

    [RelayCommand]
    private void DeleteSession(string sessionId)
    {
        _chatService.DeleteSession(sessionId);
        Sessions = _chatService.Sessions;
        Messages = CurrentSession?.Messages ?? [];
    }

    [RelayCommand]
    private void SwitchSession(string sessionId)
    {
        _chatService.SwitchSession(sessionId);
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

        _chatService.AddMessage(CurrentSession!.Id, MessageRole.User, content);
        Messages = CurrentSession.Messages;
        Sessions = _chatService.Sessions;

        IsSending = true;
        _ = RespondAsync(content);
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
                    break;                case EngineToolOutput output:
                    _toolLog.AppendLine($"  {output.Line}");
                    break;
                case EngineToolFinished finished:
                    _toolLog.AppendLine(finished.Result.IsError ? $"✘ {finished.Result.Content}" : "✔");
                    break;
                case EngineApprovalRequested approval:
                    approval.UserDecision.SetResult(true);
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
        catch (System.Exception ex)
        {
            sb.AppendLine($"⚠ 发生错误: {ex.Message}");
        }
        finally
        {
            _runtime.Engine.OnEvent -= OnEngineEvent;
        }

        _chatService.AddMessage(CurrentSession!.Id, MessageRole.Assistant, sb.ToString());
        Messages = CurrentSession.Messages;
        IsSending = false;
    }
}
