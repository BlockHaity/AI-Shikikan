using AIShikikan.Core;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Session;
using AIShikikan.Core.Services.Usage;
using AIShikikan.Core.Services.Worker;
using Avalonia;
using System;
using System.IO;
using System.Linq;

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
            var code = RunDoctor();
            UsageStatsService.Flush();
            Log.Flush();
            return code;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 用量统计是防抖落盘(合并 1.5s 内的多次记录), 不 flush 会丢掉最后一批。
            // 走到这里说明 lifetime 已结束, 不会再有新记录。
            UsageStatsService.Flush();
            Log.Info("Boot", "应用正常退出");
            Log.Flush();
        }

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

            var repoRoot = runtime.GitService.FindRepositoryRoot(Environment.CurrentDirectory);
            var gitOk = !string.IsNullOrEmpty(repoRoot);
            var gitWarn = !gitOk;
            checks.Add(("Git 仓库", !gitWarn, gitOk ? $"仓库可用 ({repoRoot})" : "当前目录不是 git 仓库, 聊天可用, 检查点/Git写工具不可用"));

            // 并发协调规则的单元级自检(纯内存, 无副作用)
            var selfCheckFailures = WorkspaceExecutionCoordinator.SelfCheck();
            checks.Add(("并发协调规则", selfCheckFailures.Count == 0,
                selfCheckFailures.Count == 0
                    ? $"{WorkspaceExecutionCoordinator.SelfCheckScenarioCount} 组场景全部通过"
                    : string.Join("; ", selfCheckFailures)));

            // 工具卡片多态编解码自检: 验证判别符清单与 ToolCardDetail 的 [JsonDerivedType] 未脱节,
            // 且每个类型都能按具体类型往返。这是 Worker 跨进程传 ToolResult.Detail 的前置条件 ——
            // 判别符一旦脱节, 故障表现是"整份会话读不出来"(抽象基类实例化失败 → 判损坏 → 永久拒写),
            // 属于最坏的一类症状, 所以放进 doctor。
            var codecFailures = ToolCardDetailCodec.SelfCheck();
            checks.Add(("工具卡片编解码", codecFailures.Count == 0,
                codecFailures.Count == 0
                    ? $"{ToolCardDetailCodec.KnownTypeNames.Count} 种卡片类型往返通过"
                    : string.Join("; ", codecFailures)));

            // Worker 可执行文件定位: 找不到会降级为"同进程内联执行", 功能不丢但失去崩溃隔离,
            // 因此必须让用户看见自己处在哪种模式。
            var workerLoc = WorkerLocator.Locate();
            checks.Add(("Worker 定位", workerLoc.Found, workerLoc.Found
                ? $"已定位 ({workerLoc.Source}) → {workerLoc.ExecutablePath}"
                : $"未找到, 将降级为同进程内联执行 | {workerLoc.Detail}"));

            // Worker 端到端自检: 协议编解码 / 工具执行 / 取消传播 / 跨进程往返 / 身份键一致性。
            // 与上面两项的分工: 那两项只验"零件在不在", 这一项验"拼起来能不能跑"。
            // 管道那一段在 Worker 产物可定位时会真的起一个进程跑完握手→同步→调用→关闭; 未定位则跳过并记一条说明,
            // 跳过不算失败(那是本方案允许的降级态), 但必须让用户看见"这次没验到跨进程那条路"。
            var workerSelf = WorkerSelfCheck.RunDetailed();
            checks.Add(("Worker 端到端", workerSelf.Ok,
                (workerSelf.Ok
                    ? $"{WorkerSelfCheck.ScenarioCount} 组场景通过"
                    : string.Join("; ", workerSelf.Failures))
                + (workerSelf.Notes.Count == 0
                    ? string.Empty
                    : " | " + string.Join("; ", workerSelf.Notes))));

            foreach (var (name, ok, detail) in checks)
            {
                var status = ok ? "✔" : (name == "Git 仓库" ? "⚠" : "✘");
                Console.WriteLine($"  {status} {name}  {detail}");
            }

            // 废弃机制残留提示: 只提示不自动删, 由用户判断
            // 放在失败早退之前, 保证任何退出路径下用户都能看到清理线索
            if (!string.IsNullOrEmpty(repoRoot))
            {
                var legacy = runtime.GitService.ScanLegacyArtifacts(repoRoot);
                if (legacy.HasAny)
                {
                    Console.WriteLine();
                    Console.WriteLine("⚠ 检测到已废弃的 Git 步骤机制残留（旧版本子代理自动分支功能）:");
                    if (legacy.StaleBranches.Count > 0)
                    {
                        var shown = legacy.StaleBranches.Take(5);
                        Console.WriteLine($"  · ac/* 分支 {legacy.StaleBranches.Count} 个: {string.Join(", ", shown)}{(legacy.StaleBranches.Count > 5 ? " ..." : "")}");
                    }
                    if (legacy.StepsFileCount > 0)
                    {
                        Console.WriteLine($"  · steps/*.json {legacy.StepsFileCount} 个文件");
                    }
                    if (legacy.CheckpointTagCount > 0)
                    {
                        Console.WriteLine($"  · ai-shikikan/checkpoint/* tag {legacy.CheckpointTagCount} 个（仍在使用的检查点 tag，是 Reset/Revert/Fork 的回滚依据；" +
                                          "若其中已无需要保留的历史检查点，可手动删除）");
                    }
                    Console.WriteLine("  当前版本不再读取上述 ac/* 分支与 steps 数据, 可手动清理(仅供参考, 请自行确认后再执行):");
                    if (legacy.StaleBranches.Count > 0)
                    {
                        Console.WriteLine("    git branch -D $(git branch --list 'ac/*' | tr -d ' ' | tr '\\n' ' ')");
                    }
                    if (legacy.StepsFileCount > 0)
                    {
                        Console.WriteLine("    rm -rf <数据目录>/steps");
                    }
                    if (legacy.CheckpointTagCount > 0)
                    {
                        Console.WriteLine("    git tag -d $(git tag --list 'ai-shikikan/checkpoint/*' | tr '\\n' ' ')   # 会失去对应检查点的回滚能力");
                    }
                }
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
                Console.WriteLine("⚠ Git 仓库未初始化, 检查点与Git写工具不可用; 请自行在该目录执行 git init 并创建首个提交。");
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
