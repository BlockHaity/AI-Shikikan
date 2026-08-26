using AIShikikan.Cli.Tui.Ui;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Git;

namespace AIShikikan.Cli.Tui.Services.Commands;

/// <summary>Git 步骤管理: 状态/步骤列表/分支图/提交/合并/回滚。</summary>
public sealed class GitCommandHandler : ICommandHandler
{
    private readonly CommanderRuntime _runtime;
    private readonly IUiOutput _ui;

    public GitCommandHandler(CommanderRuntime runtime, IUiOutput ui)
    {
        _runtime = runtime;
        _ui = ui;
    }

    public bool CanHandle(string command) => command == "git";

    public IEnumerable<(string Command, string Help)> HelpRows =>
    [
        ("/git status", "查看工作区状态"),
        ("/git steps", "列出步骤分支"),
        ("/git diff [grey]<stepId>[/]", "查看步骤 diff"),
        ("/git merge [grey]<stepId>[/]", "合并步骤到主分支"),
        ("/git drop [grey]<stepId>[/]", "丢弃步骤(回滚)"),
        ("/git revert [grey]<stepId>[/]", "反转步骤"),
        ("/git commit [grey]<message>[/]", "提交全部更改"),
        ("/git branch", "列出本地分支"),
        ("/git graph", "查看提交图"),
        ("/git stage-all", "暂存所有更改")
    ];

    public bool TryHandle(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "":
            case "status":
                ShowStatus();
                return true;
            case "steps":
                ShowSteps();
                return true;
            case "diff":
                ShowDiff(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            case "merge":
                MergeStep(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            case "drop":
                DropStep(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            case "revert":
                RevertStep(parts.Length > 1 ? parts[1] : string.Empty);
                return true;
            case "commit":
                Commit(parts.Skip(1).ToArray());
                return true;
            case "branch":
                ShowBranches();
                return true;
            case "graph":
                ShowGraph();
                return true;
            case "stage":
                Stage(parts.Skip(1).ToArray());
                return true;
            case "stage-all":
                StageAll();
                return true;
            case "unstage":
                Unstage(parts.Skip(1).ToArray());
                return true;
            default:
                ShowStatus();
                return true;
        }
    }

    private void ShowStatus()
    {
        var git = _runtime.Git;
        if (!git.IsRepoAvailable)
        {
            _ui.Error("当前目录不是 Git 仓库");
            return;
        }

        var files = git.GetStatusFiles();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var f in files)
        {
            rows.Add(new[] { f.StatusLabel, MarkupEscape.Escape(f.Path) });
        }

        _ui.Panel("Git 状态",
            $"分支: {git.CurrentBranch() ?? "?"}   未提交: {files.Count} 个文件");
        _ui.Table(["状态", "文件"], rows);
        _ui.Hint("用法: /git steps | /git diff <stepId> | /git merge|drop|revert <stepId> | /git commit <消息>");
        _ui.ScrollToBottom();
    }

    private void ShowSteps()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var s in _runtime.Git.AllSteps.OrderByDescending(s => s.CreatedAt))
        {
            rows.Add(new[]
            {
                s.StepId,
                MarkupEscape.Escape(s.Label),
                s.StepBranch,
                s.BaseBranch,
                s.Status.ToString()
            });
        }

        _ui.Table(["ID", "标签", "步骤分支", "基分支", "状态"], rows);
        _ui.Hint("用法: /git diff|merge|drop|revert <stepId>");
        _ui.ScrollToBottom();
    }

    private void ShowDiff(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            _ui.Error("用法: /git diff <stepId>");
            return;
        }

        var result = _runtime.Git.GetDiff(stepId);
        if (!result.Succeeded)
        {
            _ui.Error(result.Stderr);
            return;
        }

        _ui.ShowTextModal($"diff {stepId}", result.Stdout);
    }

    private void MergeStep(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            _ui.Error("用法: /git merge <stepId>");
            return;
        }

        Execute(() => _runtime.Git.MergeStep(stepId), $"已合并步骤 {stepId}");
    }

    private void DropStep(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            _ui.Error("用法: /git drop <stepId>");
            return;
        }

        if (_ui.ConfirmModal("丢弃步骤", $"丢弃将删除步骤 {stepId} 的分支并放弃全部变更, 确认?"))
        {
            Execute(() => _runtime.Git.DropStep(stepId), $"已丢弃步骤 {stepId}");
        }
    }

    private void RevertStep(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            _ui.Error("用法: /git revert <stepId>");
            return;
        }

        if (_ui.ConfirmModal("反转步骤", $"将生成一个反向提交以撤销步骤 {stepId}, 确认?"))
        {
            Execute(() => _runtime.Git.RevertStep(stepId), $"已反转步骤 {stepId}");
        }
    }

    private void Commit(string[] parts)
    {
        if (parts.Length == 0)
        {
            _ui.Error("用法: /git commit <提交消息>");
            return;
        }

        var result = _runtime.Git.CommitAll(string.Join(' ', parts));
        if (!result.Succeeded)
        {
            _ui.Error(result.Stderr);
            return;
        }

        _ui.Ok("已提交");
        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            _ui.Hint(CommandUi.Shorten(result.Stdout.Trim(), 200));
        }
    }

    private void ShowBranches()
    {
        var rows = new List<IReadOnlyList<string>>();
        var current = _runtime.Git.CurrentBranch();
        foreach (var b in _runtime.Git.GetLocalBranches())
        {
            var isCurrent = string.Equals(b, current, StringComparison.OrdinalIgnoreCase)
                ? "[green]◀[/]"
                : "";
            rows.Add(new[] { MarkupEscape.Escape(b), isCurrent });
        }

        _ui.Table(["分支", "当前"], rows);
        _ui.ScrollToBottom();
    }

    private void ShowGraph()
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in _runtime.Git.GetCommitGraph(60))
        {
            rows.Add(new[]
            {
                $"[grey]{MarkupEscape.Escape(line.GraphPart)}[/]",
                MarkupEscape.Escape(line.CommitPart)
            });
        }

        _ui.Table(["图", "提交"], rows);
        _ui.ScrollToBottom();
    }

    private void Stage(string[] parts)
    {
        if (parts.Length == 0)
        {
            _ui.Error("用法: /git stage <路径>");
            return;
        }

        var result = _runtime.Git.StageFile(string.Join(' ', parts));
        if (!result.Succeeded)
        {
            _ui.Error(result.Stderr);
            return;
        }

        _ui.Ok($"已暂存: {parts[0]}");
    }

    private void StageAll()
    {
        var result = _runtime.Git.StageAll();
        if (!result.Succeeded)
        {
            _ui.Error(result.Stderr);
            return;
        }

        _ui.Ok("已暂存全部更改");
    }

    private void Unstage(string[] parts)
    {
        if (parts.Length == 0)
        {
            _ui.Error("用法: /git unstage <路径>");
            return;
        }

        var result = _runtime.Git.UnstageFile(string.Join(' ', parts));
        if (!result.Succeeded)
        {
            _ui.Error(result.Stderr);
            return;
        }

        _ui.Ok($"已取消暂存: {parts[0]}");
    }

    private void Execute(Func<GitCommandResult> action, string successMessage)
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
                _ui.Hint(CommandUi.Shorten(result.Stdout.Trim(), 300));
            }
        }
        catch (Exception ex)
        {
            _ui.Error($"git 操作失败: {ex.Message}");
        }
    }
}
