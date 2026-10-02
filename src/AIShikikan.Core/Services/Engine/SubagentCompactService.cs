using System.Text;
using System.Text.Json;
using AIShikikan.Core.Logging;
using AIShikikan.Core.Services.Llm;

namespace AIShikikan.Core.Services.Engine;

/// <summary>
/// Subagent 输出压缩: 开启后 run_subagents / run_&lt;agent&gt; 的结果先经 LLM 压缩,
/// 保留任务结论、关键文件路径与数据, 剔除冗余过程输出, 降低主对话上下文占用。
/// 开关为会话级子代理配置(右侧栏每条目独立控制, 存于 roster.json)。
/// </summary>
public static class SubagentCompactService
{
    /// <summary>触发压缩的最小输出长度(字符), 短结果直接透传避免无谓调用。</summary>
    public const int MinLengthToCompact = 2000;

    /// <summary>压缩后目标长度上限(字符)。</summary>
    public const int TargetLength = 4000;

    /// <summary>
    /// 按该子代理的会话开关压缩输出; 失败或未启用时原样返回(不阻塞主流程)。
    /// </summary>
    /// <param name="providerId">当前回合实际使用的 Provider Id(来自 <c>EngineOptions.ProviderId</c>);
    /// 为 null 时回退到全局默认 <c>ActiveProvider</c>。</param>
    /// <param name="model">当前回合实际使用的模型(来自 <c>EngineOptions.Model</c> 解析结果);
    /// 为 null 时由 <see cref="LlmService.ResolveModel"/> 按该 Provider 推导默认模型。</param>
    /// <exception cref="OperationCanceledException">回合被取消时原样上抛, 不吞掉取消信号。</exception>
    public static async Task<string> CompactIfNeededAsync(
        LlmService llm, string agentDisplay, string output, bool enabled, CancellationToken ct,
        string? providerId = null, string? model = null)
    {
        if (!enabled || string.IsNullOrWhiteSpace(output) || output.Length < MinLengthToCompact)
        {
            return output;
        }

        try
        {
            // 走当前回合的 provider/model, 避免用户切了非默认模型后压缩落到另一个模型
            var provider = llm.GetProvider(providerId);
            if (provider is null) return output;

            var request = new ChatRequest
            {
                Model = llm.ResolveModel(model, providerId),
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
            if (compact.Length > TargetLength)
            {
                // 兜底: 模型不遵守"压缩"指令时硬截断, 防止压缩反而让主上下文变长
                var origin = compact.Length;
                compact = compact[..TargetLength] + $"\n...(压缩结果超长已截断, 原 {origin} 字符)";
                Log.Warn("Agent", $"子Agent 输出压缩结果超长已截断({agentDisplay}): {origin} → {TargetLength} 字符");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[已压缩 原始{output.Length}字符 → {compact.Length}字符]");
            sb.Append(compact);

            Log.Debug("Agent", $"子Agent 输出已压缩: {agentDisplay} ({output.Length} → {compact.Length} 字符)");
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            // 取消必须上抛: 否则用户点停止后, 还要等一次完整 LLM 往返才真正退出回合
            // (调用方 AgentToolFactory 转 ToolResult.Error, AgentEngine 再补占位 tool 结果, 链路完整)
            throw;
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
