using AIShikikan.Cli.Models;
using AIShikikan.Cli.Tui.Services;
using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
using AIShikikan.Core.Services.Usage;
using ChatSession = AIShikikan.Core.Models.ChatSession;
using CoreMessageRole = AIShikikan.Core.Models.MessageRole;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AIShikikan.Cli.Tui;

/// <summary>基于 Terminal.Gui 的全屏 TUI: 左侧对话日志 + 右侧信息侧栏(Agent/状态/Git) + 底部输入与状态栏。
/// 引擎事件异步驱动日志与侧栏刷新, 审批与步骤检查通过模态对话框交互。</summary>
public class TuiApp
{
    private readonly List<Message> _messages = [];
    private readonly CommanderRuntime _runtime;
    private readonly ChatService _chatService;
    private string _sessionId;
    private bool _userWantsSidebar = true;

    private IApplication _app = null!;
    private Window _window = null!;
    private ChatLogView _chat = null!;
    private SidebarView _sidebar = null!;
    private InputBar _inputBar = null!;
    private TuiUiOutput _ui = null!;
    private CommandService _commandService = null!;
    private StatusBar _statusBar = null!;
    private Shortcut _sessionShortcut = null!;
    private Shortcut _modelShortcut = null!;
    private Shortcut _runningShortcut = null!;

    public TuiApp(string? personaId = null)
    {
        _runtime = CommanderRuntime.Boot(Environment.CurrentDirectory, personaId);
        _chatService = new ChatService();
        _sessionId = _chatService.CurrentSession?.Id ?? string.Empty;
    }

    public CommanderRuntime Runtime => _runtime;

    public async Task RunAsync(CancellationToken ct = default)
    {
        using IApplication app = Application.Create();
        app.Init();
        _app = app;

        // 完全遵循终端背景: 前景/背景均用终端默认色(透明), 不覆盖终端自身配色
        MarkupConverter.DefaultForeground = Color.None;
        MarkupConverter.DefaultBackground = Color.None;

        BuildUi(app);

        // 事件接线(引擎在后台线程运行, UI 更新统一经 MainLoop.Invoke 编组)
        _runtime.Engine.OnEvent += OnEngineEvent;
        _runtime.Assignments.AssignmentChanged += OnAssignmentChanged;
        // 终端尺寸变化时(每轮布局后)重新评估侧栏显隐
        app.LayoutAndDrawComplete += (_, _) => UpdateSidebarVisibility("layout");

        EnsureSession();
        ShowWelcome();

        _messages.Add(new Message
        {
            Role = MessageRole.System,
            Content = $"AI-Shikikan TUI 已启动。会话: [{_chatService.CurrentSession?.DisplayTitle}]。输入消息或 /help 查看可用命令。"
        });
        RenderMessage(_messages[0]);

        app.Invoke(() =>
        {
            UpdateSidebarVisibility();
            _sidebar.RefreshAll();
            RefreshStatusBar();
        });

        app.Run(_window);

        _runtime.Engine.OnEvent -= OnEngineEvent;
        _runtime.Assignments.AssignmentChanged -= OnAssignmentChanged;

        ShowGoodbye();
    }

    private void BuildUi(IApplication app)
    {
        _window = new Window { Title = "AI-Shikikan", BorderStyle = LineStyle.None };
        _window.SetScheme(UiTheme.Transparent);

        _chat = new ChatLogView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };

        _ui = new TuiUiOutput(app, _chat);
        _commandService = new CommandService(_messages, _runtime, _chatService, _ui);

        _sidebar = new SidebarView(_runtime, _chatService, _ui)
        {
            X = Pos.Right(_chat) + 1,
            Y = 0,
            Width = SidebarView.DefaultWidth,
            Height = Dim.Fill(4),
            Visible = false
        };

        _inputBar = new InputBar
        {
            X = 0,
            Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(),
            Height = 3
        };
        _inputBar.Submitted += OnSubmitted;
        _inputBar.ConfigureAutocomplete(new CommandSuggestionGenerator(_runtime, _chatService, _commandService));

        BuildStatusBar();

        _window.Add(_chat, _sidebar, _inputBar, _statusBar);

        // 输入框挂到窗口(而非 InputBar 内部): 命令补全弹出列表才能以窗口为参照,
        // 完整渲染在输入框上方
        _window.Add(_inputBar.Field);

