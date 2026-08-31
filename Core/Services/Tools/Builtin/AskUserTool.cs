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

        var answer = await ctx.AskUser(question, ct);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return new ToolResult { Content = "用户未作答(已跳过)。请基于现有信息继续, 或说明缺失的假设。" };
        }

        return new ToolResult { Content = $"用户回答:\n{answer}" };
    }
}
