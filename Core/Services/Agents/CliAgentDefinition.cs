using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using AIShikikan.Core.Logging;

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

    /// <summary>"sync" 常规阻塞 | "async" 异步后台。
    /// 注意: 当前**没有** async 的执行实现路径(引擎只有阻塞式 RunSyncAsync),
    /// 该字段已从 Roster 提示词中移除展示, 字段保留仅为兼容既有 agents.toml。</summary>
    public string DefaultMode { get; set; } = "sync";

    public bool RequireApproval { get; set; } = true;

    /// <summary>子 Agent 超时上限(分钟)。默认值 30 分钟适用于绝大多数编码任务。
    /// 约定: &lt;= 0 表示**不设超时**(而不是"立即超时"), 否则 0 会被当成超时导致每次调用秒失败。
    /// 运行时会再钳到 <see cref="CliAgentRunner.MaxTimeoutMinutes"/> 上界, 防止误配(例如 999999)把子进程无限挂起。</summary>
    public int TimeoutMinutes { get; set; } = 30;

    public string Description { get; set; } = string.Empty;

    public string? RecommendedPersonaId { get; set; }

    /// <summary>该 Agent 的默认任务模板 id。
    /// 注意: 当前**未参与实际执行**(仅在 Roster 文本里展示), 保留字段以兼容既有 agents.toml;
    /// 接入模板默认值属于后续功能, 不要因为"没人读"就删掉——用户的配置文件里还写着它。</summary>
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

    /// <summary>由外部取消(用户点停止 / 回合取消)导致终止, 与 <see cref="TimedOut"/> 区分。</summary>
    public bool Cancelled { get; init; }

    public required TimeSpan Elapsed { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

public static class CliAgentRunner
{
    private const int MaxOutputChars = 512 * 1024;

    /// <summary>超时上限(分钟, 24 小时): 防御 timeout_minutes 误配成超大值把子进程无限挂起。
    /// 注意这**不**限制并发数——run_subagents 下的 N 个子代理各自独立计时, 并发上限是引擎侧的责任。</summary>
    public const int MaxTimeoutMinutes = 24 * 60;

    /// <summary>命令模板里的 prompt 占位符。</summary>
    private const string PromptPlaceholder = "{prompt}";

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
            // 必须重定向 stdin: 否则子代理直接继承 GUI 进程的 stdin(桌面启动时通常无终端),
            // 一方面与 GUI 的终端交互互相打架, 另一方面配合下面的 git 环境变量,
            // 让"没 TTY"这一信号传给子进程, 凭据提示会直接失败而不是挂死。
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // 禁止一切交互式凭据提示: 子代理若在同一仓库跑 git pull, 无 TTY 时 git 会退化为
        // 无限等待终端输入, 只能等到 TimeoutMinutes(默认 30 分钟)才被强杀。
        // 与 GitService.Run 保持同一套变量(GitService 由 A10 维护, 不要去改那边)。
        // 先注入默认值, 再让用户的 Environment 覆盖——保证"默认安全"且"用户显式配置优先"。
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_ASKPASS"] = "echo";

        foreach (var kv in definition.Environment)
        {
            psi.Environment[kv.Key] = kv.Value;
        }

        // {prompt} 在整条命令行中**最多出现一次**: args 先消费占位符,
        // 之后 plan_args / extra_args 里的同名占位符按字面量原样透传。
        // 否则用户在 plan_args 里也写了 {prompt} 时, prompt 会被重复传参,
        // 多数 CLI 会把它当成两次独立请求(重复改文件 / 重复烧 token)。
        var promptConsumed = false;

        string SubstitutePrompt(string arg)
        {
            if (promptConsumed || !arg.Contains(PromptPlaceholder, StringComparison.Ordinal))
            {
                return arg;
            }

            promptConsumed = true;
            return arg.Replace(PromptPlaceholder, prompt, StringComparison.Ordinal);
        }

        foreach (var arg in definition.Args)
        {
            psi.ArgumentList.Add(SubstitutePrompt(arg));
        }

        // Plan 模式: 追加 plan 专用参数(如 --permission-mode plan)
        if (planMode)
        {
            foreach (var arg in definition.PlanArgs)
            {
                psi.ArgumentList.Add(SubstitutePrompt(arg));
            }
        }

        // 额外参数: 始终附加到命令末尾
        foreach (var arg in definition.ExtraArgs)
        {
            psi.ArgumentList.Add(SubstitutePrompt(arg));
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
            // 锁是本次 run 的局部对象: 并发子代理(run_subagents 下的 Task.WhenAll)
            // 各自只锁自己的 builder, 不再争抢同一把全局静态锁。
            var outputLock = new object();

            // 重定向 stdin 后必须立刻关闭写端, 让子进程读到 EOF;
            // 否则一个期待输入的 CLI 会一直干等, 白白耗掉整个超时窗口。
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // 关闭失败(个别 CLI 启动即退出)不影响主流程: 子进程随后被 Kill 时管道自然断开
            }

            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // TimeoutMinutes <= 0 视为"不设超时": 交给 CancelAfter(0) 会立刻触发,
            // 于是每次调用都会被误判成 TimedOut, 用户看到的是"启动即超时"。
            // 上界钳制: 防御 timeout_minutes = 999999 这类误配。
            var timeoutMinutes = Math.Clamp(definition.TimeoutMinutes, 0, MaxTimeoutMinutes);
            if (timeoutMinutes > 0 && timeoutMinutes != definition.TimeoutMinutes)
            {
                Log.Warn("Agents",
                    $"Agent {definition.Id} 的 timeout_minutes={definition.TimeoutMinutes} 超出上界, 已按 {MaxTimeoutMinutes} 分钟执行");
            }

            if (timeoutMinutes > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            }

            var readTask = Task.WhenAll(
                ReadStreamAsync(process.StandardOutput, output, progressOutput, outputLock, timeoutCts.Token),
                ReadStreamAsync(process.StandardError, output, progressOutput, outputLock, timeoutCts.Token)
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

                // 区分来源: ct 是外部取消(用户点停止 / 回合取消), timeoutCts.CancelAfter 才是真超时。
                // 二者共用同一个 linked token, 只能靠"外部 token 是否已请求取消"来判别。
                var userCancelled = ct.IsCancellationRequested;

                // readTask 此刻已被孤儿化(两条读流都会因 token 取消而抛 OCE)。
                // 显式观测一次, 免得管道关闭引发的异常成为未观察任务异常;
                // 进程已 Kill, 管道随即关闭, 因此这个等待通常立即完成——仍加一层短超时兜底,
                // 保证"观测"这个动作本身不会把回合卡住。
                await ObserveReadTaskAsync(readTask).ConfigureAwait(false);

                return new CliAgentRunResult
                {
                    ExitCode = -1,
                    Output = Tail(output) + (userCancelled
                        ? "\n[已取消] 用户终止了子代理。"
                        : "\n[超时] 子代理超过限制时间已强制终止。"),
                    TimedOut = !userCancelled,
                    Cancelled = userCancelled,
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

    /// <summary>观测一次管道读取任务并吞掉它的失败(取消 / 超时 / 管道异常都是这条路径的预期结果)。
    /// 加 5 秒上界: 观测动作本身绝不能把回合卡住。</summary>
    private static async Task ObserveReadTaskAsync(Task readTask)
    {
        try
        {
            await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

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
                    // 按剩余额度精确裁剪: 保证 output.Length 永不超过 MaxOutputChars,
                    // 这样读取端的 Tail 就不需要再自己截一次(否则那个分支形同虚设)。
                    var room = MaxOutputChars - output.Length;
                    output.Append(capped.Length <= room ? capped : capped[..room]);
                    output.AppendLine();
                }
            }

            progressOutput?.Report(capped);
        }
    }

    /// <summary>取出累计输出。写入端已按 <see cref="MaxOutputChars"/> 精确封顶, 这里无需再截。
    /// 真正对用户可见的截断发生在引擎侧: 工具结果落库前会再做一次约 12KB 的尾部裁剪。</summary>
    private static string Tail(StringBuilder sb) => sb.ToString();

    private static string Truncate(string s) => s.Length <= 8 * 1024 ? s : s[..(8 * 1024)] + "...";
}