using System.Text.Json;

namespace AIShikikan.Core.Services.Tools.Builtin;

/// <summary>向用户反问工具: AI 遇到需要用户决策/补充信息时暂停并等待用户回答。</summary>
public class AskUserTool : ITool
{
    public string Name => "ask_user";

    public string Description =>
        "向用户提出一个问题并等待其回答。当任务信息不足、存在多种合理方案需要用户拍板、" +
        "或需要用户提供额外材料(如具体路径、偏好、验收标准)时使用。" +
        "参数: question(要问用户的问题, 应清晰具体, 可列出候选选项)。返回用户的原始回答文本。";

    public JsonElement Parameters { get; } = ToolSchema.Json("""
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "要向用户提出的问题" }
          },
          "required": ["question"]
        }
""");

    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        var question = args.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(question))
        {
            return ToolResult.Error("缺少参数 question。");
        }

        if (ctx.AskUser is null)
        {
            return ToolResult.Error("当前环境不支持向用户提问。");
        }

        // ToolContext.AskUser 的返回约定(实现在 AgentEngine, 本文件不拥有它):
        // - 返回 string: 用户给出的原始回答文本;
        // - 返回 null / 空串: 用户未作答(跳过), 工具以"未作答"文案正常结束, 不是错误;
        // - 抛 OperationCanceledException: 回合被用户中断, 必须原样上抛(不要转成"未作答");
        // - 抛 TimeoutException: 实现侧有 ApprovalTimeout(5 分钟)兜底, 无人应答时超时。
        //   不接住的话会被引擎的 catch(Exception) 兜成"工具执行异常: ...", LLM 拿不到"跳过"语义,
        //   反而可能反复重问 —— 这里按"未作答"处理。
        string? answer;
        try
        {
            answer = await ctx.AskUser(question, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            answer = null;
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            return new ToolResult { Content = "用户未作答(已跳过)。请基于现有信息继续, 或说明缺失的假设。" };
        }

        return new ToolResult { Content = $"用户回答:\n{answer}" };
    }
}
