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
        _messageCount = session.Messages.Count;
        _timeLabel = FormatRelativeTime(session.UpdatedAt);
        _editTitle = _title;
    }

    /// <summary>会话数据变化后同步展示属性。</summary>
    public void Update(ChatSession session)
    {
        Title = session.DisplayTitle;
        MessageCount = session.Messages.Count;
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
        Sessions.Clear();
        foreach (var session in _chatService.Sessions)
        {
            var item = new SessionItemViewModel(session);
            item.IsSelected = session.Id == currentId;
            Sessions.Add(item);
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
        // 会话按 UpdatedAt 降序: 若当前会话已不是最新, 重新排序
        var expected = Sessions.OrderByDescending(s => s.Session.UpdatedAt).ToList();
        var changed = false;
        for (var i = 0; i < expected.Count; i++)
        {
            if (Sessions[i] != expected[i])
            {
                changed = true;
                break;
            }
        }

        if (changed)
        {
            Sessions.Clear();
            foreach (var item in expected)
            {
                Sessions.Add(item);
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
