using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Gui.Resources;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

/// <summary>会话列表项: 包装 ChatSession, 提供选中态/编辑态等展示属性。</summary>
public partial class SessionItemViewModel : ViewModelBase
{
    public ChatSession Session { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _timeLabel;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private string _workDirText = string.Empty;

    [ObservableProperty]
    private string _branchText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isBlocked;

    [ObservableProperty]
    private string? _blockedReason;

    public string BlockedTip => !string.IsNullOrWhiteSpace(BlockedReason)
        ? BlockedReason
        : Strings.Session_BranchOccupiedTip;

    public bool HasBranch => !string.IsNullOrWhiteSpace(BranchText);

    public bool HasWorkDir => !string.IsNullOrWhiteSpace(WorkDirText);

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    public string MessageText => string.Format(Strings.Session_Messages, MessageCount);

    public bool HasMessages => MessageCount > 0;

    public void UpdateRuntime(bool isRunning, string? blockedReason)
    {
        IsRunning = isRunning;
        BlockedReason = blockedReason;
        IsBlocked = !string.IsNullOrWhiteSpace(blockedReason);
    }

    partial void OnBlockedReasonChanged(string? value) => OnPropertyChanged(nameof(BlockedTip));

    public SessionItemViewModel(ChatSession session)
    {
        Session = session;
        _title = session.DisplayTitle;
        _messageCount = session.MessageCount;
        _timeLabel = FormatRelativeTime(session.UpdatedAt);
        _workDirText = session.WorkDir;
        _branchText = session.BranchName;
        _editTitle = _title;
    }

    /// <summary>会话数据变化后同步展示属性。</summary>
    public void Update(ChatSession session)
    {
        Title = session.DisplayTitle;
        MessageCount = session.MessageCount;
        TimeLabel = FormatRelativeTime(session.UpdatedAt);
        WorkDirText = session.WorkDir;
        BranchText = session.BranchName;
        OnPropertyChanged(nameof(HasBranch));
        OnPropertyChanged(nameof(HasWorkDir));
        if (!IsEditing)
        {
            EditTitle = Title;
        }
    }

    partial void OnMessageCountChanged(int value)
    {
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(HasMessages));
    }

    public void BeginEdit()
    {
        EditTitle = Title;
        IsEditing = true;
    }

    public void EndEdit()
    {
        IsEditing = false;
    }

    // ⚠️ E30: 相对时间未走 resx, 待统一收口时处理。少于 1 分钟这一档已本地化(Session_AgoJustNow),
    // 但 m/h/d 三个后缀与末尾的 MM/dd 日期是硬编码 —— 英文界面下会直接显示 "3h" 而非本地化文案。
    // 之所以不在此就地修: 正确修法需要 (a) 新增 Session_AgoMinutes/Hours/Days 三键(共享 resx, 本类无所有权),
    // (b) 一个把"语义标识 + 数值"拼成文案的转换器(Views/Converters.cs, 亦非本文件所有)。
    // 缺任一条就只能把硬编码从 C# 挪到转换器里, 反而更坏, 故保留现状并在此标注。
    // 另注: 最后一行 time.ToString("MM/dd") 应改用 CultureInfo 以适配区域化日期格式。
    private static string FormatRelativeTime(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return Strings.Session_AgoJustNow;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        return time.ToString("MM/dd");
    }
}

/// <summary>按工作目录分组时的组头: 目录名 + 会话数, 可折叠。</summary>
public partial class SessionGroupHeaderViewModel : ViewModelBase
{
    private readonly Action<SessionGroupHeaderViewModel> _onToggled;

    public string Key { get; }

    /// <summary>完整目录路径(未分组占位组为空串), 用于悬浮提示。</summary>
    public string FullPath { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isExpanded = true;

    public SessionGroupHeaderViewModel(string key, string title, string fullPath,
        Action<SessionGroupHeaderViewModel> onToggled)
    {
        Key = key;
        Title = title;
        FullPath = fullPath;
        _onToggled = onToggled;
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        _onToggled(this);
    }
}

/// <summary>右侧"会话列表"侧栏: 新建/切换/删除/重命名会话, 会话始终按工作目录分组展示。</summary>
public partial class SessionPanelViewModel : ViewModelBase
{
    private readonly ChatService _chatService;

    /// <summary>展示项: 目录组头与组内会话项混合(始终按工作目录分组)。</summary>
    public ObservableCollection<object> DisplayItems { get; } = [];

