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
        AIShikikan.Core.Logging.Log.Initialize();
        HookGlobalExceptions();

        AIShikikan.Core.Logging.Log.Info("Boot", $"AI-Shikikan {AppInfo.Version} 启动 (args: {(args.Length == 0 ? "无" : string.Join(' ', args))})");

        FixupLinuxImeEnvironment();

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
        AIShikikan.Core.Logging.Log.Info("Boot", "应用正常退出");
        return 0;
    }

    /// <summary>挂接全局异常钩子: 未知异常落盘(Error), 便于用户报障时提供日志。</summary>
    static void HookGlobalExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AIShikikan.Core.Logging.Log.Error("Crash", e.ExceptionObject as Exception ?? new Exception("非 Exception 异常对象"),
                $"未处理异常 (IsTerminating={e.IsTerminating})");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AIShikikan.Core.Logging.Log.Warn("Task", e.Exception, "未观察的任务异常");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Linux 下修复输入法(fcitx5/ibus)检测所需的环境变量:
    /// 1. 剥离被中文输入法误录入的弯引号(如 XMODIFIERS=“@im=fcitx”);
    /// 2. 若所有 IM 变量均缺失(常见于从桌面图标而非终端启动), 按运行中的
    ///    输入法守护进程探测并补写 AVALONIA_IM_MODULE, 保证 AOT 发布包可直接使用输入法。
    /// </summary>
    static void FixupLinuxImeEnvironment()
    {
        if (!OperatingSystem.IsLinux()) return;

        var imNames = new[] { "AVALONIA_IM_MODULE", "GTK_IM_MODULE", "QT_IM_MODULE", "XMODIFIERS" };

        // 1) 清洗弯引号等包裹字符
        foreach (var name in imNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) continue;

            var trimmed = value.Trim('\u201C', '\u201D', '\u2018', '\u2019', '"', '\'');
            if (trimmed.Length != value.Length)
            {
                Environment.SetEnvironmentVariable(name, trimmed);
            }
        }

        // 2) 全部缺失时按守护进程兜底探测(尊重显式 none, 不覆盖用户意图)
        var configured = false;
        foreach (var name in imNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value == "none")
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                configured = name != "XMODIFIERS" || value.Contains("@im=");
                if (configured)
                {
                    break;
                }
            }
        }

        if (configured || (Environment.GetEnvironmentVariable("DISPLAY") is null &&
                           Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null))
        {
            return;
        }

        if (ProcessExists("fcitx5") || ProcessExists("fcitx"))
        {
            Environment.SetEnvironmentVariable("AVALONIA_IM_MODULE", "fcitx");
            AIShikikan.Core.Logging.Log.Info("Boot", "检测到 fcitx 输入法守护进程, 已补写 AVALONIA_IM_MODULE=fcitx");
        }
        else if (ProcessExists("ibus-daemon"))
        {
            Environment.SetEnvironmentVariable("AVALONIA_IM_MODULE", "ibus");
            AIShikikan.Core.Logging.Log.Info("Boot", "检测到 ibus 输入法守护进程, 已补写 AVALONIA_IM_MODULE=ibus");
        }
    }

    /// <summary>遍历 /proc 判断指定进程是否存在(避免依赖 shell/ps)。</summary>
    static bool ProcessExists(string comm)
    {
        try
        {
            var procDir = new DirectoryInfo("/proc");
            foreach (var dir in procDir.EnumerateDirectories())
            {
                // 仅匹配数字命名的进程目录
                if (!long.TryParse(dir.Name, out _)) continue;

                using var reader = new StreamReader(Path.Combine(dir.FullName, "comm"));
                if (reader.ReadLine() is { } line && line.StartsWith(comm, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // /proc 不可读时静默跳过探测
        }

        return false;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Avalonia.X11PlatformOptions
            {
                // Linux 下显式启用输入法支持, 不依赖自动检测
                EnableIme = true
            })
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
            var gitWarn = !gitOk;
            checks.Add(("Git 仓库", !gitWarn, gitOk ? "仓库可用" : "当前目录不是 git 仓库, 聊天可用, 检查点/Git写工具不可用"));

            foreach (var (name, ok, detail) in checks)
            {
                var status = ok ? "✔" : (name == "Git 仓库" ? "⚠" : "✘");
                Console.WriteLine($"  {status} {name}  {detail}");
            }

            var failed = checks.Count(c => !c.Ok && c.Name != "Git 仓库");
            Console.WriteLine();
            if (failed > 0)
            {
                Console.WriteLine($"发现 {failed} 项问题。配置目录: {AppPaths.ConfigDir}");
                return 1;
            }

            if (gitWarn)
            {
                Console.WriteLine("⚠ Git 仓库未初始化, 检查点与Git写工具不可用; 首次发送消息将自动执行 git init 与 first commit。");
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
