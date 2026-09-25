using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AIShikikan.Core.Models;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

/// <summary>侧栏源代码管理：保留常规 Git 操作，并以检查点列表替代旧步骤列表。</summary>
public partial class GitPanelViewModel : ViewModelBase
{
    private readonly CommanderRuntime _runtime = AppShell.Instance.Runtime;
    private GitWorkspaceContext? _context;
    private string _workDir = string.Empty;

    public ObservableCollection<GitFileStatus> StatusFiles { get; } = [];
    public ObservableCollection<GitCheckpointRecord> Checkpoints { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    public ObservableCollection<GitGraphLine> Graph { get; } = [];

    /// <summary>视图注入的文件夹选择器，返回 null 表示取消。</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    /// <summary>由聊天页注入统一 Fork 流程，确保分支与会话截断使用同一套对话框。</summary>
    public Func<GitCheckpointRecord, Task>? CheckpointForker { get; set; }

    public Func<(string SessionId, int ConversationCutoff)>? CurrentConversationPosition { get; set; }

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private bool _isRepoAvailable;

    [ObservableProperty]
    private string _rootText = string.Empty;

    [ObservableProperty]
    private bool _hasSelectedFolder;

    [ObservableProperty]
    private string _commitMessage = string.Empty;

    [ObservableProperty]
    private string _selectedBranch = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _diffText = string.Empty;

    [ObservableProperty]
    private bool _hasDiff;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private bool _hasGraph;

    [ObservableProperty]
    private bool _hasCheckpoints;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isEmptyRepository;

    [ObservableProperty]
    private bool _isDetachedHead;

    [ObservableProperty]
    private string _checkpointLabel = string.Empty;

    public GitPanelViewModel()
    {
        Refresh();
    }

    public void SetWorkspace(string workDir)
    {
        _workDir = workDir ?? string.Empty;
        HasSelectedFolder = !string.IsNullOrWhiteSpace(_workDir);
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        StatusFiles.Clear();
        Checkpoints.Clear();
        Branches.Clear();
        Graph.Clear();
        HasDiff = false;
        DiffText = string.Empty;

        if (string.IsNullOrWhiteSpace(_workDir))
        {
            _context = null;
            IsRepoAvailable = false;
            RootText = string.Empty;
            StateText = string.Empty;
            HasChanges = false;
            HasGraph = false;
            HasCheckpoints = false;
            return;
        }

        _context = _runtime.Git.ResolveContext(_workDir);
        RootText = _context.RepositoryRoot;
        IsRepoAvailable = _context.IsValidRepo;
        IsEmptyRepository = _context.IsEmptyRepo;
        IsDetachedHead = _context.IsDetachedHead;
        if (!IsRepoAvailable)
        {
            StateText = string.Empty;
            HasChanges = false;
            HasGraph = false;
            HasCheckpoints = false;
            return;
        }

        IsDirty = !_runtime.Git.IsClean(_context).Succeeded;
        var head = _runtime.Git.GetHeadSha(_context.RepositoryRoot);
        StateText = $"{_context.BranchName} {(IsDirty ? "●" : "○")} {head?[..Math.Min(8, head.Length)] ?? string.Empty}".TrimEnd();
        if (IsEmptyRepository) StateText = string.Empty;

        RefreshStatus();
        RefreshCheckpoints();
        RefreshBranches();
        RefreshGraph();
    }

    [RelayCommand]
    private async Task SelectFolderAsync()
    {
        if (FolderPicker is null) return;
        var path = await FolderPicker();
        if (string.IsNullOrWhiteSpace(path)) return;
        SetWorkspace(path);
    }

    [RelayCommand]
    private void StageFile(GitFileStatus file) => Run(_runtime.Git.StageFile(_context!, file.Path), RefreshStatus);

    [RelayCommand]
    private void ToggleStage(GitFileStatus file) => Run(file.IsStaged
        ? _runtime.Git.UnstageFile(_context!, file.Path)
        : _runtime.Git.StageFile(_context!, file.Path), RefreshStatus);

    [RelayCommand]
    private void UnstageFile(GitFileStatus file) => Run(_runtime.Git.UnstageFile(_context!, file.Path), RefreshStatus);

    [RelayCommand]
    private void StageAll() => Run(_runtime.Git.StageAll(_context!), RefreshStatus);

    [RelayCommand]
    private void Commit()
    {
        if (!IsRepoAvailable || _context is null) return;
        var files = _runtime.Git.GetStatusFiles(_context);
        if (files.Count > 0 && !files.Any(f => f.IsStaged)) _runtime.Git.StageAll(_context);
        var result = _runtime.Git.Commit(_context, CommitMessage);
        SetResult(result);
        if (result.Succeeded) CommitMessage = string.Empty;
        Refresh();
    }

    [RelayCommand]
    private void SwitchBranch(string branch)
    {
        if (_context is null || string.IsNullOrWhiteSpace(branch)) return;
        Run(_runtime.Git.SwitchBranch(_context, branch.Trim()), Refresh);
    }

    [RelayCommand]
    private void Pull()
    {
        if (_context is not null) Run(_runtime.Git.Pull(_context), Refresh);
    }

    [RelayCommand]
    private void Push()
    {
        if (_context is not null) Run(_runtime.Git.Push(_context), Refresh);
    }

    [RelayCommand]
    private void ShowCheckpointDiff(GitCheckpointRecord checkpoint)
    {
        if (_context is null) return;
        var result = _runtime.Git.GetDiff(_context, checkpoint.CommitSha, "HEAD");
        DiffText = result.Succeeded ? result.Stdout : result.Stderr;
        HasDiff = true;
    }

    [RelayCommand]
    private async Task ForkCheckpointAsync(GitCheckpointRecord checkpoint)
    {
        if (CheckpointForker is not null) await CheckpointForker(checkpoint);
        Refresh();
    }

    [RelayCommand]
    private void MarkManualCheckpoint()
    {
        if (_context is null || !IsRepoAvailable) return;
        var sha = _runtime.Git.GetHeadSha(_context.RepositoryRoot);
        if (string.IsNullOrWhiteSpace(sha))
        {
            ResultText = Strings.GitPanel_FirstCommitBeforeCheckpoint;
            return;
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var label = string.IsNullOrWhiteSpace(CheckpointLabel)
            ? Strings.GitPanel_DefaultCheckpointLabel
            : CheckpointLabel.Trim();
        var position = CurrentConversationPosition?.Invoke() ?? (string.Empty, 0);
        var record = new GitCheckpointRecord
        {
            Id = id,
            RepositoryRoot = _context.RepositoryRoot,
            WorkDir = _context.WorkDir,
            BranchName = _context.BranchName,
            CommitSha = sha,
            TagName = $"ai-shikikan/checkpoint/{id}",
            SessionId = position.SessionId,
            ConversationCutoff = position.ConversationCutoff,
            Source = GitCheckpointSource.Manual,
            Label = label,
            CreatedAt = DateTime.Now
        };
        Run(_runtime.Git.MarkCheckpoint(_context, record), Refresh);
        CheckpointLabel = string.Empty;
    }

    private void RefreshStatus()
    {
        if (_context is null) return;
        StatusFiles.Clear();
        foreach (var file in _runtime.Git.GetStatusFiles(_context)) StatusFiles.Add(file);
        HasChanges = StatusFiles.Count > 0;
        IsDirty = HasChanges;
    }

    private void RefreshCheckpoints()
    {
        if (_context is null) return;
        foreach (var checkpoint in _runtime.Checkpoints.GetAll(_context.RepositoryRoot)) Checkpoints.Add(checkpoint);
        HasCheckpoints = Checkpoints.Count > 0;
    }

    private void RefreshBranches()
    {
        if (_context is null) return;
        foreach (var branch in _runtime.Git.GetLocalBranches(_context)) Branches.Add(branch);
        SelectedBranch = _context.BranchName;
    }

    private void RefreshGraph()
    {
        if (_context is null) return;
        foreach (var line in _runtime.Git.GetCommitGraph(_context)) Graph.Add(line);
        HasGraph = Graph.Count > 0;
    }

    private void Run(GitCommandResult result, Action? refresh = null)
    {
        SetResult(result);
        refresh?.Invoke();
    }

    private void SetResult(GitCommandResult result)
    {
        ResultText = result.Succeeded
            ? (string.IsNullOrWhiteSpace(result.Stdout) ? "✔" : result.Stdout.Trim())
            : $"✘ {(string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim()}";
    }
}
