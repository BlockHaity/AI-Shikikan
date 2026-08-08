using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCommander.Core.Models;
using AgentCommander.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentCommander.Gui.ViewModels;

public partial class ChatPageViewModel : ViewModelBase
{
    private readonly ChatService _chatService;

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
        await Task.Delay(500);
        var response = $"Echo: {userMessage}";
        _chatService.AddMessage(CurrentSession!.Id, MessageRole.Assistant, response);
        Messages = CurrentSession.Messages;
        IsSending = false;
    }
}
