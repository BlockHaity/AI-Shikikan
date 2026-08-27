using AIShikikan.Core;
using AIShikikan.Core.Services;
using Avalonia;
using System;
using System.IO;

namespace AIShikikan.Gui;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "version" or "-v" or "--version")
        {
            Console.WriteLine(AppInfo.Version);
            return 0;
        }

        if (args.Length > 0 && args[0] == "doctor")
        {
            return RunDoctor();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();

    static int RunDoctor()
    {
        Console.WriteLine("=== AI-Shikikan Doctor ===");
        Console.WriteLine();

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
                configured ? "至少一个 Provider 已配置 Key" : "未找到 API Key(检查 providers.toml 或环境变量)"));

            checks.Add(("Agent 定义", runtime.Agents.Count > 0, $"{runtime.Agents.Count} 个 Agent"));
            checks.Add(("专家/模板", runtime.Personas.Count > 0, $"{runtime.Personas.Count} 个专家, {runtime.Templates.Count} 个模板"));
            checks.Add(("工具装载", runtime.Registry.All.Count > 0, $"{runtime.Registry.All.Count} 个工具"));

            var gitOk = runtime.Git.IsRepoAvailable;
            checks.Add(("Git 仓库", gitOk, gitOk ? "仓库可用" : "当前目录不是 git 仓库, 步骤回滚将不可用"));

            foreach (var (name, ok, detail) in checks)
            {
                var status = ok ? "✔" : "✘";
                Console.WriteLine($"  {status} {name}  {detail}");
            }

            var failed = checks.Count(c => !c.Ok);
            Console.WriteLine();
            if (failed > 0)
            {
                Console.WriteLine($"发现 {failed} 项问题。配置目录: {AppPaths.ConfigDir}");
                return 1;
            }

            Console.WriteLine("全部检查通过 ✓");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动诊断失败: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
    }
}
