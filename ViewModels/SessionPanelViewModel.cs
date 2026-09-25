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

    public bool HasBranch => !string.IsNullOrWhiteSpace(BranchText);

    public bool HasWorkDir => !string.IsNullOrWhiteSpace(WorkDirText);

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    public string MessageText => string.Format(Strings.Session_Messages, MessageCount);

    public bool HasMessages => MessageCount > 0;

    public void UpdateRuntime(bool isRunning, bool isBlocked)
    {
        IsRunning = isRunning;
        IsBlocked = isBlocked;
    }

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

/// <summary>右侧"会话列表"侧栏: 新建/切换/删除/重命名会话, 支持按工作目录分组整理。</summary>
public partial class SessionPanelViewModel : ViewModelBase
{
    private readonly ChatService _chatService;

    /// <summary>展示项: 会话项与(分组模式下的)组头混合。</summary>
    public ObservableCollection<object> DisplayItems { get; } = [];

    private readonly Dictionary<string, SessionItemViewModel> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionGroupHeaderViewModel> _headers = new(StringComparer.Ordinal);

    /// <summary>由聊天页注入 Core 会话运行态，避免 UI 自行维护第二份状态。</summary>
    public Func<string, bool> IsSessionRunning { get; set; } = _ => false;

    /// <summary>返回不同分支活动会话的占用原因；null 表示可发送。</summary>
    public Func<ChatSession, string?>? GetWorkspaceBlockReason { get; set; }

    [ObservableProperty]
    private bool _groupByWorkDir;

    public bool HasSessions => _chatService.Sessions.Count > 0;

    partial void OnGroupByWorkDirChanged(bool value) => Reload();

    public SessionPanelViewModel(ChatService chatService)
    {
        _chatService = chatService;
        Reload();

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

    /// <summary>重建期望顺序并对账到 DisplayItems(原地增/移/删, 避免整集合替换触发容器回收级联 NRE)。</summary>
    private void Reload()
    {
        OnPropertyChanged(nameof(HasSessions));
        var currentId = _chatService.CurrentSession?.Id;
        var desired = GroupByWorkDir ? BuildGrouped(currentId) : BuildFlat(currentId);
        Reconcile(desired);
    }

    private List<object> BuildFlat(string? currentId)
    {
        var list = new List<object>();
        foreach (var session in _chatService.Sessions)
        {
            var item = GetOrCreateItem(session, currentId);
            if (item is null) continue;
            list.Add(item);
        }

        return list;
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
        var blocked = GetWorkspaceBlockReason?.Invoke(session) is not null;
        item.UpdateRuntime(running, blocked);
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
        _chatService.CreateSession();
    }

    [RelayCommand]
    private void SwitchSession(SessionItemViewModel item)
    {
        if (item.Session.Id != _chatService.CurrentSession?.Id)
        {
            _chatService.SwitchSession(item.Session.Id);
        }
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
