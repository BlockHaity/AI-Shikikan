using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIShikikan.Gui.ViewModels;

/// <summary>侧栏"源代码管理": 类 VSCode 的 Git 面板。</summary>
public partial class GitPanelViewModel : ViewModelBase
{
    private readonly CommanderRuntime _runtime = AppShell.Instance.Runtime;

    public ObservableCollection<GitFileStatus> StatusFiles { get; } = [];
    public ObservableCollection<GitStepRecord> Steps { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    public ObservableCollection<GitGraphLine> Graph { get; } = [];

    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private bool _isRepoAvailable;

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

    public GitPanelViewModel()
    {
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var git = _runtime.Git;
        IsRepoAvailable = git.IsRepoAvailable;
        if (!IsRepoAvailable)
        {
            StateText = "";
            StatusFiles.Clear();
            Steps.Clear();
            Branches.Clear();
            return;
        }

        var branch = git.CurrentBranch() ?? "?";
        var dirty = git.HasUncommittedChanges() ? "●" : "○";
        StateText = $"{branch} {dirty}  {git.LastCommitShort() ?? ""}";

        RefreshStatus();
        RefreshSteps();
        RefreshBranches();
        RefreshGraph();
    }

    private void RefreshStatus()
    {
        StatusFiles.Clear();
        foreach (var f in _runtime.Git.GetStatusFiles())
        {
            StatusFiles.Add(f);
        }

        HasChanges = StatusFiles.Count > 0;
    }

    private void RefreshSteps()
    {
        Steps.Clear();
        foreach (var s in _runtime.Git.PendingReview())
        {
            Steps.Add(s);
        }
    }

    private void RefreshBranches()
    {
        var current = _runtime.Git.CurrentBranch();
        Branches.Clear();
        foreach (var b in _runtime.Git.GetLocalBranches())
        {
            Branches.Add(b);
            if (string.Equals(b, current, StringComparison.Ordinal))
            {
                SelectedBranch = b;
            }
        }

        if (SelectedBranch.Length == 0 && Branches.Count > 0)
        {
            SelectedBranch = Branches[0];
        }
    }

    private void RefreshGraph()
    {
        var lines = _runtime.Git.GetCommitGraph();
        Graph.Clear();
        foreach (var line in lines)
        {
            Graph.Add(line);
        }

        HasGraph = Graph.Count > 0;
    }

    [RelayCommand]
    private void StageFile(GitFileStatus file)
    {
        SetResult(_runtime.Git.StageFile(file.Path));
        RefreshStatus();
    }

    [RelayCommand]
    private void ToggleStage(GitFileStatus file)
    {
        SetResult(file.IsStaged
            ? _runtime.Git.UnstageFile(file.Path)
            : _runtime.Git.StageFile(file.Path));
        RefreshStatus();
    }

    [RelayCommand]
    private void UnstageFile(GitFileStatus file)
    {
        SetResult(_runtime.Git.UnstageFile(file.Path));
        RefreshStatus();
    }

    [RelayCommand]
    private void StageAll()
    {
        SetResult(_runtime.Git.StageAll());
        RefreshStatus();
    }

    [RelayCommand]
    private void Commit()
    {
        var files = _runtime.Git.GetStatusFiles();
        if (files.Count > 0 && !files.Any(f => f.IsStaged))
        {
            _runtime.Git.StageAll();
        }

        var result = _runtime.Git.CommitAll(CommitMessage);
        SetResult(result);
        if (result.Succeeded)
        {
            CommitMessage = string.Empty;
        }

        Refresh();
    }

    [RelayCommand]
    private void SwitchBranch(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return;
        SetResult(_runtime.Git.SwitchBranch(branch.Trim()));
        Refresh();
    }

    [RelayCommand]
    private void Pull()
    {
        SetResult(_runtime.Git.Pull());
        Refresh();
    }

    [RelayCommand]
    private void Push()
    {
        SetResult(_runtime.Git.Push());
        Refresh();
    }

    [RelayCommand]
    private void ShowGitDiff(string stepId)
    {
        var diff = _runtime.Git.GetDiff(stepId);
        DiffText = diff.Succeeded ? diff.Stdout : diff.Stderr;
        HasDiff = true;
    }

    [RelayCommand]
    private void MergeStep(string stepId) => RunStepAction(stepId, s => _runtime.Git.MergeStep(s));

    [RelayCommand]
    private void DropStep(string stepId) => RunStepAction(stepId, s => _runtime.Git.DropStep(s));

    [RelayCommand]
    private void RevertStep(string stepId) => RunStepAction(stepId, s => _runtime.Git.RevertStep(s));

    private void RunStepAction(string stepId, Func<string, GitCommandResult> action)
    {
        try
        {
            var result = action(stepId);
            SetResult(result);
        }
        catch (Exception ex)
        {
            ResultText = ex.Message;
        }

        Refresh();
    }

    private void SetResult(GitCommandResult result)
    {
        ResultText = result.Succeeded
            ? (string.IsNullOrWhiteSpace(result.Stdout) ? "✔" : result.Stdout.Trim())
            : $"✘ {(string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim()}";
    }
}