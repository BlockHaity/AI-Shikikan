using AgentCommander.Cli.Tui;
using AgentCommander.Core;
using AgentCommander.Core.Services;
using AgentCommander.Core.Services.Agents;
using AgentCommander.Core.Services.Engine;
using Spectre.Console;

if (args.Length > 0 && args[0] is "version" or "-v" or "--version")
{
    Console.WriteLine(AppInfo.Version);
    return 0;
}

if (args.Length > 0 && args[0] == "doctor")
{
    return RunDoctor();
}

var personaId = args.Length > 1 && args[0] == "--persona" ? args[1] : null;

var app = new TuiApp(personaId);
await app.RunAsync();

return 0;

static int RunDoctor()
{
    AnsiConsole.Write(new FigletText("Doctor").Color(Color.Yellow));
    AnsiConsole.WriteLine();

    var checks = new List<(string Name, bool Ok, string Detail)>();

    try
    {
        var runtime = CommanderRuntime.Boot(Environment.CurrentDirectory);

        checks.Add(("配置目录", true, Path.GetFullPath(AppPaths.ConfigDir)));

        var providers = runtime.Llm.Settings.Providers;
        var configured = providers.Any(p => !string.IsNullOrEmpty(p.ApiKey) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(p.EnvKey)));
        checks.Add(("Provider API Key",
            configured,
            configured ? "至少一个 Provider 已配置 Key" : "未找到 API Key(检查 providers.json 或环境变量)"));

        checks.Add(("Agent 定义", runtime.Agents.Count > 0, $"{runtime.Agents.Count} 个 Agent"));
        checks.Add(("专家/模板", runtime.Personas.Count > 0, $"{runtime.Personas.Count} 个专家, {runtime.Templates.Count} 个模板"));
        checks.Add(("工具装载", runtime.Registry.All.Count > 0, $"{runtime.Registry.All.Count} 个工具"));

        var gitOk = runtime.Git.IsRepoAvailable;
        checks.Add(("Git 仓库", gitOk, gitOk ? "仓库可用" : "当前目录不是 git 仓库, 步骤回滚将不可用"));

        foreach (var (name, ok, detail) in checks)
        {
            var status = ok ? "[green]✔[/]" : "[red]✘[/]";
            AnsiConsole.MarkupLine($"  {status} [bold]{name}[/]  [grey]{Markup.Escape(detail)}[/]");
        }

        var failed = checks.Count(c => !c.Ok);
        AnsiConsole.WriteLine();
        if (failed > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]发现 {failed} 项问题。配置目录:[/] [grey]{AppPaths.ConfigDir}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold green]全部检查通过 ✓[/]");
        return 0;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]💥 启动诊断失败: {Markup.Escape(ex.Message)}[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ex.StackTrace ?? string.Empty)}[/]");
        return 2;
    }
}