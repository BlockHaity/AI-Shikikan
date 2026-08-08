using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Agents;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Personas;
using AIShikikan.Core.Services.Runtime;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

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

    [ObservableProperty]
    private IReadOnlyList<Persona> _personas = [];

    [ObservableProperty]
    private Persona? _selectedPersona;

    [ObservableProperty]
    private IReadOnlyList<CliAgentDefinition> _agents = [];

    [ObservableProperty]
    private CliAgentDefinition? _selectedAgent;

    [ObservableProperty]
    private string _assignTaskText = string.Empty;

    [ObservableProperty]
    private bool _isAsyncAssign;

    [ObservableProperty]
    private bool _isAssigning;

    [ObservableProperty]
    private IReadOnlyList<Assignment> _assignments = [];

    [ObservableProperty]
    private string _gitStateText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<GitStepRecord> _gitSteps = [];

    [ObservableProperty]
    private string _gitDiffText = string.Empty;

    [ObservableProperty]
    private bool _hasGitDiff;

    [ObservableProperty]
    private string _activeModelText = string.Empty;

    public ChatPageViewModel()
    {
        _chatService = new ChatService();
        _runtime = AppShell.Instance.Runtime;
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

        _runtime.Assignments.AssignmentChanged += _ =>
            Dispatcher.UIThread.Post(RefreshAssignments);

        AppShell.Instance.DataChanged += OnShellDataChanged;
        RefreshSidePanel();
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

    partial void OnSelectedPersonaChanged(Persona? value)
    {
        if (value is not null)
        {
            _runtime.Engine.SetPersonaText(value.SystemPrompt);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void Assign()
    {
        if (SelectedAgent is null || string.IsNullOrWhiteSpace(AssignTaskText)) return;

        var agent = SelectedAgent;
        var taskText = AssignTaskText.Trim();
        var sessionId = CurrentSession?.Id;
        IsAssigning = true;

        AppShell.Instance.Dispatch(agent, taskText, IsAsyncAssign ? "async" : "sync",
            onFinished: (assignment, run) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsAssigning = false;
                    AssignTaskText = string.Empty;
                    RefreshAssignments();
                    AppendAssignmentResult(sessionId, assignment, run);
                });
            });
    }

    private void AppendAssignmentResult(string? sessionId, Assignment assignment, CliAgentRunResult? run)
    {
        if (sessionId is null) return;

        if (assignment.Status == SubagentStatus.Completed
            && !string.IsNullOrWhiteSpace(assignment.OutputTail))
        {
            _chatService.AddMessage(sessionId, MessageRole.Assistant,
                $"**子代理 {assignment.AgentName} 完成**\n\n{assignment.OutputTail}");
        }
        else if (assignment.Status is SubagentStatus.Failed or SubagentStatus.TimedOut)
        {
            _chatService.AddMessage(sessionId, MessageRole.Assistant,
                $"⚠ 子代理 {assignment.AgentName} {assignment.Status}: {assignment.Error ?? run?.Output ?? "未知错误"}");
        }

        Messages = CurrentSession?.Messages ?? [];
    }

    private bool CanAssign() => SelectedAgent is not null
                                && !string.IsNullOrWhiteSpace(AssignTaskText)
                                && !IsAssigning;

    [RelayCommand]
    private void CancelAssignment(string assignmentId)
    {
        AppShell.Instance.CancelDispatch(assignmentId);
        RefreshAssignments();
    }

    private void RefreshAssignments()
    {
        Assignments = AppShell.Instance.ProcessList.ToList();
    }

    private void OnShellDataChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshSidePanel);
            return;
        }

        RefreshSidePanel();
    }

    private void RefreshSidePanel()
    {
        ActiveModelText = $"{_runtime.Llm.ResolveModel()} @ {_runtime.Llm.GetProvider()?.Id ?? "-"}";
        Personas = AppShell.Instance.Personas.ToList();
        Agents = AppShell.Instance.Agents.ToList();
        if (SelectedAgent is null && Agents.Count > 0)
        {
            SelectedAgent = Agents[0];
        }

        RefreshAssignments();
        RefreshGit();
    }

    [RelayCommand]
    private void RefreshGit()
    {
        var git = _runtime.Git;
        if (!git.IsRepoAvailable)
        {
            GitStateText = "当前目录不是 git 仓库";
            GitSteps = [];
            return;
        }

        var dirty = git.HasUncommittedChanges() ? "有未提交变更" : "干净";
        GitStateText = $"分支: {git.CurrentBranch() ?? "?"}  最近提交: {git.LastCommitShort() ?? "?"}  [{dirty}]";
        GitSteps = git.PendingReview().ToList();
        GitDiffText = string.Empty;
        HasGitDiff = false;
    }

    [RelayCommand]
    private void ShowGitDiff(string stepId)
    {
        var diff = _runtime.Git.GetDiff(stepId);
        GitDiffText = diff.Succeeded ? diff.Stdout : diff.Stderr;
        HasGitDiff = true;
    }

    [RelayCommand]
    private void MergeStep(string stepId) => RunGitAction(stepId, s => _runtime.Git.MergeStep(s));

    [RelayCommand]
    private void DropStep(string stepId) => RunGitAction(stepId, s => _runtime.Git.DropStep(s));

    [RelayCommand]
    private void RevertStep(string stepId) => RunGitAction(stepId, s => _runtime.Git.RevertStep(s));

    private void RunGitAction(string stepId, Func<string, GitCommandResult> action)
    {
        try
        {
            var result = action(stepId);
            RefreshGit();
            if (!result.Succeeded)
            {
                GitDiffText = result.Stderr;
                HasGitDiff = true;
            }
        }
        catch (Exception ex)
        {
            GitDiffText = ex.Message;
            HasGitDiff = true;
        }
    }

    [RelayCommand]
    private void ToggleAssignMode()
    {
        IsAsyncAssign = !IsAsyncAssign;
    }
}