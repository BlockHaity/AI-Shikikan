using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private bool _isEditing;

    [ObservableProperty]
    private string _editTitle = string.Empty;

    public string MessageText => string.Format(Strings.Session_Messages, MessageCount);

    public bool HasMessages => MessageCount > 0;

    public SessionItemViewModel(ChatSession session)
    {
        Session = session;
        _title = session.DisplayTitle;
        _messageCount = session.MessageCount;
        _timeLabel = FormatRelativeTime(session.UpdatedAt);
        _editTitle = _title;
    }

    /// <summary>会话数据变化后同步展示属性。</summary>
    public void Update(ChatSession session)
    {
        Title = session.DisplayTitle;
        MessageCount = session.MessageCount;
        TimeLabel = FormatRelativeTime(session.UpdatedAt);
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

/// <summary>右侧"会话列表"侧栏: 新建/切换/删除/重命名会话。</summary>
public partial class SessionPanelViewModel : ViewModelBase
{
    private readonly ChatService _chatService;

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public SessionPanelViewModel(ChatService chatService)
    {
        _chatService = chatService;
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));
        Reload();

        _chatService.CurrentSessionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Reload();
                SyncSelection();
            });
        };

        _chatService.MessageAdded += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in Sessions)
                {
                    if (item.Session.Id == _chatService.CurrentSession?.Id)
                    {
                        item.Update(_chatService.CurrentSession);
                        break;
                    }
                }

                SyncOrder();
            });
        };

        _chatService.SessionRenamed += (_, session) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in Sessions)
                {
                    if (item.Session.Id == session.Id)
                    {
                        item.Update(session);
                        break;
                    }
                }
            });
        };
    }

    private void Reload()
    {
        var currentId = _chatService.CurrentSession?.Id;

        // 批量对账: 已有项原地更新, 仅增删真正变化的会话, 避免 Clear+重建触发主题过渡在
        // 控件移除级联中的 Avalonia 内部 NRE(Avalonia 12.0.4 未修复)
        var remaining = new Dictionary<string, SessionItemViewModel>(StringComparer.Ordinal);
        foreach (var item in Sessions)
        {
            if (string.IsNullOrEmpty(item.Session.Id)) continue;
            remaining[item.Session.Id] = item;
        }

        var desired = new List<SessionItemViewModel>(_chatService.Sessions.Count);
        foreach (var session in _chatService.Sessions)
        {
            // 防御: 损坏的会话文件可能带空 Id, 直接跳过
            if (string.IsNullOrEmpty(session.Id)) continue;

            if (remaining.Remove(session.Id, out var item))
            {
                item.Update(session);
            }
            else
            {
                item = new SessionItemViewModel(session);
            }

            item.IsSelected = session.Id == currentId;
            desired.Add(item);
        }

        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (remaining.ContainsKey(Sessions[i].Session.Id))
            {
                Sessions.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < Sessions.Count && ReferenceEquals(Sessions[i], item)) continue;

            var idx = Sessions.IndexOf(item);
            if (idx < 0)
            {
                Sessions.Insert(Math.Min(i, Sessions.Count), item);
            }
            else if (idx != i)
            {
                Sessions.Move(idx, i);
            }
        }
    }

    private void SyncSelection()
    {
        var currentId = _chatService.CurrentSession?.Id;
        foreach (var item in Sessions)
        {
            item.IsSelected = item.Session.Id == currentId;
        }
    }

    private void SyncOrder()
    {
        // 会话按 UpdatedAt 降序: 仅用 Move 调整顺序, 避免 Clear+重建销毁控件
        var expected = Sessions.OrderByDescending(s => s.Session.UpdatedAt).ToList();
        for (var i = 0; i < expected.Count; i++)
        {
            if (Sessions[i] == expected[i]) continue;

            var idx = Sessions.IndexOf(expected[i]);
            if (idx >= 0)
            {
                Sessions.Move(idx, i);
            }
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