        // 首次初始化后聚焦输入框(此前视图尚未初始化, SetFocus 无效)
        _window.Initialized += (_, _) => _inputBar.SetFocusToField();
        _chat.MouseEvent += OnChatMouseEvent;
    }

    private void BuildStatusBar()
    {
        _statusBar = new StatusBar
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1
        };

        _sessionShortcut = new Shortcut { Title = "会话: -" };
        _modelShortcut = new Shortcut { Title = "模型: -" };
        _runningShortcut = new Shortcut { Title = "●0 工作中" };

        var toggleSidebar = new Shortcut
        {
            Title = "F2 侧栏",
            Key = Key.F2,
            BindKeyToApplication = true,
            Action = ToggleSidebar
        };

        var clear = new Shortcut
        {
            Title = "F4 清屏",
            Key = Key.F4,
            BindKeyToApplication = true,
            Action = () => _chat.Clear()
        };

        var quit = new Shortcut { Title = "Esc 退出" };

        _statusBar.Add(_sessionShortcut, _modelShortcut, _runningShortcut, toggleSidebar, clear, quit);
    }

    /// <summary>侧栏显隐: 终端过窄时自动隐藏(用户手动开关在宽窗口时生效)。</summary>
    private void UpdateSidebarVisibility(string? reason = null)
    {
        var show = _userWantsSidebar && _window.Viewport.Width >= SidebarView.MinWindowWidth;
        if (show == _sidebar.Visible)
        {
            return;
        }

        _sidebar.Visible = show;
        _chat.Width = show ? Dim.Fill(SidebarView.DefaultWidth + 1) : Dim.Fill();
        _chat.SetNeedsLayout();
        _sidebar.SetNeedsLayout();
    }

    private void ToggleSidebar()
    {
        _userWantsSidebar = !_userWantsSidebar;
        UpdateSidebarVisibility();
    }

    /// <summary>点击聊天区将焦点还给输入框(聊天为只读, 不应抢焦点)。</summary>
    private void OnChatMouseEvent(object? sender, Mouse mouse)
    {
        if (mouse.IsSingleClicked)
        {
            _inputBar.SetFocusToField();
        }
    }

    private void OnSubmitted(string input)
    {
        if (input is "/quit" or "/exit")
        {
            _app.RequestStop();
            return;
        }

        if (input.StartsWith('/'))
        {
            _commandService.TryHandle(input);
            _ui.ScrollToBottom();
            return;
        }

        _ = HandleUserMessageAsync(input);
    }

    /// <summary>会话切换后同步当前会话 id, 使 roster/用量等命令作用于新会话。</summary>
    private void SyncSession()
    {
        _sessionId = _chatService.CurrentSession?.Id ?? string.Empty;
    }

    private void EnsureSession()
    {
        if (_chatService.CurrentSession is null)
        {
            _chatService.CreateSession("TUI 会话");
        }

        SyncSession();
    }

    private async Task HandleUserMessageAsync(string input)
    {
        EnsureSession();
        var session = _chatService.CurrentSession!;

        var userMessage = new Message
        {
            Role = MessageRole.User,
            Content = input
        };
        _messages.Add(userMessage);
        RenderMessage(userMessage);

        _chatService.AddMessage(session.Id, CoreMessageRole.User, input);

        // 首条消息自动生成标题(GUI 同款体验)
        if (session.Messages.Count == 1)
        {
            _ = AutoGenerateTitleAsync(session, input);
        }

        var beforeSteps = _runtime.Git.PendingReview().Select(s => s.StepId).ToHashSet(StringComparer.Ordinal);

        var response = await _runtime.Engine.RunTurnAsync(input, CancellationToken.None);
        _chatService.AddMessage(session.Id, CoreMessageRole.Assistant, response);

        var assistantMessage = new Message
        {
            Role = MessageRole.Assistant,
            Content = response
        };
        _messages.Add(assistantMessage);
        RenderMessage(assistantMessage);

        await ReviewPendingStepsAsync(beforeSteps);
        _app.Invoke(() =>
        {
            RefreshStatusBar();
            _inputBar.SetFocusToField();
        });
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

    private void RenderMessage(Message message)
    {
        var (icon, color) = message.Role switch
        {
            MessageRole.User => ("👤", "brightblue"),
            MessageRole.Assistant => ("🤖", "brightgreen"),
            MessageRole.System => ("ℹ️", "grey"),
            MessageRole.Tool => ("🔧", "yellow"),
            _ => ("?", "white")
        };

        _chat.AppendMarkup($"[bold {color}]{icon} {message.RoleLabel}[/]");
        if (!string.IsNullOrEmpty(message.Content))
        {
            _chat.AppendMarkup(MarkupEscape.Escape(message.Content));
        }

        _chat.AppendCells([]);
    }

    private void ShowWelcome()
    {
        _chat.AppendMarkup($"[bold green]AI-Shikikan[/] [grey]v{AppInfo.Version}[/]");
        _chat.AppendMarkup("[grey]一个强大的 Agent 管理与指挥工具 — 输入消息对话, /help 查看命令, Esc 退出[/]");
        _chat.AppendCells([]);
    }

    private void ShowGoodbye()
    {
        Console.WriteLine();
        Console.WriteLine("再见!");
    }

    private async Task ReviewPendingStepsAsync(HashSet<string> before)
    {
        var pending = _runtime.Git.PendingReview()
            .Where(s => !before.Contains(s.StepId))
            .ToList();

        foreach (var step in pending)
        {
            _chat.AppendMarkup(
                $"[bold cyan]步骤检查点:[/] {MarkupEscape.Escape(step.StepId)} [{MarkupEscape.Escape(step.Label)}]  分支: {step.StepBranch} → 基: {step.BaseBranch}");

            while (true)
            {
                var choice = _ui.SelectModal("步骤检查点",
                    $"步骤 {step.StepId} 的变更如何处理?", ["查看 diff", "合并到主分支", "丢弃(回滚该步)", "暂不处理"]);

                switch (choice)
                {
                    case 0:
                        var diff = _runtime.Git.GetDiff(step.StepId);
                        _ui.ShowTextModal($"diff {step.StepId}",
                            diff.Succeeded ? diff.Stdout : diff.Stderr);
                        continue;

                    case 1:
                        ExecuteGitSafe(() => _runtime.Git.MergeStep(step.StepId),
                            $"已合并步骤 {step.StepId} 到 {step.BaseBranch}");
                        break;

                    case 2:
                        if (_ui.ConfirmModal("丢弃步骤", "丢弃将删除该步骤分支并放弃全部变更, 确认?"))
                        {
                            ExecuteGitSafe(() => _runtime.Git.DropStep(step.StepId),
                                $"已丢弃步骤 {step.StepId}");
                        }

                        break;
                }

                break;
            }

            _app.Invoke(() => _sidebar.Refresh());
        }
    }

    private void ExecuteGitSafe(Func<GitCommandResult> action, string successMessage)
    {
        try
        {
            var result = action();
            if (!result.Succeeded)
            {
                _ui.Error($"git 操作失败: {result.Stderr}");
                return;
            }

            _ui.Ok(successMessage);
            if (!string.IsNullOrWhiteSpace(result.Stdout))
            {
                _ui.Hint(Truncate(result.Stdout, 2000));
            }
        }
        catch (Exception ex)
        {
            _ui.Error($"git 操作失败: {ex.Message}");
        }
    }

    /// <summary>记录 CLI 会话的 LLM 用量统计(与会话 id 关联, 与 GUI 共用统计)。</summary>
    private void OnUsageRecorded(EngineUsageRecorded usage)
    {
        var session = _chatService.CurrentSession;
        UsageStatsService.RecordLlmUsage(
            session?.Id ?? _sessionId, session?.DisplayTitle ?? "TUI 会话",
            usage.Provider, usage.Model,
            usage.Usage.InputTokens, usage.Usage.OutputTokens,
            usage.Usage.CachedInputTokens);

        _app.Invoke(() =>
        {
            _sidebar.Refresh();
            RefreshStatusBar();
        });
    }

    private void OnAssignmentChanged(Assignment assignment)
    {
        _app.Invoke(() =>
        {
            _sidebar.Refresh();
            RefreshStatusBar();
        });
    }

    private void OnEngineEvent(AgentEngineEvent e)
    {
        switch (e)
        {
            case EngineToolStarted started:
                var args = started.Arguments.Length > 100
                    ? started.Arguments[..100] + "..."
                    : started.Arguments;
                _app.Invoke(() =>
                {
                    _chat.AppendMarkup($"[cyan]🔥 工具调用:[/] [bold]{MarkupEscape.Escape(started.ToolName)}[/]");
                    if (args.Length > 0)
                    {
                        _chat.AppendMarkup($"[grey]   {MarkupEscape.Escape(args)}[/]");
                    }
                });
                break;

            case EngineToolOutput output:
                _app.Invoke(() => _chat.AppendMarkup($"[grey]   ├ {MarkupEscape.Escape(TruncateLine(output.Line))}[/]"));
                break;

            case EngineToolFinished finished:
                _app.Invoke(() => _chat.AppendMarkup(finished.Result.IsError
                    ? $"[red]   └ 工具失败: {MarkupEscape.Escape(Truncate(finished.Result.Content, 200))}[/]"
                    : "[green]   └ 工具完成 ✓[/]"));
                break;

            case EngineApprovalRequested approval:
                // 阻塞引擎线程直到用户在模态对话框做出选择(主循环继续运行, 无死锁)
                var approved = _ui.ConfirmModal("批准工具调用",
                    $"{approval.ToolName}: {approval.Arguments}", "批准", "拒绝");
                approval.UserDecision.TrySetResult(approved);
                break;

            case EngineUsageRecorded usage:
                OnUsageRecorded(usage);
                break;
        }
    }

    private void RefreshStatusBar()
    {
        _sessionShortcut.Title = $"会话: {_chatService.CurrentSession?.DisplayTitle ?? "-"}";
        _modelShortcut.Title = $"模型: {_runtime.Llm.ResolveModel() ?? "-"}";
        _runningShortcut.Title = $"●{_sidebar.RunningAgentCount} 工作中";
        _statusBar.SetNeedsLayout();
        _statusBar.SetNeedsDraw();
    }

    private static string Truncate(string s, int len) => s.Length <= len ? s : s[..len] + "...";

    private static string TruncateLine(string s) => s.Length <= 160 ? s : s[..160] + "...";
}
