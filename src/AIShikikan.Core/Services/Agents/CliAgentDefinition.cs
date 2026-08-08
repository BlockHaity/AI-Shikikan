using System.Diagnostics;
using System.Text;

namespace AIShikikan.Core.Services.Agents;

public enum InjectionMode
{
    None,
    PromptFlag,
    MemoryFile,
    Both
}

public record CliAgentDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;

    /// <summary>参数模板, 支持 {prompt} 占位符。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>记忆文件名(如 CLAUDE.md / AGENTS.md / GEMINI.md), 用于 MemoryFile 注入。</summary>
    public string? MemoryFile { get; set; }

    public InjectionMode Injection { get; set; } = InjectionMode.Both;

    /// <summary>"sync" 常规阻塞 | "async" 异步后台。</summary>
    public string DefaultMode { get; set; } = "sync";

    public int MaxConcurrent { get; set; } = 1;

    public bool RequireApproval { get; set; } = true;

    public int TimeoutMinutes { get; set; } = 30;

    public List<string> Expertise { get; set; } = [];

    public string Description { get; set; } = string.Empty;

    public string? RecommendedPersonaId { get; set; }

    public string? DefaultTemplateId { get; set; }

    /// <summary>自定义 Roster 条目, 非空时完全替换自动生成的条目。</summary>
    public string? RosterEntry { get; set; }

    public Dictionary<string, string> Environment { get; set; } = [];

    public string Display => string.IsNullOrEmpty(Name) ? Id : Name;
}

public record CliAgentRunResult
{
    public int ExitCode { get; init; }
    public required string Output { get; init; }
    public bool TimedOut { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

public static class BuiltinCliAgents
{
    public static readonly List<CliAgentDefinition> All = Build();

    private static List<CliAgentDefinition> Build() =>
    [
        new CliAgentDefinition
        {
            Id = "claude",
            Name = "Claude Code",
            Executable = "claude",
            Args = ["-p", "{prompt}"],
            MemoryFile = "CLAUDE.md",
            Injection = InjectionMode.Both,
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "Anthropic 官方编码 Agent, 擅长代码实现/重构/测试",
            Expertise = ["编码", "重构", "测试", "feature", "bug", "实现"],
            RecommendedPersonaId = "general-expert"
        },
        new CliAgentDefinition
        {
            Id = "codex",
            Name = "Codex CLI",
            Executable = "codex",
            Args = ["exec", "{prompt}"],
            MemoryFile = "AGENTS.md",
            Injection = InjectionMode.Both,
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "OpenAI 官方 CLI 编码 Agent, 擅长精确的小步修改",
            Expertise = ["修复", "小步修改", "lint", "样式修复", "快速实现"],
            RecommendedPersonaId = "general-expert"
        },
        new CliAgentDefinition
        {
            Id = "gemini",
            Name = "Gemini CLI",
            Executable = "gemini",
            Args = ["-p", "{prompt}"],
            MemoryFile = "GEMINI.md",
            Injection = InjectionMode.Both,
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "Google 官方命令行 Agent, 支持联网与多模态, 适合研究与搜索",
            Expertise = ["搜索", "研究", "文档", "多模态", "翻译"]
        },
        new CliAgentDefinition
        {
            Id = "opencode",
            Name = "OpenCode",
            Executable = "opencode",
            Args = ["run", "{prompt}"],
            MemoryFile = "AGENTS.md",
            Injection = InjectionMode.Both,
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "开源通用命令行 Agent, 插件生态丰富",
            Expertise = ["通用", "跨栈", "脚本", "自动化"]
        },
        new CliAgentDefinition
        {
            Id = "reasonix",
            Name = "Reasonix",
            Executable = "reasonix",
            Args = ["run", "{prompt}"],
            MemoryFile = null,
            Injection = InjectionMode.PromptFlag,
            DefaultMode = "sync",
            MaxConcurrent = 1,
            RequireApproval = true,
            TimeoutMinutes = 30,
            Description = "DeepSeek 原生缓存优先 Agent, 低成本批量/长任务",
            Expertise = ["低成本", "批量", "长任务", "审查", "批处理"]
        }
    ];
}

public static class CliAgentRunner
{
    private const int MaxOutputChars = 512 * 1024;

    public static async Task<CliAgentRunResult> RunAsync(
        CliAgentDefinition definition,
        string prompt,
        string workingDirectory,
        IProgress<string>? progressOutput,
        CancellationToken ct = default)
    {
        var startedAt = DateTime.Now;
        var psi = new ProcessStartInfo
        {
            FileName = definition.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var kv in definition.Environment)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        foreach (var arg in definition.Args)
        {
            psi.ArgumentList.Add(arg.Replace("{prompt}", prompt, StringComparison.Ordinal));
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new CliAgentRunResult
                {
                    ExitCode = -1,
                    Output = $"无法启动 {definition.Executable}, 请确认已安装并加入 PATH。",
                    TimedOut = false,
                    StartedAt = startedAt,
                    Elapsed = DateTime.Now - startedAt
                };
            }

            var output = new StringBuilder();
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(definition.TimeoutMinutes));

            var readTask = Task.WhenAll(
                ReadStreamAsync(process.StandardOutput, output, progressOutput, _outputLock, timeoutCts.Token),
                ReadStreamAsync(process.StandardError, output, progressOutput, _outputLock, timeoutCts.Token)
            );

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new CliAgentRunResult
                {
                    ExitCode = -1,
                    Output = Tail(output) + "\n[超时] 子代理超过限制时间已强制终止。",
                    TimedOut = true,
                    StartedAt = startedAt,
                    Elapsed = DateTime.Now - startedAt
                };
            }

            await readTask;

            return new CliAgentRunResult
            {
                ExitCode = process.ExitCode,
                Output = Tail(output),
                TimedOut = false,
                StartedAt = startedAt,
                Elapsed = DateTime.Now - startedAt
            };
        }
        catch (Exception ex)
        {
            return new CliAgentRunResult
            {
                ExitCode = -1,
                Output = $"启动失败: {ex.Message}",
                TimedOut = false,
                StartedAt = startedAt,
                Elapsed = DateTime.Now - startedAt
            };
        }
    }

    private static object _outputLock = new();

    private static async Task ReadStreamAsync(
        StreamReader stream,
        StringBuilder output,
        IProgress<string>? progressOutput,
        object outputLock,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await stream.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            var capped = Truncate(line);
            lock (outputLock)
            {
                if (output.Length < MaxOutputChars)
                {
                    output.AppendLine(capped);
                }
            }

            progressOutput?.Report(capped);
        }
    }

    private static string Tail(StringBuilder sb)
    {
        var s = sb.ToString();
        return s.Length <= MaxOutputChars ? s : s[^MaxOutputChars..] + "\n...(输出过长已截断)";
    }

    private static string Truncate(string s) => s.Length <= 8 * 1024 ? s : s[..(8 * 1024)] + "...";
}