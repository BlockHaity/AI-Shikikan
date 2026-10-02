using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using AIShikikan.Core.Services.Git;
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
    private readonly HashSet<string> _runningSessionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingImageAttachment>> _sessionAttachments = new(StringComparer.Ordinal);
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

    // 注: 原先这里还有一个 [ObservableProperty] IReadOnlyList<ChatSession> _sessions,
    // 全仓检索确认没有任何绑定/代码后置使用它(会话列表由 SessionPanelViewModel 自行持有),
    // 每次赋值只是触发一次无意义的 PropertyChanged, 已删除。

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

    /// <summary>当前会话排队等待执行的回合数(不含正在执行的那个)。</summary>
    /// <remarks>
    /// SessionRuntime.PendingTurnCount 没有事件(入队/出队刻意不抛 StateChanged, 保持"回合开始/结束"语义),
    /// UI 无法直接感知, 故由 <see cref="_queuedPollTimer"/> 在有排队时轮询, 归零即停。
    /// </remarks>
    [ObservableProperty]
    private int _queuedTurnCount;

    /// <summary>当前会话有排队等待的回合(供界面提示与发送态判定)。</summary>
    public bool IsQueued => QueuedTurnCount > 0;

    // TODO(i18n 收口): 排队徽标文案暂无对应 resx 键(全仓检索 Strings 无排队相关键),
    // 暂用硬编码占位。收口时建议新增 Session_QueuedBadge(中文 "排队中 ({0})" / 英文 "Queued ({0})"),
    // 把这里的常量换成 string.Format(Strings.Session_QueuedBadge, QueuedTurnCount) 即可。
    private const string QueuedBadgeFormat = "排队中 ({0})";

    /// <summary>排队徽标文案(仅 <see cref="IsQueued"/> 为 true 时显示)。</summary>
    public string QueuedBadgeText => string.Format(QueuedBadgeFormat, QueuedTurnCount);

    /// <summary>排队计数轮询表: 仅在 QueuedTurnCount &gt; 0 时运行, 归零即停, 避免常驻轮询。</summary>
    private readonly DispatcherTimer _queuedPollTimer;

    /// <summary>
    /// 排队计数变化。启停条件是自指的(靠 QueuedTurnCount 自身), 因此必须有外部"点火"调用:
    /// 构造函数末尾、切会话、回合结束三处都会调用 <see cref="RefreshQueuedTurnCount"/>,
    /// 缺一个就会出现"首次进入页面计数恒为 0 → 轮询表永不启动"的死状态。
    /// </summary>
    partial void OnQueuedTurnCountChanged(int value)
    {
        if (value > 0)
        {
            // 首次观察到排队时启动轮询(尚未启动才启动, 重复 Start 重启间隔)
            if (!_queuedPollTimer.IsEnabled) _queuedPollTimer.Start();
        }
        else
        {
            _queuedPollTimer.Stop();
        }

        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(QueuedBadgeText));
        NotifySendState();
    }

    /// <summary>按当前会话刷新排队计数(读 SessionRuntime.PendingTurnCount, 仅 UI 线程调用)。</summary>
    private void RefreshQueuedTurnCount() =>
        QueuedTurnCount = _runtime.Sessions.TryGet(_currentSessionId ?? string.Empty)?.PendingTurnCount ?? 0;

    /// <summary>手动终止当前回合的取消源（按会话保存，支持后台会话独立停止）。</summary>
    private readonly Dictionary<string, CancellationTokenSource> _turnCtsMap = new(StringComparer.Ordinal);

    // 注: 原先还有一个单一字段 _turnCts 与上面的字典语义重叠, 其回退分支
    // `else _turnCts?.Cancel()` 可能停掉另一个会话的回合。已删除, 统一走 _turnCtsMap。

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

    /// <summary>当前工作目录对应的 Git 仓库根目录。</summary>
    public string RepositoryRootText => CurrentRepositoryRoot;

    /// <summary>当前绑定分支；detached HEAD 时显示资源化提示。</summary>
    public string BranchText => IsDetachedHead
        ? Strings.Chat_DetachedHead
        : string.IsNullOrWhiteSpace(CurrentBranch) ? Strings.Chat_BranchUnknown : CurrentBranch;

    [ObservableProperty]
    private string _currentRepositoryRoot = string.Empty;

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    [ObservableProperty]
    private bool _isDetachedHead;

    [ObservableProperty]
    private bool _isEmptyRepository;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>工作区存在未提交变更时仅提示，不阻止发送。</summary>
    public string? GitWarningText => IsDirty ? Strings.Chat_GitDirtyWarning : null;

    public bool HasGitWarning => !string.IsNullOrEmpty(GitWarningText);

    /// <summary>空工作目录始终阻止发送，新会话必须显式选择目录。</summary>
    public bool HasWorkDir => !string.IsNullOrWhiteSpace(WorkDir);

    /// <summary>输入区阻止原因；null 表示 Git 前置条件满足。</summary>
    public string? SendBlockedReason
    {
        get
        {
            if (!HasWorkDir) return Strings.Chat_SelectWorkDirFirst;
            if (IsDetachedHead) return Strings.Chat_DetachedHeadBlocked;
            if (IsEmptyRepository) return Strings.Chat_FirstCommitRequired;
            return WorkspaceBlockedReason;
        }
    }

    /// <summary>其他分支的活动会话占用同一工作区时给出明确原因。</summary>
    public string? WorkspaceBlockedReason
    {
        get
        {
            if (CurrentSession is not { } current || IsSessionRunning(current.Id)) return null;
            var currentBranch = !string.IsNullOrWhiteSpace(current.BranchName)
                ? current.BranchName
                : CurrentBranch;
            if (string.IsNullOrWhiteSpace(currentBranch)) return null;

            var blocker = _chatService.Sessions.FirstOrDefault(s =>
                s.Id != current.Id && IsSessionRunning(s.Id) &&
                !string.Equals(s.BranchName, currentBranch, StringComparison.OrdinalIgnoreCase));
            return blocker is null
                ? null
                : string.Format(Strings.Chat_BranchOccupied,
                    string.IsNullOrWhiteSpace(blocker.DisplayTitle) ? blocker.Id : blocker.DisplayTitle,
                    string.IsNullOrWhiteSpace(blocker.BranchName) ? Strings.Chat_BranchUnknown : blocker.BranchName);
        }
    }

    public bool HasSendBlockedReason => !string.IsNullOrEmpty(SendBlockedReason);

    /// <summary>排队中的回合会被 Core 队列串行执行, 期间禁止再发消息以免排成长队且打断会话内串行语义。</summary>
    public bool CanSendMessage => !IsSending && !IsQueued && !HasSendBlockedReason &&
                                  (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0);

    partial void OnWorkDirChanged(string value)
    {
        ReleaseWorkerOnWorkDirChange(value);
        OnPropertyChanged(nameof(HasWorkDir));
        GitPanel.SetWorkspace(value);
        StatusPanel.SetWorkspace(value);
        RefreshWorkspaceContext();
        NotifySendState();
    }

    /// <summary>
    /// 同一会话换了工作目录: 释放<b>旧目录</b>的 Worker 引用。
    /// 下一次工具调用会按新目录自动 <c>AcquireAsync</c> 到新 Worker, 不需要额外 API。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不能只看「值变了就 Release 当前会话」</b>: <see cref="WorkDir"/> 的赋值点不止「用户改目录」——
    /// 切会话(构造函数里 CurrentSessionChanged 处理器)与新建会话也会赋值, 那时当前值属于<b>另一个</b>会话,
    /// 按当前会话去 -1 就是减错了别人的引用, 好在该目录里别人的 Worker 被误关。</para>
    /// <para>这里的判据是「会话上已持久化的目录仍等于被替换掉的那个值」:
    /// ① 用户在界面改目录时, <c>ChatSession.WorkDir</c> 要到发消息时才落盘(<c>SendMessage</c> 里才调
    /// <c>SetSessionWorkDir</c>), 所以它此刻还是旧目录, 条件成立;
    /// ② 切会话时 <c>CurrentSession</c> 已经是新会话、它的 <c>WorkDir</c> 就等于新值, 条件不成立, 不会误减。</para>
    /// <para><b>与删除会话的 Release 不重复</b>: 那条路径走的是 <see cref="AppShell.ReleaseSessionResources"/>,
    /// 且删完之后不会再有本会话的目录赋值事件(会话已不在列表里)。</para>
    /// <para>⚠ 已知交互: 在**本会话正在跑回合**时改目录, 旧目录的 Worker 会被立刻关闭, 此刻正在旧目录里
    /// 执行的那次工具调用会以一条明确的工具错误收场(而不是静默挂起)。这是「立即释放」的代价:
    /// 不释放的话旧目录的引用计数永远归不了零(该会话还活着, 60s 对账也回收不到它), 残留更久。</para>
    /// </remarks>
    private void ReleaseWorkerOnWorkDirChange(string newWorkDir)
    {
        var sessionId = _currentSessionId;
        var previous = CurrentSession?.WorkDir;
        if (string.IsNullOrEmpty(sessionId) ||
            string.IsNullOrWhiteSpace(previous) ||
            IsSameWorkDir(previous, newWorkDir)) // 复用同目录判定: 归一化后相同(如 /repo 与 /repo/)不算换目录
        {
            return;
        }

        _runtime.Workers?.Release(sessionId, previous);
    }

    partial void OnIsSendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendMessage));
        SendMessageCommand.NotifyCanExecuteChanged();
        ClearMessagesCommand.NotifyCanExecuteChanged();
    }

    partial void OnInputTextChanged(string value)
    {
        if (!_applyingHistory)
        {
            HistoryUserEdited();
        }

        NotifySendState();
    }

    private void NotifySendState()
    {
        OnPropertyChanged(nameof(CanSendMessage));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    // TODO(async 化, 依赖 GitService 的 async 版): 本方法在 UI 线程同步拉起 git 进程
    // (ResolveContext + IsClean, 约 2 个进程), 最坏可冻结 UI 数百毫秒。调用点遍布
    // OnWorkDirChanged / EndSessionRun / 切会话 / 回滚 / Fork, 迁移时必须逐个改为 await。
    private void RefreshWorkspaceContext()
    {
        var context = HasWorkDir
            ? _runtime.GitService.ResolveContext(WorkDir)
            : new GitWorkspaceContext
            {
                WorkDir = string.Empty,
                RepositoryRoot = string.Empty,
                BranchName = string.Empty,
                IsValidRepo = false,
                IsDetachedHead = false,
                IsEmptyRepo = false
            };
        CurrentRepositoryRoot = context.RepositoryRoot;
        IsDirty = context.IsValidRepo && !_runtime.GitService.IsClean(context).Succeeded;
        IsEmptyRepository = context.IsValidRepo && context.IsEmptyRepo;
        CurrentBranch = context.BranchName;
        IsDetachedHead = context.IsValidRepo && context.IsDetachedHead;
        OnPropertyChanged(nameof(RepositoryRootText));
        OnPropertyChanged(nameof(BranchText));
        OnPropertyChanged(nameof(GitWarningText));
        OnPropertyChanged(nameof(HasGitWarning));
        OnPropertyChanged(nameof(WorkspaceBlockedReason));
        OnPropertyChanged(nameof(SendBlockedReason));
        OnPropertyChanged(nameof(HasSendBlockedReason));
        NotifySendState();
    }

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
        CurrentSession = _chatService.CurrentSession;
        _workDir = CurrentSession?.WorkDir ?? string.Empty;
        RefreshMessages();
        _currentSessionId = CurrentSession?.Id;

        AgentPanel = new AgentPanelViewModel();
        GitPanel = new GitPanelViewModel
        {
            CheckpointForker = ForkCheckpointRecordAsync,
            CurrentConversationPosition = GetCurrentConversationPosition
        };
        StatusPanel = new StatusPanelViewModel();
        SessionPanel = new SessionPanelViewModel(_chatService)
        {
            IsSessionRunning = IsSessionRunning,
            GetWorkspaceBlockReason = GetWorkspaceBlockReason,
            GetSessionSwitchBlockReason = GetSessionSwitchBlockReason
        };
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        GitPanel.SetWorkspace(WorkDir);
        StatusPanel.SetSession(_currentSessionId ?? string.Empty);
        StatusPanel.SetWorkspace(WorkDir);

        // 排队计数轮询: PendingTurnCount 无事件可订阅, 仅在确有排队时运行
        _queuedPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _queuedPollTimer.Tick += (_, _) => RefreshQueuedTurnCount();

        _chatService.CurrentSessionChanged += (_, session) =>
        {
            SaveSessionInputState(_currentSessionId);
            CurrentSession = session;
            RefreshMessages();
            _currentSessionId = session?.Id;
            RestoreSessionInputState(_currentSessionId);
            UpdateCurrentSendingState();
            CanContinue = false;
            // 切换会话时恢复绑定目录；新会话为空，必须显式重新选择。
            WorkDir = session?.WorkDir ?? string.Empty;
            // 必须先切换活动会话: _runtime.Engine 是 Sessions.ActiveEngine 的转发属性,
            // SetSession 内部才调用 SetActiveSession。顺序颠倒会把新会话历史写进上一个会话的引擎。
            AgentPanel.SetSession(_currentSessionId ?? string.Empty);
            _runtime.Engine.RebuildConversation(session?.Messages ?? []);
            GitPanel.SetWorkspace(WorkDir);
            StatusPanel.SetSession(_currentSessionId ?? string.Empty);
            StatusPanel.SetWorkspace(WorkDir);
            RefreshContextUsage();
            RefreshWorkspaceContext();
            // 排队数按会话隔离: 切会话后必须跟着换, 否则会拿上一个会话的计数挡住发送
            RefreshQueuedTurnCount();
            NotifySendState();
        };
        _chatService.MessageAdded += (_, msg) =>
        {
            // 为什么需要显式回 UI 线程: 本处理器直接写 Messages(显示集合)并整集合重建,
            // 而它此前只在"所有 AddMessage 调用点恰好都在 UI 线程"这一**隐式契约**下成立。
            // 一旦将来某个调用点来自后台线程, 就会在非 UI 线程改 ObservableCollection(崩溃)。
            // 显式 Post 比隐式约定可靠: 契约落在代码里, 不依赖调用方自觉。
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnMessageAdded(msg));
                return;
            }

            OnMessageAdded(msg);
        };

        _runtime.Assignments.AssignmentChanged += _ =>
            Dispatcher.UIThread.Post(() => AgentPanel.RefreshAssignments());

        _runtime.Engine.OnEvent += OnEngineUsageRecorded;

        AppShell.Instance.DataChanged += OnShellDataChanged;

        RefreshActiveModel();
        RefreshProviders();
        RefreshThinkingOptions();
        RefreshContextUsage();
        RefreshWorkspaceContext();
        // 轮询表启停条件自指 QueuedTurnCount, 首帧必须由外部点一次火:
        // 缺这一句, 首次进入聊天页时计数恒为 0 → 轮询表永不启动 → 期间产生的排队要等
        // 下一次切会话/回合结束才被纠正。
        RefreshQueuedTurnCount();
    }

    /// <summary>消息新增回调体(始终在 UI 线程执行)。</summary>
    private void OnMessageAdded(ChatMessage msg)
    {
        // Core 事件不带 sessionId, 按消息归属会话判定: AddMessage 是"追加后同步触发",
        // 故该消息此刻必然是所属会话 Messages 的末位。
        // 归属会话正在流式时, 显示列表已由对应 RunTurnCoreAsync 的流式占位气泡维护, 不能再追加一条。
        // 不能沿用"任一会话在跑就整体忽略": A 会话后台流式时用户在 B 会话发的消息也会被吞掉,
        // B 少一条气泡, 下一条消息的增量判定必然失败 → 退化成整集合替换(容器全量重建)。
        var owner = _chatService.Sessions.FirstOrDefault(s =>
            s.Messages.Count > 0 && ReferenceEquals(s.Messages[^1], msg));
        if (owner is not null && IsSessionRunning(owner.Id)) return;

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
    }

    private (string SessionId, int ConversationCutoff) GetCurrentConversationPosition() =>
        (_currentSessionId ?? string.Empty, (CurrentSession?.MessageCount ?? 0) - 1);

    private bool IsSessionRunning(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) && _runningSessionIds.Contains(sessionId);

    private string? GetWorkspaceBlockReason(ChatSession target)
    {
        if (IsSessionRunning(target.Id)) return null;
        var branch = !string.IsNullOrWhiteSpace(target.BranchName) ? target.BranchName : CurrentBranch;
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var blocker = _chatService.Sessions.FirstOrDefault(s =>
            s.Id != target.Id && IsSessionRunning(s.Id) &&
            !string.Equals(s.BranchName, branch, StringComparison.OrdinalIgnoreCase));
        return blocker is null ? null : string.Format(
            Strings.Chat_BranchOccupied,
            string.IsNullOrWhiteSpace(blocker.DisplayTitle) ? blocker.Id : blocker.DisplayTitle,
            string.IsNullOrWhiteSpace(blocker.BranchName) ? Strings.Chat_BranchUnknown : blocker.BranchName);
    }

    /// <summary>
    /// 任一活动会话运行时，只允许切换到与所有活动会话相同工作目录的会话。
    /// 同一 WorkDir 下继续沿用原有分支并发规则。
    /// </summary>
    private string? GetSessionSwitchBlockReason(string targetWorkDir)
    {
        if (_runningSessionIds.Count == 0) return null;

        var blocker = _chatService.Sessions.FirstOrDefault(session =>
            IsSessionRunning(session.Id) && !IsSameWorkDir(session.WorkDir, targetWorkDir));
        if (blocker is null) return null;

        return string.Format(
            Strings.Session_WorkDirSwitchBlocked,
            string.IsNullOrWhiteSpace(blocker.DisplayTitle) ? blocker.Id : blocker.DisplayTitle,
            string.IsNullOrWhiteSpace(blocker.WorkDir) ? Strings.Chat_WorkDirDefault : blocker.WorkDir,
            string.IsNullOrWhiteSpace(targetWorkDir) ? Strings.Chat_WorkDirDefault : targetWorkDir);
    }

    private static bool IsSameWorkDir(string? left, string? right) =>
        string.Equals(NormalizeWorkDir(left), NormalizeWorkDir(right), WorkDirComparison);

    private static string NormalizeWorkDir(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch
        {
            return value.Trim();
        }
    }

    private static StringComparison WorkDirComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private void SaveSessionInputState(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (string.IsNullOrEmpty(InputText)) _sessionDrafts.Remove(sessionId);
        else _sessionDrafts[sessionId] = InputText;
        if (PendingAttachments.Count == 0)
        {
            if (_sessionAttachments.Remove(sessionId, out var droppedEmpty))
            {
                DisposeAttachments(droppedEmpty, keep: null);
            }
        }
        else
        {
            var snapshot = PendingAttachments.ToList();
            // 旧值里可能有一部分就是当前正显示在待发送条带上的同一批对象(重复保存),
            // 整体释放会把还在界面上的缩略图一起释放掉, 所以只释放不在新快照里的
            if (_sessionAttachments.TryGetValue(sessionId, out var previous))
            {
                DisposeAttachments(previous, keep: snapshot);
            }
            _sessionAttachments[sessionId] = snapshot;
        }
    }

    /// <summary>释放 <paramref name="items"/> 中不在 <paramref name="keep"/> 里的附件位图。
    /// 附件缩略图是原生内存, 集合被丢弃时必须显式归还; 但同一批对象常常既在待发送条带上、
    /// 又在 <c>_sessionAttachments</c> 缓存里, 所以按差集释放而不是整体释放。</summary>
    private static void DisposeAttachments(IEnumerable<PendingImageAttachment>? items,
        IReadOnlyCollection<PendingImageAttachment>? keep)
    {
        if (items is null) return;
        foreach (var attachment in items)
        {
            if (keep is not null && keep.Contains(attachment)) continue;
            attachment.Dispose();
        }
    }

    private void RestoreSessionInputState(string? sessionId)
    {
        _applyingHistory = true;
        InputText = sessionId is not null && _sessionDrafts.TryGetValue(sessionId, out var draft)
            ? draft
            : string.Empty;
        _applyingHistory = false;
        // 恢复出来的同一批对象马上会加回 PendingAttachments, 绝不能整体释放;
        // 只释放"切会话后将被丢弃"的那部分
        var restoring = sessionId is not null && _sessionAttachments.TryGetValue(sessionId, out var cached)
            ? cached
            : null;
        DisposeAttachments(PendingAttachments, keep: restoring);
        PendingAttachments.Clear();
        if (restoring is not null)
        {
            foreach (var attachment in restoring) PendingAttachments.Add(attachment);
        }
        OnPropertyChanged(nameof(HasPendingAttachments));
        NotifySendState();
    }

    private void BeginSessionRun(string sessionId)
    {
        _runningSessionIds.Add(sessionId);
        UpdateCurrentSendingState();
        SessionPanel.RefreshRuntime();
    }

    private void EndSessionRun(string sessionId)
    {
        _runningSessionIds.Remove(sessionId);
        if (_turnCtsMap.Remove(sessionId, out var cts)) cts.Dispose();
        UpdateCurrentSendingState();
        SessionPanel.RefreshRuntime();
        RefreshWorkspaceContext();
    }

    private void UpdateCurrentSendingState()
    {
        IsSending = _currentSessionId is not null && IsSessionRunning(_currentSessionId);
        OnPropertyChanged(nameof(WorkspaceBlockedReason));
        OnPropertyChanged(nameof(SendBlockedReason));
        OnPropertyChanged(nameof(HasSendBlockedReason));
        NotifySendState();
        SessionPanel.RefreshRuntime();
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
        // Auto = 0, 而 Low = 1, 所以未配置上限时(ProviderConfig.GetMaxThinking 兜底为 Auto)
        // 不能直接把 cap 当上限用 —— 否则 for 循环一次都不进, 菜单只剩"关闭/自动"两项。
        // 语义上 Auto = "交回模型自身决定" = 本层不设上限, 因此按 Max 处理。
        var raw = CurrentMaxThinking();
        var cap = raw == ThinkingLevel.Auto ? ThinkingLevel.Max : raw;
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

    /// <summary>
    /// 引擎全局事件处理(用量落盘 / 自动压缩提示 / ask_user 应答 / 工具审批)。
    /// </summary>
    /// <remarks>
    /// 这里刻意订阅 <c>Engine.OnEvent</c>(即全局 Hub, 按活动会话过滤)而不是引擎的 LocalEvent:
    /// 非活动会话的用量由 <c>SessionRuntimeRegistry</c> 在 RawEvent 侧直接落盘,
    /// 两边都收全量事件会重复计数。
    /// </remarks>
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

        // 会话身份一律取事件自带的 Scope, 不读 UI 状态(CurrentSession/_currentSessionId):
        // 本回调在引擎线程触发, 读 UI 状态既是跨线程访问共享状态, 又会在用户切会话的瞬间
        // 把用量记到另一个会话名下。兜底与会话注册表的写法保持一致(标题空则回落会话 Id)。
        var usageSessionId = e.SessionId;
        var title = string.IsNullOrWhiteSpace(e.SessionTitle) ? usageSessionId : e.SessionTitle;
        UsageStatsService.RecordLlmUsage(
            usageSessionId, title,
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
        // 整集合替换前先让旧消息项释放其分段位图; 不释放则每切一次会话/刷新一次列表
        // 就会留下一批已无 UI 宿主的原生位图(终结器兜底只是延迟, 不是解决)
        foreach (var old in Messages) old.Deactivate();
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
        // AgentPanel 自己订阅 DataChanged 并合并刷新, 避免同一次 reload 重复重建列表。
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
        if (GetSessionSwitchBlockReason(string.Empty) is { } blockedReason)
        {
            AppendNotice(blockedReason);
            return;
        }

        _chatService.CreateSession();
        // 新会话必须显式选择自己的工作目录，禁止隐式继承上一会话上下文。
        WorkDir = string.Empty;
        SessionPanel.RefreshItems();
        RefreshMessages();
        _currentSessionId = CurrentSession?.Id;
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        StatusPanel.SetSession(_currentSessionId ?? string.Empty);
    }

    [RelayCommand]
    private void DeleteSession(string sessionId)
    {
        // 先取目录再删: 删完就再也问不到这个会话绑过哪个目录了(而 Worker 引用计数要按目录归零)
        var workDir = _chatService.Sessions.FirstOrDefault(s => s.Id == sessionId)?.WorkDir;
        _chatService.DeleteSession(sessionId);
        // Worker 引用 -1 + 移除会话运行时(引擎此前不会被释放, 删除后仍能跑后台回合)
        AppShell.Instance.ReleaseSessionResources(sessionId, workDir);
        // 清会话级状态: 删除"正在运行"的会话时, _runningSessionIds/取消源/草稿/附件会变成悬挂条目 ——
        // 后续只有按"新会话 id"的刷新才会纠正它们, 这些条目永远残留(草稿与附件还会随内存单调增长)。
        // 取消源一并释放, 避免漏掉的回合继续持有已删会话的引用。
        _runningSessionIds.Remove(sessionId);
        if (_turnCtsMap.Remove(sessionId, out var cts)) cts.Dispose();
        _sessionDrafts.Remove(sessionId);
        // 该会话的待恢复附件随会话一起消失, 位图要归还(否则删会话后原生内存永久泄漏)
        if (_sessionAttachments.Remove(sessionId, out var droppedAttachments))
        {
            DisposeAttachments(droppedAttachments, keep: null);
        }

        AppShell.Instance.NotifyDataChanged(); // 主页会话分布移除该会话
        SessionPanel.RefreshItems();
        RefreshMessages();
        _currentSessionId = CurrentSession?.Id;
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        StatusPanel.SetSession(_currentSessionId ?? string.Empty);
    }

    [RelayCommand]
    private void SwitchSession(string sessionId)
    {
        var target = _chatService.Sessions.FirstOrDefault(session => session.Id == sessionId);
        if (target is null) return;
        if (GetSessionSwitchBlockReason(target.WorkDir) is { } blockedReason)
        {
            AppendNotice(blockedReason);
            return;
        }

        _chatService.SwitchSession(sessionId);
    }

    /// <summary>清空当前会话消息; 生成中不可清(流式占位气泡正挂在显示列表上, 清掉会与引擎历史不一致)。</summary>
    [RelayCommand(CanExecute = nameof(CanClearMessages))]
    private void ClearMessages()
    {
        if (!CanClearMessages() || CurrentSession is null) return;
        _chatService.ClearMessages(CurrentSession.Id);
        RefreshMessages();
        _runtime.Engine.ClearConversation();
        CanContinue = false;
    }

    private bool CanClearMessages() => !IsSending;

    /// <summary>从用户消息的自动检查点分叉。</summary>
    [RelayCommand]
    private async Task ForkUserCheckpointAsync(ChatItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.CheckpointId) || CurrentSession is null) return;
        var context = _runtime.GitService.ResolveContext(CurrentSession.WorkDir);
        var record = _runtime.Checkpoints.Get(context.RepositoryRoot, item.CheckpointId);
        if (record is null) return;
        var detail = new CheckpointDetail
        {
            CheckpointId = record.Id,
            Label = record.Label,
            ShortSha = record.ShortSha,
            FullSha = record.CommitSha,
            BranchName = record.BranchName,
            WorkDir = record.WorkDir,
            Source = record.Source,
            CreatedAt = record.CreatedAt,
            SessionId = record.SessionId,
            ConversationCutoff = record.ConversationCutoff
        };
        await ExecuteForkAsync(detail, null);
    }

    /// <summary>检查点卡片 Fork。</summary>
    [RelayCommand]
    private async Task ForkCheckpointAsync(SegmentItemViewModel segment)
    {
        if (segment.Checkpoint is { } detail) await ExecuteForkAsync(detail, segment);
    }

    /// <summary>回滚检查点：先选择方式，再经过独立确认窗口才调用 Core。</summary>
    [RelayCommand]
    private async Task RollbackCheckpointAsync(SegmentItemViewModel segment)
    {
        if (IsSending || segment.Checkpoint is not { } detail ||
            string.IsNullOrWhiteSpace(detail.CheckpointId) || GetMainWindow() is not { } owner)
        {
            return;
        }

        var choice = await Views.RollbackChoiceDialog.ShowAsync(
            owner,
            string.Format(Strings.Checkpoint_RollbackHeading, detail.ShortSha, detail.Label),
            Strings.Checkpoint_RollbackChooseDescription,
            Strings.Checkpoint_ContinueReset,
            Strings.Checkpoint_ContinueRevert);
        if (choice is null) return;

        var isReset = choice == CheckpointRollbackMode.ResetHard;
        var mode = isReset ? Strings.Checkpoint_ResetHard : Strings.Checkpoint_Revert;
        var impact = isReset
            ? Strings.Checkpoint_ResetHardConfirm
            : Strings.Checkpoint_RevertConfirm;
        var confirmed = await Views.ConfirmDialog.ShowAsync(
            owner,
            Strings.Checkpoint_RollbackConfirmTitle,
            string.Format(Strings.Checkpoint_RollbackConfirmMessage, mode, detail.ShortSha, impact),
            Strings.Checkpoint_ExecuteRollback,
            Strings.Settings_Cancel);
        if (!confirmed) return;

        var context = _runtime.GitService.ResolveContext(WorkDir);
        var record = _runtime.Checkpoints.Get(context.RepositoryRoot, detail.CheckpointId);
        if (record is null)
        {
            segment.SetCheckpointActionError(Strings.Checkpoint_RecordMissing);
            return;
        }

        var result = isReset
            ? _runtime.GitService.ResetHardToCheckpoint(context, record)
            : _runtime.GitService.RevertToCheckpoint(context, record);
        if (result.Succeeded)
        {
            segment.MarkCheckpointRolledBack(isReset
                ? CheckpointRollbackMode.ResetHard
                : CheckpointRollbackMode.Revert);
            AppShell.Instance.NotifyDataChanged();
            RefreshWorkspaceContext();
        }
        else
        {
            segment.SetCheckpointActionError(string.Format(
                Strings.Checkpoint_RollbackFailed, result.Stderr.Trim()));
        }
    }

    private Task ForkCheckpointRecordAsync(GitCheckpointRecord record)
    {
        var detail = new CheckpointDetail
        {
            CheckpointId = record.Id,
            Label = record.Label,
            ShortSha = record.ShortSha,
            FullSha = record.CommitSha,
            BranchName = record.BranchName,
            WorkDir = record.WorkDir,
            Source = record.Source,
            CreatedAt = record.CreatedAt,
            SessionId = record.SessionId,
            ConversationCutoff = record.ConversationCutoff
        };
        return ExecuteForkAsync(detail, null);
    }

    private async Task ExecuteForkAsync(CheckpointDetail detail, SegmentItemViewModel? card)
    {
        if (CurrentSession is null || GetMainWindow() is not { } owner) return;
        var occupied = IsSending;
        var suggestion = $"fork/{detail.CheckpointId}-{DateTime.Now:MMdd-HHmm}";
        var request = await Views.ForkCheckpointDialog.ShowAsync(
            owner,
            string.Format(Strings.Fork_Heading, detail.ShortSha),
            Strings.Fork_Description,
            suggestion,
            copyNewSessionByDefault: false,
            occupied,
            Strings.Fork_SessionOccupied);
        if (request is null) return;

        var source = CurrentSession;
        var context = _runtime.GitService.ResolveContext(source.WorkDir);
        var record = _runtime.Checkpoints.Get(context.RepositoryRoot, detail.CheckpointId);
        if (record is null)
        {
            card?.SetCheckpointActionError(Strings.Checkpoint_RecordMissing);
            return;
        }

        var gitResult = _runtime.GitService.Fork(context, record, request.BranchName);
        if (!gitResult.Succeeded)
        {
            var message = string.Format(Strings.Fork_Failed, gitResult.Stderr.Trim());
            card?.SetCheckpointActionError(message);
            AppendNotice(message);
            return;
        }

        // 会话截断/复制失败时不要让异常逃逸: 此时 git 分支已切换, 状态已不一致,
        // 崩溃只会掩盖问题, 应给出可读提示并同步刷新界面, 让用户看到真实状态。
        try
        {
            if (request.SessionMode == Views.ForkSessionMode.CopyNewSession)
            {
                CopySessionAtCheckpoint(source, record.ConversationCutoff, request.BranchName, context.RepositoryRoot);
            }
            else
            {
                TruncateCurrentSessionAtCheckpoint(record.ConversationCutoff, request.BranchName, context.RepositoryRoot);
            }
        }
        catch (Exception ex)
        {
            AIShikikan.Core.Logging.Log.Error("Fork", ex, $"会话截断失败(分支 {request.BranchName} 已创建)");
            AppendNotice(Strings.Fork_TruncateFailed);
        }

        SessionPanel.RefreshItems();
        RefreshMessages();
        // 必须先切换活动会话: _runtime.Engine 是 Sessions.ActiveEngine 的转发属性,
        // SetSession 内部才调用 SetActiveSession(复制新会话模式下当前会话已改变)。
        AgentPanel.SetSession(_currentSessionId ?? string.Empty);
        _runtime.Engine.RebuildConversation(CurrentSession?.Messages ?? []);
        RefreshWorkspaceContext();
        AppShell.Instance.NotifyDataChanged();
        var done = string.Format(Strings.Fork_Completed, detail.ShortSha, request.BranchName);
        card?.SetCheckpointActionSuccess(done);
        if (card is null) AppendNotice(done);
    }

    /// <summary>
    /// 同会话 Fork：通过 Core 真正截断并持久化 [0, cutoff] 对话。
    /// cutoff &lt; 0 表示检查点创建时对话尚为空(ConversationCutoff = MessageCount - 1),
    /// 此时应清空全部消息而不是把非法索引传给 TruncateMessages(会返回 false)。
    /// </summary>
    private void TruncateCurrentSessionAtCheckpoint(int cutoff, string branch, string repositoryRoot)
    {
        if (CurrentSession is not { } session) return;

        if (cutoff < 0)
        {
            _chatService.ClearMessages(session.Id);
        }
        else if (cutoff < session.Messages.Count && !_chatService.TruncateMessages(session.Id, cutoff))
        {
            throw new InvalidOperationException(Strings.Fork_TruncateFailed);
        }

        _chatService.SetSessionRepositoryInfo(session.Id, repositoryRoot, branch);
    }

    /// <summary>复制新会话：Core 复制 cutoff 前缀，UI 额外复制 AOT 持久化的 Roster。</summary>
    private void CopySessionAtCheckpoint(ChatSession source, int cutoff, string branch, string repositoryRoot)
    {
        ChatSession? copy;
        if (cutoff < 0)
        {
            copy = _chatService.CreateSession(source.Title);
        }
        else
        {
            copy = _chatService.ForkSession(source.Id, cutoff, source.Title);
        }
        if (copy is null) throw new InvalidOperationException(Strings.Fork_TruncateFailed);
        _chatService.SetSessionWorkDir(copy.Id, source.WorkDir);
        _chatService.SetSessionRepositoryInfo(copy.Id, repositoryRoot, branch);
        RosterConfigService.Save(copy.Id, RosterConfigService.Load(source.Id));
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
        BeginSessionRun(session.Id);
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
                Messages[pos].Dispose();
                Messages.RemoveAt(pos);
            }

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
            // 移除前显式释放: 这些消息项不会有人再引用, 终结器兜底只是把泄漏推迟到 GC
            Messages[i].Dispose();
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

    // TODO(async 化, 依赖 GitService 的 async 版): 在 UI 线程同步拉起约 3 个 git 进程
    // (GetHeadSha + MarkCheckpoint 的 cat-file/tag -l/tag)。它是发送路径的一环,
    // 与 SendMessage 里的 ResolveContext、RefreshWorkspaceContext 合计约 8 个进程卡在 UI 线程。
    private GitCheckpointRecord? MarkAutomaticCheckpoint(
        GitWorkspaceContext context, ChatSession session, string userText)
    {
        var head = _runtime.GitService.GetHeadSha(context.RepositoryRoot);
        if (string.IsNullOrWhiteSpace(head)) return null;
        var id = Guid.NewGuid().ToString("N")[..8];
        var record = new GitCheckpointRecord
        {
            Id = id,
            RepositoryRoot = context.RepositoryRoot,
            WorkDir = context.WorkDir,
            BranchName = context.BranchName,
            CommitSha = head,
            TagName = $"ai-shikikan/checkpoint/{id}",
            SessionId = session.Id,
            ConversationCutoff = session.MessageCount - 1,
            Source = GitCheckpointSource.AutoUserMessage,
            Label = string.IsNullOrWhiteSpace(userText)
                ? string.Format(Strings.Checkpoint_AutoLabel, session.MessageCount + 1)
                : userText.Length <= 48 ? userText : userText[..48] + "…",
            CreatedAt = DateTime.Now
        };
        var result = _runtime.GitService.MarkCheckpoint(context, record);
        if (!result.Succeeded)
        {
            AppendNotice(string.Format(Strings.Checkpoint_AutoFailed, result.Stderr.Trim()));
            return null;
        }
        return record;
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private void SendMessage()
    {
        var hasAttachments = PendingAttachments.Count > 0;
        if (string.IsNullOrWhiteSpace(InputText) && !hasAttachments) return;
        if (!HasWorkDir) return;
        if (CurrentSession is null) return;

        var sessionId = CurrentSession.Id;
        var content = InputText;
        InputText = string.Empty;
        _sessionDrafts.Remove(sessionId);
        _sessionAttachments.Remove(sessionId);
        HistoryReset(); // 发送后该条已进入会话历史, 退出浏览态

        // 发送路径上的 UI 线程同步 git 调用(合计约 8 个进程: ResolveContext 3 + 检查点 3 + 上下文刷新 2)。
        // TODO(async 化): 依赖 GitService 的 async 版; 迁移时注意检查点失败要降级为提示而非中断发送。
        var context = _runtime.GitService.ResolveContext(WorkDir);
        CurrentSession.RepositoryRoot = context.RepositoryRoot;
        CurrentSession.BranchName = context.BranchName;
        var checkpoint = MarkAutomaticCheckpoint(context, CurrentSession, content);

        // 附件转图片分段(与引擎侧 ChatImagePart 同源), 发送后清空待发送条带
        var attachments = PendingAttachments.ToList();
        PendingAttachments.Clear();
        // 会话输入缓存里也清掉: 这些附件已随消息发出, 留着只会在切回本会话时又冒出来
        if (_currentSessionId is { } sending) _sessionAttachments.Remove(sending);

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

        // Base64 字符串已提取进分段, 附件对象(含缩略图位图)不再需要。
        // 注意 FromBitmap 不接管入参所有权, 所以剪贴板那张由 ChatPageView.axaml.cs 负责 Dispose
        ImageAttachmentService.DisposeAll(attachments);

        if (!string.IsNullOrWhiteSpace(content))
        {
            segments.Add(new MessageSegment { Kind = MessageSegmentKind.Text, Content = content });
        }

        // AddMessage 同步触发 MessageAdded 处理器完成列表重建, 无需在此重复 RefreshMessages
        var userMessage = _chatService.AddMessage(sessionId, MessageRole.User, segments);
        if (checkpoint is not null)
        {
            _chatService.SetMessageCheckpoint(sessionId, userMessage.Id, checkpoint.Id);
            userMessage.CheckpointId = checkpoint.Id;
            if (Messages.LastOrDefault()?.MessageId == userMessage.Id)
            {
                Messages[^1].SetCheckpointId(checkpoint.Id);
            }
        }
        // 记录会话绑定的工作目录(用于按目录整理会话与切换会话时恢复)
        _chatService.SetSessionWorkDir(CurrentSession.Id, WorkDir);

        if (isFirstMessage && !string.IsNullOrWhiteSpace(content))
        {
            _ = AutoGenerateTitleAsync(CurrentSession, content);
        }

        CanContinue = false;
        BeginSessionRun(sessionId);
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
            NotifySendState();
        }
    }

    /// <summary>把剪贴板位图加入待发送附件。</summary>
    /// <summary>收下调用方交来的位图并转成附件。<b>不接管 <paramref name="bmp"/> 的所有权</b>:
    /// 调用方(剪贴板路径)必须自己 Dispose, 否则这里提前返回时位图就彻底没人引用了。</summary>
    public void AddImageFromBitmap(Avalonia.Media.Imaging.Bitmap bmp, string name)
    {
        if (PendingAttachments.Count >= ImageAttachmentService.MaxAttachments) return;

        var att = ImageAttachmentService.FromBitmap(bmp, name);
        if (att is null) return;

        PendingAttachments.Add(att);
        OnPropertyChanged(nameof(HasPendingAttachments));
        NotifySendState();
    }

    /// <summary>移除一个待发送附件。</summary>
    [RelayCommand]
    private void RemoveAttachment(PendingImageAttachment attachment)
    {
        PendingAttachments.Remove(attachment);
        // 已从待发送条带摘掉, 缩略图位图可以归还; 从会话输入缓存里一并摘掉, 避免它悬着到会话销毁
        if (_currentSessionId is { } sid && _sessionAttachments.TryGetValue(sid, out var cached))
        {
            _sessionAttachments[sid] = cached.Where(a => !ReferenceEquals(a, attachment)).ToList();
        }
        attachment.Dispose();
        OnPropertyChanged(nameof(HasPendingAttachments));
        NotifySendState();
    }

    /// <summary>手动终止当前生成(发送/工具循环均会收到取消信号)。</summary>
    [RelayCommand]
    private void StopGeneration()
    {
        if (!IsSending || _currentSessionId is not { } sessionId) return;
        if (_turnCtsMap.TryGetValue(sessionId, out var cts)) cts.Cancel();
    }

    /// <summary>继续输出: 以固定指令驱动引擎从中断处续写(不新增用户气泡)。</summary>
    [RelayCommand]
    private void ContinueOutput()
    {
        if (!CanContinue || IsSending || _currentSessionId is not { } sessionId) return;
        CanContinue = false;
        BeginSessionRun(sessionId);
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
        var sessionId = _currentSessionId ?? string.Empty;
        var assistantItem = new ChatItemViewModel(MessageRole.Assistant);
        if (CurrentSession?.Id == sessionId) Messages.Add(assistantItem);

        // 线性时间线: 分段按事件到达顺序排列(思考/正文/工具交替), UI 顺序 = 实际发生顺序
        // 线程模型(重要隐式约定, 见 B5 报告): 以下集合**只在 UI 线程**被访问。
        // 引擎线程(含 run_subagents 的 Task.WhenAll 回调, 无 SyncContext)只往 pendingEvents 入队,
        // 由 FlushUi 统一排空 —— 彻底消除此前"引擎线程写 List/Dictionary + UI 线程读"的跨线程共享。
        // ⚠ 本方法整体依赖"await 不加 ConfigureAwait(false) → 续体留在 UI 线程"这一约定:
        // 若下方 EnqueueTurnAsync/RunTurnAsync 任一处改成 ConfigureAwait(false),
        // ProcessEvent/RebuildSegments 就会在后台线程改 ObservableCollection → 崩溃。
        var entries = new List<TimelineEntry>();
        var toolData = new List<(string Id, string Name, string Args)>();       // 工具调用, 按调用顺序
        var toolOutputs = new Dictionary<string, StringBuilder>();
        var toolFinished = new Dictionary<string, (string Result, bool IsError, string? StepId, ToolCardDetail? Detail)>();

        // 引擎线程 → UI 线程的事件队列(ConcurrentQueue 保证入队本身线程安全)
        var pendingEvents = new ConcurrentQueue<AgentEngineEvent>();
        var flushScheduled = 0; // 0=空闲 1=已排 UI 刷新(用 Interlocked 做节流标记)

        // 时间节流(与上面的"同一 tick 只刷一次"正交): Markdown 全文重解析是 CPU 密集且随
        // 文本长度线性增长的工作, 逐 token 触发会让一轮回复的总代价变成 O(n²)(n = 回复字符数)。
        // 故两次 RebuildSegments 之间至少间隔 StreamRebuildIntervalMs。
        const int StreamRebuildIntervalMs = 60;
        var rebuildTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(StreamRebuildIntervalMs)
        };
        long lastRebuildStamp = 0; // 0 = 本轮还没重建过分段(首次立即刷新)

        // 引擎线程回调: 只入队, 首次入队时排一次 UI 刷新
        void OnEngineEvent(AgentEngineEvent e)
        {
            pendingEvents.Enqueue(e);
            if (Interlocked.CompareExchange(ref flushScheduled, 1, 0) == 0)
            {
                Dispatcher.UIThread.Post(FlushUi);
            }
        }

        // UI 线程: 消费单个引擎事件(原 OnEngineEvent 的 switch 体)
        void ProcessEvent(AgentEngineEvent e)
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
        }

        // UI 线程同步: 排空事件队列后重建分段(受时间门限节流)
        void FlushUi() => DrainAndRebuild(force: false);

        // UI 线程同步: 忽略时间门限的强制刷新。回合收尾(成功/取消/异常)与节流表回调必须走这里,
        // 否则 Segments 落后于 entries, 末尾按 Segments 持久化的就是被截断的助手消息(数据损坏)。
        void FlushUiNow() => DrainAndRebuild(force: true);

        void DrainAndRebuild(bool force)
        {
            Dispatcher.UIThread.CheckAccess();

            Interlocked.Exchange(ref flushScheduled, 0);

            // 必须先排空再决定是否续排: 排空期间引擎线程可能又入队了新事件
            while (pendingEvents.TryDequeue(out var e))
            {
                ProcessEvent(e);
            }

            if (!pendingEvents.IsEmpty)
            {
                // 本轮排空后仍有积压: 续排一次刷新(期间新事件的入队者也会看到 flushScheduled=0 自行排)
                if (Interlocked.CompareExchange(ref flushScheduled, 1, 0) == 0)
                {
                    Dispatcher.UIThread.Post(FlushUi);
                }

                return;
            }

            // 距上次重建不足门限: 本次只累积不刷新, 由节流表补一次(保证最终一定会呈现)。
            // 只在这里生效: 回合收尾走 FlushUiNow, 不会漏掉最后一次刷新。
            var now = Stopwatch.GetTimestamp();
            if (!force && lastRebuildStamp != 0 &&
                (now - lastRebuildStamp) * 1000L / Stopwatch.Frequency < StreamRebuildIntervalMs)
            {
                if (!rebuildTimer.IsEnabled) rebuildTimer.Start();
                return;
            }

            lastRebuildStamp = now;
            rebuildTimer.Stop();
            RebuildSegments();
        }

        // UI 线程同步: 按 entries 线性顺序补齐缺失分段(仅尾部追加)并覆盖最新内容
        void RebuildSegments()
        {
            Dispatcher.UIThread.CheckAccess();

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
                            if (fin.Detail is CheckpointDetail checkpoint)
                            {
                                en.Vm.CheckpointId = checkpoint.CheckpointId;
                            }
                            if (!string.IsNullOrEmpty(fin.StepId))
                            {
                                en.Vm.StepId = fin.StepId;
                            }
                        }

                        break;
                }
            }
        }

        // 节流表回调: 到点强制重建一次(绑在局部函数声明之后, 避免从 lambda 前向引用局部函数)
        rebuildTimer.Tick += (_, _) => FlushUiNow();

        // 会话运行时: 同一会话的多个回合必须经其队列串行执行(此前直接调 Engine.RunTurnAsync
        // 会并发写引擎对话历史这个普通 List, 是数据竞争)。
        // 优先按 sessionId 精确取, 取不到再退到活动会话; 两者皆 null 才直连兜底引擎。
        var runtime = _runtime.Sessions.TryGet(sessionId) ?? _runtime.Sessions.Active;
        var engine = runtime?.Engine ?? _runtime.Engine;

        // 订阅引擎的**本地事件出口**(LocalEvent)而不是 OnEvent:
        // OnEvent 在引擎带 Hub 时实际订阅的是全局 EngineEventHub, 而 Hub 只向"当前活动会话"
        // 投递, 于是 (a) 流式中切会话 → 旧会话增量被丢弃 → 下方按 Segments 持久化出被截断的
        // 助手消息(数据损坏); (b) 后台会话流式 → UI 完全无输出, 只剩整段 reply 兜底, 分段结构/
        // 思考过程/工具卡全丢。LocalEvent 绑定到本引擎、无条件触发, 两个方向都正确。
        // 注: 引擎每会话独立且同时只跑一个回合, 故这里收到的必然都是本会话事件, 无需再过滤。
        engine.LocalEvent += OnEngineEvent;
        var turnCts = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (_turnCtsMap.Remove(sessionId, out var previous)) previous.Dispose();
            _turnCtsMap[sessionId] = turnCts;
        }
        try
        {
            engine.Options.Thinking = SelectedThinking;
            engine.Options.WorkDir = string.IsNullOrWhiteSpace(WorkDir) ? null : WorkDir;
            engine.Options.IsPlanMode = IsPlanMode;

            // 把当前全局 LLM 路由物化进本回合(引擎据此填 ToolContext.ProviderId, 供子代理输出压缩
            // 等"必须与主回合同 Provider"的子流程复用)。与上面三项同为回合级快照: 回合中途切 Provider
            // 不影响正在跑的回合, 下一回合自动跟上; 漏调只是退回"跟随全局", 不会把会话钉死在旧 Provider 上。
            _runtime.ApplyLlmRouting(engine);

            // 注意此处 await 不加 ConfigureAwait(false): 续体要留在 UI 线程, 下面的 FlushUiNow/
            // 分段追加都直接操作 ObservableCollection。见方法开头"线程模型"注释。
            var reply = runtime is not null
                ? await runtime.EnqueueTurnAsync(engineMessage, images, turnCts.Token)
                : await engine.RunTurnAsync(engineMessage, images, turnCts.Token);
            FlushUiNow(); // 兜底同步一次, 确保最终增量已呈现(必须强制, 否则会漏掉门限内的最后一批)

            // 全程无流式文本时(如纯最终回复), 将整体回复作为正文分段补到时间线末尾
            if (!string.IsNullOrWhiteSpace(reply) &&
                assistantItem.Segments.All(s => s.Kind != MessageSegmentKind.Text))
            {
                var fallback = new TimelineEntry(MessageSegmentKind.Text);
                fallback.Sb.Append(reply);
                entries.Add(fallback);
                FlushUiNow(); // 强制: 否则兜底正文可能还没进 Segments, 会被后面的持久化丢掉
            }
        }
        catch (OperationCanceledException)
        {
            // 用户主动终止: 保留已生成的部分内容并允许继续输出
            FlushUiNow(); // 强制: 取消多发生在时间门限内, 不强制就会丢掉最后一批增量
            var note = new SegmentItemViewModel(MessageSegmentKind.Text);
            note.SetBody(Strings.Chat_StoppedNote);
            assistantItem.Segments.Add(note);
            CanContinue = true;
        }
        catch (Exception ex)
        {
            AIShikikan.Core.Logging.Log.Error("Session", ex, "回合执行失败");
            var err = new SegmentItemViewModel(MessageSegmentKind.Text);
            // TODO(i18n 收口): 该串为硬编码中文, 且把内部异常消息裸示给用户。
            // 正确做法是区分"内部错误"(只记日志 + 通用提示)与"用户可见错误"(可展示细节),
            // 并抽成 Strings 键(如 Chat_TurnFailed)。本次未改: 加键需动共享文件 Resources/Strings*.resx。
            err.SetBody($"⚠ 发生错误: {ex.Message}");
            assistantItem.Segments.Add(err);
        }
        finally
        {
            engine.LocalEvent -= OnEngineEvent;
            rebuildTimer.Stop();
            // 无会话归属时取消源无处安放(不进字典), 在此释放避免泄漏
            if (string.IsNullOrEmpty(sessionId)) turnCts.Dispose();
            // 回合出队后排队数可能已归零: 先同步一次, 不必等轮询表下一拍(轮询表仅在 >0 时运行)
            RefreshQueuedTurnCount();
        }

        // 持久化为结构化分段(读的是 Segments, 故上面每次收尾刷新都必须强制执行)
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
                            CheckpointId = seg.CheckpointId,
                            StepId = seg.StepId,
                            Detail = seg.ToolCardDetail
                        }
                    });
                    break;
            }
        }

        if (segments.Count > 0 && !string.IsNullOrEmpty(sessionId))
        {
            _chatService.AddMessage(sessionId, MessageRole.Assistant, segments);
        }

        if (!string.IsNullOrEmpty(sessionId)) EndSessionRun(sessionId);
        else IsSending = false;
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