    private readonly Dictionary<string, SessionItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionGroupHeaderViewModel> _headers = new(StringComparer.Ordinal);

    /// <summary>由聊天页注入 Core 会话运行态，避免 UI 自行维护第二份状态。</summary>
    public Func<string, bool> IsSessionRunning { get; set; } = _ => false;

    /// <summary>返回不同分支活动会话的占用原因；null 表示可发送。</summary>
    public Func<ChatSession, string?>? GetWorkspaceBlockReason { get; set; }

    /// <summary>返回运行期间跨工作目录切换的阻止原因；null 表示允许切换。</summary>
    public Func<string, string?>? GetSessionSwitchBlockReason { get; set; }

    public bool HasSessions => _chatService.Sessions.Count > 0;

    public SessionPanelViewModel(ChatService chatService)
    {
        _chatService = chatService;
        Reload();

        // 这 4 个订阅一律 Dispatcher.UIThread.Post(Reload): ChatService 会在引擎线程/后台线程
        // 触发事件, 而 Reload 会写 DisplayItems(有容器绑定的 ObservableCollection),
        // 必须切回 UI 线程。Post 而非 Invoke 是为了不阻塞事件源(引擎)线程。
        _chatService.CurrentSessionChanged += (_, _) => Dispatcher.UIThread.Post(Reload);
        _chatService.MessageAdded += (_, _) => Dispatcher.UIThread.Post(Reload);
        _chatService.SessionWorkDirChanged += (_, _) => Dispatcher.UIThread.Post(Reload);
        _chatService.SessionRenamed += (_, session) => Dispatcher.UIThread.Post(() =>
        {
            if (_items.TryGetValue(session.Id, out var item))
            {
                item.Update(session);
            }
        });
    }

    public void RefreshRuntime() => Reload();

    public void RefreshItems() => Reload();

    /// <summary>
    /// 重建期望顺序并对账到 DisplayItems(原地增/移/删, 避免整集合替换触发容器回收级联 NRE)。
    /// </summary>
    /// <remarks>
    /// 设计取舍: 每次事件(哪怕只追加了一条消息)都全量重算一遍期望序列。
    /// 之所以不改成"增量插入单条", 是因为整集合替换会回收全部容器,
    /// 在滚动/展开态下引发级联 NRE —— Reconcile 的原地差集已把这个成本压到"只动真正变化的项"。
    /// 代价是 O(n) 重算 + 每项 O(n) 的占用判定, 见 GetOrCreateItem 上的说明。
    /// </remarks>
    private void Reload()
    {
        OnPropertyChanged(nameof(HasSessions));
        var currentId = _chatService.CurrentSession?.Id;
        // 始终按工作目录分组: 分组早已是主路径, 原来的开关只是历史遗留的探索性 UI, 留着会让用户面对两种排列。
        var desired = BuildGrouped(currentId);
        Reconcile(desired);
    }

    private List<object> BuildGrouped(string? currentId)
    {
        var groups = new List<(string Key, string Title, string FullPath, List<SessionItemViewModel> Items)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        const string unassignedKey = "\0unassigned";

        foreach (var session in _chatService.Sessions)
        {
            var item = GetOrCreateItem(session, currentId);
            if (item is null) continue;

            var dir = session.WorkDir?.Trim() ?? string.Empty;
            var key = string.IsNullOrEmpty(dir) ? unassignedKey : dir;
            if (!byKey.TryGetValue(key, out var gi))
            {
                var title = string.IsNullOrEmpty(dir)
                    ? Strings.Session_GroupUnassigned
                    : (Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                        ? name
                        : dir);
                groups.Add((key, title, dir, []));
                gi = groups.Count - 1;
                byKey[key] = gi;
            }

            groups[gi].Items.Add(item);
        }

        // 组按组内最新活跃时间降序; 未指定目录组固定排最后
        var ordered = groups
            .OrderBy(g => g.Key == unassignedKey)
            .ThenByDescending(g => g.Items.Count > 0 ? g.Items.Max(i => i.Session.UpdatedAt).Ticks : 0L)
            .ToList();

        var list = new List<object>();
        foreach (var g in ordered)
        {
            var header = GetOrCreateHeader(g.Key, g.Title, g.FullPath);
            header.Count = g.Items.Count;
            list.Add(header);
            if (header.IsExpanded)
            {
                foreach (var item in g.Items.OrderByDescending(i => i.Session.UpdatedAt))
                {
                    list.Add(item);
                }
            }
        }

        return list;
    }

