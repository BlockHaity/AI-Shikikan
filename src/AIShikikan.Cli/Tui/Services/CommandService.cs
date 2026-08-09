using AIShikikan.Cli.Models;
using AIShikikan.Core;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Personas;
using Spectre.Console;

namespace AIShikikan.Cli.Tui.Services;

public class CommandService
{
    private readonly List<Message> _messages;
    private readonly CommanderRuntime _runtime;

    public CommandService(List<Message> messages, CommanderRuntime runtime)
    {
        _messages = messages;
        _runtime = runtime;
    }

    public bool TryHandle(string input)
    {
        if (!input.StartsWith('/'))
            return false;

        var parts = input.Split(' ', 2);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "/help":
                ShowHelp();
                return true;
            case "/clear":
                ClearMessages();
                return true;
            case "/quit":
            case "/exit":
                return true;
            case "/status":
                ShowStatus();
                return true;
            case "/model":
                SetModel(args);
                return true;
            case "/persona":
                ShowPersonas(args);
                return true;
            case "/personas":
                ShowPersonas(string.Empty);
                return true;
            case "/agents":
                ShowAgents();
                return true;
            case "/templates":
                ShowTemplates();
                return true;
            case "/tools":
                ShowTools();
                return true;
            case "/assign":
                ShowAssignments();
                return true;
            case "/git":
                ShowGit(args);
                return true;
            case "/providers":
                ShowProviders();
                return true;
            default:
                AnsiConsole.MarkupLine($"[red]未知命令: {Markup.Escape(command)}[/]");
                AnsiConsole.MarkupLine("[grey]输入 /help 查看可用命令[/]");
                return true;
        }
    }

    private void ShowHelp()
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]命令[/]"))
            .AddColumn(new TableColumn("[bold]说明[/]"));

        table.AddRow("/help", "显示帮助信息");
        table.AddRow("/clear", "清除对话历史");
        table.AddRow("/model [grey]<model>[/]", $"切换活动模型, 格式: 模型{Markup.Escape("/provider")}");
        table.AddRow("/persona [grey]<id/名称|import <路径>>[/]", "列出/切换专家人格, import 导入专家文件");
        table.AddRow("/agents", "列出已注册的 CLI Agent 定义");
        table.AddRow("/templates", "列出专家模板");
        table.AddRow("/tools", "列出当前可用工具");
        table.AddRow("/assign", "列出子代理分派状态");
        table.AddRow("/git [grey]<rc|show|merge|drop|revert> <stepId>[/]", "待审步骤列表; rc 仓库状态; 查看 diff/合并/丢弃/回滚");
        table.AddRow("/providers", "查看 Provider 配置与 API Key 状态");
        table.AddRow("/status", "显示系统状态");
        table.AddRow("/quit, /exit", "退出 TUI");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ClearMessages()
    {
        _messages.Clear();
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[green]对话历史已清除[/]");
        AnsiConsole.WriteLine();
    }

    private void ShowStatus()
    {
        AnsiConsole.WriteLine();
        var provider = _runtime.Llm.GetProvider();

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]工作区:[/] {_runtime.WorkspaceRoot}"),
                new Markup($"[bold]消息数量:[/] {_messages.Count}"),
                new Markup($"[bold]活动 Provider:[/] {provider?.Id ?? "未配置"}"),
                new Markup($"[bold]活动模型:[/] {_runtime.Llm.ResolveModel()}"),
                new Markup($"[bold]注册 Agent:[/] {_runtime.Agents.Count}"),
                new Markup($"[bold]可用工具:[/] {_runtime.Registry.All.Count}"),
                new Markup($"[bold]待审步骤:[/] {_runtime.Git.PendingReview().Count}"),
                new Markup($"[bold]子代理:[/] {_runtime.Assignments.All.Count} 条目")
            ))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue)
            .Header("[bold]系统状态[/]");
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private void SetModel(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            AnsiConsole.MarkupLine(
                $"[bold]当前模型:[/] [green]{_runtime.Llm.ResolveModel()}[/] " +
                $"(provider: [grey]{_runtime.Llm.GetProvider()?.Id ?? "?"}[/])");
            AnsiConsole.WriteLine();
            return;
        }

        var parts = args.Split('/', 2);
        var model = parts[0].Trim();
        var providerId = parts.Length > 1 ? parts[1].Trim() : null;

        var provider = _runtime.Llm.GetProvider(providerId) ?? _runtime.Llm.GetProvider();
        if (provider is null)
        {
            AnsiConsole.MarkupLine($"[red]找不到 Provider: {Markup.Escape(providerId ?? "?")}[/]");
            return;
        }

        _runtime.Llm.Settings.ActiveProviderId = provider.Id;
        _runtime.Llm.Settings.ActiveModel = model;
        ProviderSettingsService.Save(_runtime.Llm.Settings);
        AnsiConsole.MarkupLine($"[green]已切换:[/] [bold]{Markup.Escape(model)}[/] @ [bold]{provider.Id}[/]");
        AnsiConsole.WriteLine();
    }

    private void ShowPersonas(string arg)
    {
        if (!string.IsNullOrWhiteSpace(arg))
        {
            var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sub = parts[0];

            if (string.Equals(sub, "import", StringComparison.OrdinalIgnoreCase))
            {
                ImportPersonaFile(parts.Length > 1 ? parts[1].Trim() : string.Empty);
                return;
            }

            var persona = PersonaService.Find(arg, _runtime.Personas);
            if (persona is null)
            {
                AnsiConsole.MarkupLine($"[red]未找到人格: {Markup.Escape(arg)}[/]");
                AnsiConsole.MarkupLine("[grey]用法: /persona <id|名称> 切换主 Agent 人格 | /persona import <文件路径> 导入专家文件[/]");
            }
            else
            {
                _runtime.Engine.SetPersonaText(persona.SystemPrompt);
                AnsiConsole.MarkupLine(
                    $"[green]已切换主 Agent 人格:[/] [bold]{Markup.Escape(persona.Name)}[/] ({persona.Id})");
            }

            AnsiConsole.WriteLine();
            return;
        }

        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]ID[/]"))
            .AddColumn(new TableColumn("[bold]名称[/]"))
            .AddColumn(new TableColumn("[bold]说明[/]"));

        foreach (var p in _runtime.Personas)
        {
            table.AddRow(p.Id, Markup.Escape(p.Name), Markup.Escape(Shorten(p.Description ?? string.Empty)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]用法: /persona <id|名称> 切换主 Agent 人格 | /persona import <文件路径> 导入专家文件[/]");
        AnsiConsole.WriteLine();
    }

    private void ImportPersonaFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AnsiConsole.MarkupLine("[red]用法: /persona import <专家文件.toml>[/]");
            AnsiConsole.WriteLine();
            return;
        }

        try
        {
            var (persona, error) = PersonaService.ImportFile(path);
            if (persona is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error ?? "导入失败")}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[green]✔ 已导入专家:[/] [bold]{Markup.Escape(persona.Name)}[/] ({persona.Id})");
                _runtime.Engine.SetPersonaText(persona.SystemPrompt);
                AnsiConsole.MarkupLine($"[grey]已切换主 Agent 人格为 {Markup.Escape(persona.Name)}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();
    }

    private void ShowAgents()
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]ID[/]"))
            .AddColumn(new TableColumn("[bold]名称[/]"))
            .AddColumn(new TableColumn("[bold]模式[/]"))
            .AddColumn(new TableColumn("[bold]可执行文件[/]"))
            .AddColumn(new TableColumn("[bold]说明[/]"));

        foreach (var a in _runtime.Agents)
        {
            table.AddRow(
                a.Id,
                Markup.Escape(a.Display),
                a.DefaultMode,
                a.Executable,
                Markup.Escape(Shorten(a.Description)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ShowTemplates()
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]ID[/]"))
            .AddColumn(new TableColumn("[bold]名称[/]"))
            .AddColumn(new TableColumn("[bold]说明[/]"));

        foreach (var t in _runtime.Templates)
        {
            table.AddRow(t.Id, Markup.Escape(t.Name), Markup.Escape(Shorten(t.SystemPrompt, 80)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ShowTools()
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]名称[/]"))
            .AddColumn(new TableColumn("[bold]权限[/]"))
            .AddColumn(new TableColumn("[bold]说明[/]"));

        foreach (var tool in _runtime.Registry.All.OrderBy(t => t.Name))
        {
            table.AddRow(
                tool.Name,
                tool.RequiresApproval ? "[yellow]需确认[/]" : "[green]自动[/]",
                Markup.Escape(Shorten(tool.Description, 80)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ShowAssignments()
    {
        AnsiConsole.WriteLine();
        var assignments = _runtime.Assignments.All.ToList();
        if (assignments.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]暂无子代理分派[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]ID[/]"))
            .AddColumn(new TableColumn("[bold]Agent[/]"))
            .AddColumn(new TableColumn("[bold]状态[/]"))
            .AddColumn(new TableColumn("[bold]任务[/]"));

        foreach (var a in assignments)
        {
            table.AddRow(a.AssignmentId, a.AgentName, a.Status.ToString(), Markup.Escape(a.ShortTask));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void ShowGit(string args)
    {
        AnsiConsole.WriteLine();

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0] : string.Empty;
        var rest = parts.Length > 1 ? parts[1] : string.Empty;

        switch (sub)
        {
            case "rc":
                ShowGitRepoState();
                return;
            case "show":
                RunGitAction("show", rest, step =>
                {
                    var diff = _runtime.Git.GetDiff(step);
                    if (!diff.Succeeded)
                    {
                        throw new InvalidOperationException(diff.Stderr.Trim());
                    }

                    AnsiConsole.MarkupLine(Markup.Escape(diff.Stdout));
                    return string.Empty;
                });
                return;
            case "merge":
                RunGitAction("merge", rest, step =>
                {
                    var result = _runtime.Git.MergeStep(step);
                    return result.Stdout.Trim() + result.Stderr.Trim();
                });
                return;
            case "drop":
                RunGitAction("drop", rest, step =>
                {
                    var result = _runtime.Git.DropStep(step);
                    return result.Stdout.Trim() + result.Stderr.Trim();
                });
                return;
            case "revert":
                RunGitAction("revert", rest, step =>
                {
                    var result = _runtime.Git.RevertStep(step);
                    return result.Stdout.Trim() + result.Stderr.Trim();
                });
                return;
        }

        var pending = _runtime.Git.PendingReview();
        if (pending.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]暂无待审步骤[/]");
            AnsiConsole.MarkupLine("[grey]用法: /git rc 仓库状态 | /git show|merge|drop|revert <stepId>[/]");
            AnsiConsole.WriteLine();
            return;
        }

        foreach (var step in pending)
        {
            AnsiConsole.MarkupLine(
                $"[bold cyan]{step.StepId}[/] [{Markup.Escape(step.Label)}]  [grey]{step.StepBranch} → {step.BaseBranch}[/]");
        }

        AnsiConsole.WriteLine($"共 [bold]{pending.Count}[/] 个待审步骤");
        AnsiConsole.MarkupLine("[grey]用法: /git show|merge|drop|revert <stepId> 查看 diff / 合并 / 丢弃 / 回滚[/]");
        AnsiConsole.WriteLine();
    }

    private void RunGitAction(string action, string stepId, Func<string, string> execute)
    {
        if (string.IsNullOrWhiteSpace(stepId))
        {
            AnsiConsole.MarkupLine($"[red]用法: /git {action} <stepId>[/]");
            return;
        }

        try
        {
            var detail = execute(stepId);
            AnsiConsole.MarkupLine($"[green]✔ /git {action} {stepId}[/]");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                AnsiConsole.MarkupLine(detail.Length > 120 ? detail[..120] + "..." : detail);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }

        AnsiConsole.WriteLine();
    }

    private void ShowGitRepoState()
    {
        var git = _runtime.Git;
        if (!git.IsRepoAvailable)
        {
            AnsiConsole.MarkupLine("[red]当前目录不是 git 仓库[/]");
        }
        else
        {
            var dirty = git.HasUncommittedChanges() ? "[yellow]有未提交变更[/]" : "[green]干净[/]";
            AnsiConsole.MarkupLine(
                $"[green]分支:[/] [bold]{git.CurrentBranch() ?? "?"}[/]  " +
                $"[green]最近提交:[/] {git.LastCommitShort() ?? "?"}  {dirty}");
        }

        AnsiConsole.WriteLine();
        return;
    }

    private void ShowProviders()
    {
        AnsiConsole.WriteLine();
        var settings = _runtime.Llm.Settings;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]ID[/]"))
            .AddColumn(new TableColumn("[bold]名称[/]"))
            .AddColumn(new TableColumn("[bold]默认模型[/]"))
            .AddColumn(new TableColumn("[bold]API Key[/]"));

        foreach (var p in settings.Providers)
        {
            var hasKey = !string.IsNullOrEmpty(p.ApiKey) ||
                         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(p.EnvKey));
            table.AddRow(p.Id, p.Name, p.DefaultModel, hasKey ? "[green]✔[/]" : "[red]✘[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]配置文件: {AppPaths.ProvidersPath}[/]");
        AnsiConsole.WriteLine();
    }

    private static string Shorten(string s, int max = 60)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Length <= max ? s : s[..max] + "...";
    }
}