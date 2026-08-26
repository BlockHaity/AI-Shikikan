using AIShikikan.Core.Services;
using AIShikikan.Core.Services.Llm;
using AIShikikan.Core.Services.Usage;

namespace AIShikikan.Cli.Tui.Ui;

/// <summary>状态面板快照: 与 GUI StatusPanel / /status 命令共用同一份数据计算逻辑。</summary>
public sealed class StatusSnapshot
{
    public required string WorkspaceRoot { get; init; }
    public required string ModelText { get; init; }
    public required long ContextUsed { get; init; }
    public required long ContextTotal { get; init; }
    public required bool HasContext { get; init; }
    public required string ContextText { get; init; }
    public required double Percent { get; init; }
    public required long Calls { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long CachedTokens { get; init; }
    public required string CostText { get; init; }
    public required string PriceSource { get; init; }
    public required string Branch { get; init; }
    public required int AgentCount { get; init; }
    public required int ToolCount { get; init; }
    public required int PendingSteps { get; init; }
    public required string SessionTitle { get; init; }
    public required string SessionId { get; init; }

    public static StatusSnapshot Build(CommanderRuntime runtime, ChatService chatService)
    {
        var provider = runtime.Llm.GetProvider();
        var model = runtime.Llm.ResolveModel();
        var session = chatService.CurrentSession;

        var profile = ModelProfileService.Resolve(model, provider?.Id);
        var sessionId = session?.Id ?? "cli-default";
        var contextUsed = UsageStatsService.GetLastContextTokens(sessionId);
        var contextTotal = profile.ContextTokens;
        var hasContext = profile.Source != ProfileSource.Unknown && contextTotal > 0;
        var percent = contextTotal > 0 ? contextUsed * 100.0 / contextTotal : 0.0;
        var contextText = hasContext
            ? $"{contextUsed:N0} / {contextTotal:N0} tokens"
            : $"上下文窗口未知 ({contextUsed:N0} tokens 已使用)";

        var stat = UsageStatsService.GetSessionStat(sessionId);
        var cost = ModelProfileService.CalcCostUsd(profile, stat.InputTokens, stat.OutputTokens, stat.CachedTokens);
        var priceSource = profile.Source switch
        {
            ProfileSource.Manual => "手动配置 (models.toml)",
            ProfileSource.Api => "API 拉取",
            _ => "未知 (运行 /prices 拉取)"
        };

        var git = runtime.Git;
        var branch = git.IsRepoAvailable ? git.CurrentBranch() ?? "detached HEAD" : "非 git 仓库";

        return new StatusSnapshot
        {
            WorkspaceRoot = runtime.WorkspaceRoot,
            ModelText = string.IsNullOrWhiteSpace(model) ? "-" : $"{model} @ {provider?.Id ?? "-"}",
            ContextUsed = contextUsed,
            ContextTotal = contextTotal,
            HasContext = hasContext,
            ContextText = contextText,
            Percent = percent,
            Calls = stat.Calls,
            InputTokens = stat.InputTokens,
            OutputTokens = stat.OutputTokens,
            CachedTokens = stat.CachedTokens,
            CostText = ModelProfileService.FormatCost(cost),
            PriceSource = priceSource,
            Branch = branch,
            AgentCount = runtime.Agents.Count,
            ToolCount = runtime.Registry.All.Count,
            PendingSteps = runtime.Git.PendingReview().Count,
            SessionTitle = session?.DisplayTitle ?? "-",
            SessionId = sessionId
        };
    }
}