    private SessionItemViewModel? GetOrCreateItem(ChatSession session, string? currentId)
    {
        // 防御: 损坏的会话文件可能带空 Id, 直接跳过
        if (string.IsNullOrEmpty(session.Id)) return null;

        if (!_items.TryGetValue(session.Id, out var item))
        {
            item = new SessionItemViewModel(session);
            _items[session.Id] = item;
        }
        else
        {
            item.Update(session);
        }

        item.IsSelected = session.Id == currentId;
        var running = IsSessionRunning(session.Id);
        // 性能特征(P1-3, 暂不改): 下面两个委托由 ChatPageViewModel 提供, 内部对"全部会话"做 LINQ 扫描;
        // 而 Reload 每条消息都会触发一次, 于是整体退化为 O(n^2)。
        // 之所以不在这里优化: 判定逻辑的所有权在 ChatPageViewModel, 本类拿不到会话集合的只读视图,
        // 强行缓存又会把"别的会话刚启动/刚结束"这个外部状态变更漏掉(没有对应的失效信号)。
        // 正确修法见 docs/plans: 把两个判定下沉为 WorkspaceExecutionCoordinator 的查询并加事件失效。
        var switchReason = GetSessionSwitchBlockReason?.Invoke(session.WorkDir);
        var blockedReason = switchReason ?? GetWorkspaceBlockReason?.Invoke(session);
        item.UpdateRuntime(running, blockedReason);
        return item;
    }

    private SessionGroupHeaderViewModel GetOrCreateHeader(string key, string title, string fullPath)
    {
        if (!_headers.TryGetValue(key, out var header))
        {
            header = new SessionGroupHeaderViewModel(key, title, fullPath, _ => Dispatcher.UIThread.Post(Reload));
            _headers[key] = header;
        }
        else
        {
            header.Title = title;
        }

        return header;
    }

    /// <summary>把 desired 序列对账到 DisplayItems: 只移动/插入/移除真正变化的项。</summary>
    private void Reconcile(List<object> desired)
    {
        var keep = new HashSet<object>(desired);
        for (var i = DisplayItems.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(DisplayItems[i]))
            {
                DisplayItems.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var target = desired[i];
            if (i < DisplayItems.Count && ReferenceEquals(DisplayItems[i], target)) continue;

            var idx = DisplayItems.IndexOf(target);
            if (idx < 0)
            {
                DisplayItems.Insert(Math.Min(i, DisplayItems.Count), target);
            }
            else if (idx != i)
            {
                DisplayItems.Move(idx, i);
            }
        }

        // 清理不再使用的组头缓存
        var usedKeys = desired.OfType<SessionGroupHeaderViewModel>().Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _headers.Keys.Where(k => !usedKeys.Contains(k)).ToList())
        {
            _headers.Remove(key);
        }
    }

    [RelayCommand]
    private void NewSession()
    {
        if (GetSessionSwitchBlockReason?.Invoke(string.Empty) is not null) return;
        _chatService.CreateSession();
    }

    [RelayCommand]
    private void SwitchSession(SessionItemViewModel item)
    {
        if (item.Session.Id == _chatService.CurrentSession?.Id) return;
        if (GetSessionSwitchBlockReason?.Invoke(item.Session.WorkDir) is { } reason)
        {
            item.UpdateRuntime(IsSessionRunning(item.Session.Id), reason);
            return;
        }

        _chatService.SwitchSession(item.Session.Id);
    }

    [RelayCommand]
    private void DeleteSession(SessionItemViewModel item)
    {
        _chatService.DeleteSession(item.Session.Id);
        AppShell.Instance.NotifyDataChanged(); // 主页会话分布移除该会话
    }

    [RelayCommand]
    private void RenameSession(SessionItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.EditTitle)) return;
        if (!string.Equals(item.EditTitle.Trim(), item.Session.Title, StringComparison.Ordinal))
        {
            _chatService.RenameSession(item.Session.Id, item.EditTitle.Trim());
            item.Update(item.Session);
        }

        item.EndEdit();
    }

    [RelayCommand]
    private void CancelRename(SessionItemViewModel item)
    {
        item.EndEdit();
    }

    [RelayCommand]
    private void ContextRename(SessionItemViewModel item)
    {
        item.BeginEdit();
    }

    [RelayCommand]
    private void ContextDelete(SessionItemViewModel item)
    {
        DeleteSession(item);
    }
}
