using AIShikikan.Core.Services.Llm;

namespace AIShikikan.Core.Services;

/// <summary>会话标题生成: 调用 LLM 为会话生成简洁标题, GUI 与 CLI 共用。</summary>
public static class ChatTitleService
{
    /// <summary>首条用户消息后异步生成会话标题; 失败时静默返回 null(不阻塞主流程)。</summary>
    public static async Task<string?> GenerateAsync(
        LlmService llm, string userMessage, CancellationToken ct = default)
    {
        try
        {
            var provider = llm.GetProvider();
            if (provider is null) return null;

            var request = new ChatRequest
            {
                Model = llm.ResolveModel(),
                MaxTokens = 32,
                Temperature = 0.3,
                System = "你是一个会话标题生成助手。根据用户的消息生成一个简洁的中文标题(不超过20个字符), 只输出标题本身, 不要引号、不要标点、不要多余说明。",
                Messages =
                [
                    new ChatTurnMessage
                    {
                        Role = ChatMsgRole.User,
                        Content = userMessage.Length > 200 ? userMessage[..200] : userMessage
                    }
                ]
            };

            var response = await llm.GetClient(provider.Id).CompleteAsync(request, ct);
            if (response.IsError || string.IsNullOrWhiteSpace(response.Content)) return null;

            var title = Clean(response.Content);
            return title.Length > 0 ? title : null;
        }
        catch
        {
            // 标题生成失败不影响主流程
            return null;
        }
    }

    /// <summary>清理 LLM 生成的标题: 去掉引号与常见标点, 截断到 24 字符。</summary>
    public static string Clean(string text)
    {
        var title = text.Trim().Trim('"', '\'', '“', '”', '「', '」', '【', '】', '。', '：', ':');
        if (title.Length <= 24) return title;
        return title[..24].TrimEnd('…', '.', '。') + "...";
    }
}
