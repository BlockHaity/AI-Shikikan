using System.Collections.ObjectModel;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Engine;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>右侧信息侧栏(单一合并视图): 子Agent列表 + 分派列表 + 状态(与 GUI StatusPanel 及 /status 同数据源) + Git 信息。
/// 原 Agent / 状态 / Git 三个标签页已合并为本视图一次性展示, 不再切换。
/// 窄窗口时由外部按 <see cref="MinWindowWidth"/> 自动隐藏。</summary>
public sealed class SidebarView : View
{
    public const int DefaultWidth = 44;

    /// <summary>终端宽度低于该值(列)时自动隐藏侧栏, 保证对话区可读。</summary>
    public const int MinWindowWidth = 96;

    private readonly CommanderRuntime _runtime;
    private readonly ChatService _chatService;
    private readonly IUiOutput _ui;

    // 子 Agent 列表
    private readonly MarkupLabelView _agentHeader = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
    private readonly ListView _agentList;
    private readonly ObservableCollection<string> _agentSource = [];
    private readonly List<bool> _agentRunning = [];

    // 分派列表
    private readonly MarkupLabelView _assignHeader = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly ListView _assignList;
    private readonly ObservableCollection<string> _assignSource = [];
    private readonly List<Assignment> _assignments = [];

    // 状态块
    private readonly MarkupLabelView _modelLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _progressLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _contextLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _costLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _workspaceLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _branchLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _summaryLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };

    // Git 块
    private readonly MarkupLabelView _repoLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _gitBranchLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _changesLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _stepsLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };
    private readonly MarkupLabelView _commitLabel = new() { X = 0, Width = Dim.Fill(), Height = 1 };

    private static readonly Attribute GreyAttr = new(Color.Gray, MarkupConverter.DefaultBackground);
    private static readonly Attribute GreenAttr = new(Color.BrightGreen, MarkupConverter.DefaultBackground);
    private static readonly Attribute YellowAttr = new(Color.BrightYellow, MarkupConverter.DefaultBackground);
    private static readonly Attribute RedAttr = new(Color.BrightRed, MarkupConverter.DefaultBackground);

    public SidebarView(CommanderRuntime runtime, ChatService chatService, IUiOutput ui)
    {
        _runtime = runtime;
        _chatService = chatService;
        _ui = ui;

        // 自上而下: 头(1) + 子Agent列表(弹性) + 头(1) + 分派列表(4) + 状态块(7) + 空行(1) + Git块(5)
        _agentList = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(17)
        };
        _agentList.SetSource(_agentSource);
        _agentList.BorderStyle = LineStyle.None;
        _agentList.CanFocus = false;
        _agentList.RowRender += (_, e) =>
        {
            if (e.Row < _agentRunning.Count)
            {
                e.RowAttribute = _agentRunning[e.Row] ? YellowAttr : GreyAttr;
            }
        };

        _assignHeader.Y = Pos.Bottom(_agentList);
        _assignHeader.SetMarkup("[bold]分派列表[/]");

        _assignList = new ListView
        {
            X = 0,
            Y = Pos.Bottom(_assignHeader),
            Width = Dim.Fill(),
            Height = 4
        };
        _assignList.SetSource(_assignSource);
        _assignList.BorderStyle = LineStyle.None;
        _assignList.RowRender += OnAssignmentRowRender;
        _assignList.KeyDown += OnAssignmentKeyDown;

        // 状态块(7 行)
        _modelLabel.Y = Pos.Bottom(_assignList);
        _progressLabel.Y = Pos.Bottom(_assignList) + 1;
        _contextLabel.Y = Pos.Bottom(_assignList) + 2;
        _costLabel.Y = Pos.Bottom(_assignList) + 3;
        _workspaceLabel.Y = Pos.Bottom(_assignList) + 4;
        _branchLabel.Y = Pos.Bottom(_assignList) + 5;
        _summaryLabel.Y = Pos.Bottom(_assignList) + 6;

        // Git 块(5 行, 与状态块间隔一行)
        _repoLabel.Y = Pos.Bottom(_assignList) + 8;
        _gitBranchLabel.Y = Pos.Bottom(_assignList) + 9;
        _changesLabel.Y = Pos.Bottom(_assignList) + 10;
        _stepsLabel.Y = Pos.Bottom(_assignList) + 11;
        _commitLabel.Y = Pos.Bottom(_assignList) + 12;

        Add(
            _agentHeader, _agentList, _assignHeader, _assignList,
            _modelLabel, _progressLabel, _contextLabel, _costLabel, _workspaceLabel, _branchLabel, _summaryLabel,
            _repoLabel, _gitBranchLabel, _changesLabel, _stepsLabel, _commitLabel);
    }

    /// <summary>当前正在工作中的子 Agent 数量。</summary>
    public int RunningAgentCount => _agentRunning.Count(r => r);

    /// <summary>刷新侧栏全部内容(等价于 <see cref="Refresh"/>, 保留以兼容旧调用)。</summary>
    public void RefreshAll() => Refresh();

    /// <summary>刷新侧栏全部内容(子Agent / 分派 / 状态 / Git, 须在 UI 线程调用)。</summary>
    public void Refresh()
    {
        // 子 Agent 列表
        var running = _runtime.Assignments.All
            .Where(a => a.Status is SubagentStatus.Running or SubagentStatus.Queued)
            .GroupBy(a => a.AgentId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        _agentSource.Clear();
        _agentRunning.Clear();
        foreach (var agent in _runtime.Agents)
        {
            var count = running.GetValueOrDefault(agent.Id);
            _agentSource.Add(count > 0 ? $"● {agent.Display}" : $"○ {agent.Display}");
            _agentRunning.Add(count > 0);
        }

        _agentHeader.SetMarkup(
            $"[bold]子Agent[/] [grey]{_runtime.Agents.Count}[/]  " +
            $"[brightyellow]● {RunningAgentCount} 工作中[/]");

        // 分派列表(最新在前, 最多 20 条)
        _assignments.Clear();
        _assignSource.Clear();
        foreach (var assignment in _runtime.Assignments.All.Take(20))
        {
            _assignments.Add(assignment);
            _assignSource.Add(FormatAssignment(assignment));
        }

        if (_assignments.Count == 0)
        {
            _assignSource.Add("— 暂无分派 —");
        }

        RefreshStatus();
        RefreshGit();

        _agentList.SetNeedsDraw();
        _assignList.SetNeedsDraw();
    }

    /// <summary>状态块: 模型 / 进度 / 上下文 / 费用 / 工作区 / 分支 / 摘要。</summary>
    private void RefreshStatus()
    {
        var s = StatusSnapshot.Build(_runtime, _chatService);

        _modelLabel.SetMarkup($"[brightcyan]模型: {MarkupEscape.Escape(s.ModelText)}[/]");

        // 进度条
        var barWidth = Math.Max(8, Math.Min(36, (int)Viewport.Width - 8));
        var filled = Math.Min(barWidth, (int)(s.Percent / 100.0 * barWidth));
        var bar = new string('█', filled) + new string('░', barWidth - filled);
        _progressLabel.SetMarkup($"[grey][{bar}] {s.Percent:0.0}%[/]");

        _contextLabel.SetMarkup($"[grey]{MarkupEscape.Escape(s.HasContext
            ? $"{s.ContextUsed:N0} / {s.ContextTotal:N0} tokens"
            : s.ContextText)}[/]");

        _costLabel.SetMarkup($"[brightyellow]{MarkupEscape.Escape($"{s.CostText} · {s.Calls} 次调用 · {s.PriceSource}")}[/]");

        _workspaceLabel.SetMarkup($"[grey]{MarkupEscape.Escape(Shorten(s.WorkspaceRoot, 40))}[/]");

        _branchLabel.SetMarkup($"[brightgreen]分支: {MarkupEscape.Escape(s.Branch)}[/]");

        _summaryLabel.SetMarkup($"[grey]Agent {s.AgentCount} · 工具 {s.ToolCount} · 待审 {s.PendingSteps}[/]");
    }

    /// <summary>Git 块: 仓库 / 分支 / 变更 / 待审步骤 / 最近提交。完整操作走 /git 命令。</summary>
    private void RefreshGit()
    {
        var git = _runtime.Git;

        var repoText = git.IsRepoAvailable ? Shorten(git.RepositoryRoot, 36) : "非 git 仓库";
        _repoLabel.SetMarkup(git.IsRepoAvailable
            ? $"[brightcyan]{MarkupEscape.Escape(repoText)}[/]"
            : $"[grey]{repoText}[/]");

        _gitBranchLabel.SetMarkup($"[brightgreen]分支: {MarkupEscape.Escape(git.CurrentBranch() ?? "detached HEAD")}[/]");

        var files = git.GetStatusFiles();
        var dirty = git.HasUncommittedChanges();
        _changesLabel.SetMarkup(dirty
            ? $"[brightyellow]变更: {files.Count} 个文件未提交[/]"
            : "[grey]变更: 工作区干净[/]");

        var pending = git.PendingReview();
        _stepsLabel.SetMarkup(pending.Count > 0
            ? $"[brightyellow]待审步骤: {pending.Count}[/]"
            : $"[grey]待审步骤: {pending.Count}[/]");

        _commitLabel.SetMarkup($"[grey]最近提交: {MarkupEscape.Escape(git.LastCommitShort() ?? "-")}[/]");
    }

    private static string FormatAssignment(Assignment a)
    {
        var glyph = a.Status switch
        {
            SubagentStatus.Running => "●",
            SubagentStatus.Queued => "◍",
            SubagentStatus.Completed => "✓",
            SubagentStatus.Failed => "✗",
            SubagentStatus.Cancelled => "⊘",
            SubagentStatus.TimedOut => "!",
            _ => "?"
        };
        var status = a.Status switch
        {
            SubagentStatus.Running => "运行中",
            SubagentStatus.Queued => "排队中",
            SubagentStatus.Completed => "完成",
            SubagentStatus.Failed => "失败",
            SubagentStatus.Cancelled => "已取消",
            SubagentStatus.TimedOut => "超时",
            _ => a.Status.ToString()
        };
        var task = a.ShortTask.Length > 18 ? a.ShortTask[..18] + "…" : a.ShortTask;
        return $"{glyph} {a.AssignmentId} [{a.AgentName}] {task} · {status}";
    }

    private void OnAssignmentRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row >= _assignments.Count) return;
        var status = _assignments[e.Row].Status;
        e.RowAttribute = status switch
        {
            SubagentStatus.Running => YellowAttr,
            SubagentStatus.Completed => GreenAttr,
            SubagentStatus.Failed => RedAttr,
            SubagentStatus.TimedOut => YellowAttr,
            _ => GreyAttr
        };
    }

    private void OnAssignmentKeyDown(object? sender, Key key)
    {
        if (_assignments.Count == 0) return;

        if (key.KeyCode == KeyCode.Enter)
        {
            key.Handled = true;
            ShowAssignmentDetail();
        }
        else if (key.KeyCode is KeyCode.Delete or KeyCode.D)
        {
            key.Handled = true;
            TryCancelSelected();
        }
    }

    private void ShowAssignmentDetail()
    {
        var index = _assignList.SelectedItem ?? -1;
        if (index < 0 || index >= _assignments.Count) return;

        var a = _assignments[index];
        var text =
            $"任务: {a.Task}\n\n" +
            $"Agent: {a.AgentName} ({a.AgentId})\n" +
            $"模式: {a.Mode}   状态: {a.Status}\n" +
            $"工作目录: {a.WorkingDirectory}\n" +
            $"创建: {a.CreatedAt:yyyy-MM-dd HH:mm:ss}" +
            (a.FinishedAt is { } fin ? $"\n完成: {fin:yyyy-MM-dd HH:mm:ss}" : string.Empty) +
            (a.ExitCode is { } code ? $"\n退出码: {code}" : string.Empty) +
            (string.IsNullOrEmpty(a.Error) ? string.Empty : $"\n错误: {a.Error}") +
            (string.IsNullOrWhiteSpace(a.OutputTail) ? string.Empty : $"\n\n输出:\n{a.OutputTail}");

        _ui.ShowTextModal($"分派 {a.AssignmentId}", text);
    }

    private void TryCancelSelected()
    {
        var index = _assignList.SelectedItem ?? -1;
        if (index < 0 || index >= _assignments.Count) return;

        var a = _assignments[index];
        if (a.Status is not (SubagentStatus.Running or SubagentStatus.Queued))
        {
            return;
        }

        if (_ui.ConfirmModal("取消分派", $"确定取消分派 {a.AssignmentId} [{a.AgentName}] {a.ShortTask}?"))
        {
            _runtime.Assignments.Cancel(a.AssignmentId);
            Refresh();
        }
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
