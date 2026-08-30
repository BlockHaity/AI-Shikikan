using System.Text;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Services.Llm;

namespace AIShikikan.Core.Services.Engine;

/// <summary>
/// Subagent 输出压缩: 开启后 run_subagents / run_&lt;agent&gt; 的结果先经 LLM 压缩,
/// 保留任务结论、关键文件路径与数据, 剔除冗余过程输出, 降低主对话上下文占用。
/// </summary>
public static class SubagentCompactService
{
    /// <summary>全局开关(GUI 设置面板控制; 默认关闭)。</summary>
    public static bool Enabled { get; set; }

    /// <summary>启动时从偏好配置恢复开关状态。</summary>
    public static void Restore(ThemeService prefs) => Enabled = prefs.CompactSubagents;

    /// <summary>开关变化时同步到偏好配置持久化。</summary>
    public static void Persist(ThemeService prefs, bool enabled)
    {
        Enabled = enabled;
        prefs.CompactSubagents = enabled;
    }

    /// <summary>触发压缩的最小输出长度(字符), 短结果直接透传避免无谓调用。</summary>
    public const int MinLengthToCompact = 2000;

    /// <summary>压缩后目标长度上限(字符)。</summary>
    public const int TargetLength = 4000;

    /// <summary>
    /// 按开关压缩子 Agent 输出; 失败或未启用时原样返回(不阻塞主流程)。
    /// </summary>
    public static async Task<string> CompactIfNeededAsync(
        LlmService llm, string agentDisplay, string output, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(output) || output.Length < MinLengthToCompact)
        {
            return output;
        }

        try
        {
            var provider = llm.GetProvider();
            if (provider is null) return output;

            var request = new ChatRequest
            {
                Model = llm.ResolveModel(),
                MaxTokens = 2048,
                Temperature = 0.1,
                System = """
                    你是子Agent输出压缩器。把给定的子Agent执行结果压缩为一份简明纪要, 要求:
                    1. 保留: 最终结论、修改/创建的文件路径、关键数据与错误信息;
                    2. 剔除: 冗长的过程日志、重复内容、无关细节;
                    3. 用紧凑的分点列表输出, 不添加评论和开场白。
                    """,
                Messages =
                [
                    new ChatTurnMessage
                    {
                        Role = ChatMsgRole.User,
                        Content = $"子Agent「{agentDisplay}」的原始输出:\n\n{TruncateHead(output)}"
                    }
                ]
            };

            var response = await llm.GetClient(provider.Id).CompleteAsync(request, ct);
            if (response.IsError || string.IsNullOrWhiteSpace(response.Content))
            {
                Log.Debug("Agent", $"子Agent 输出压缩未生效({agentDisplay}): {response.Error ?? "空内容"}");
                return output;
            }

            var compact = response.Content.Trim();
            var sb = new StringBuilder();
            sb.AppendLine($"[已压缩 原始{output.Length}字符 → {compact.Length}字符]");
            sb.Append(compact);

            Log.Debug("Agent", $"子Agent 输出已压缩: {agentDisplay} ({output.Length} → {compact.Length} 字符)");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            // 压缩失败不影响工具结果返回
            Log.Warn("Agent", ex, $"子Agent 输出压缩失败({agentDisplay}), 使用原始输出");
            return output;
        }
    }

    private static string TruncateHead(string s)
        => s.Length <= 24000 ? s : s[..12000] + "\n...(中段过长已省略)...\n" + s[^6000..];
}
