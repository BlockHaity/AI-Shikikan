using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace AIShikikan.Core.Services.Agents;

public record CliAgentDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;

    /// <summary>参数模板, 支持 {prompt} 占位符。</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Plan 模式附加 CLI 参数(如 claude 的 --permission-mode plan)。
    /// 主对话处于 Plan 模式且该子代理被允许时追加; 为空表示该 Agent 不支持 Plan 模式。</summary>
    public List<string> PlanArgs { get; set; } = [];

    /// <summary>即使未配置 plan_args 也允许该 Agent 在 Plan 模式中被使用(以普通参数启动)。
    /// 默认 false: Plan 模式下仅 plan_args 非空的 Agent 可被授权。</summary>
    public bool AllowPlanModeWithoutArgs { get; set; }

    /// <summary>额外 CLI 参数: 始终附加到子 Agent 命令末尾(在 args / plan_args 之后), 支持 {prompt} 占位符。</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>"sync" 常规阻塞 | "async" 异步后台。</summary>
    public string DefaultMode { get; set; } = "sync";

    public int MaxConcurrent { get; set; } = 1;

    public bool RequireApproval { get; set; } = true;

    public int TimeoutMinutes { get; set; } = 30;

    public string Description { get; set; } = string.Empty;

    public string? RecommendedPersonaId { get; set; }

    public string? DefaultTemplateId { get; set; }

    /// <summary>自定义 Roster 条目, 非空时完全替换自动生成的条目。</summary>
    public string? RosterEntry { get; set; }

    public Dictionary<string, string> Environment { get; set; } = [];

    [JsonIgnore]
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

public static class CliAgentRunner
{
    private const int MaxOutputChars = 512 * 1024;

    public static async Task<CliAgentRunResult> RunAsync(
        CliAgentDefinition definition,
        string prompt,
        string workingDirectory,
        IProgress<string>? progressOutput,
        bool planMode = false,
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

        // Plan 模式: 追加 plan 专用参数(如 --permission-mode plan)
        if (planMode)
        {
            foreach (var arg in definition.PlanArgs)
            {
                psi.ArgumentList.Add(arg.Replace("{prompt}", prompt, StringComparison.Ordinal));
            }
        }

        // 额外参数: 始终附加到命令末尾
        foreach (var arg in definition.ExtraArgs)
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