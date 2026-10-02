using System.Text.Json;

namespace AIShikikan.Core.Services.Llm;

public enum ChatMsgRole
{
    System,
    User,
    Assistant,
    Tool
}

public enum ProviderKind
{
    OpenAi,
    Anthropic
}

public class ToolCallData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>用户消息附带的图片(多模态输入): base64 数据 + MIME 类型。</summary>
public class ChatImagePart
{
    public string Base64Data { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/png";
}

public class ChatTurnMessage
{
    public ChatMsgRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public List<ToolCallData>? ToolCalls { get; set; }

    /// <summary>用户消息的图片附件(可选, 仅 User 角色使用)。</summary>
    public List<ChatImagePart>? Images { get; set; }
}

public class ToolSpec
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonElement Parameters { get; set; }
}

public class ChatRequest
{
    public string Model { get; set; } = string.Empty;
    public string? System { get; set; }
    public List<ChatTurnMessage> Messages { get; set; } = [];
    public List<ToolSpec>? Tools { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.2;

    /// <summary>思考深度等级, 供客户端映射为 API 参数(如 OpenAI reasoning_effort)。</summary>
    public ThinkingLevel Thinking { get; set; } = ThinkingLevel.Auto;
}

public class ChatUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>命中的缓存输入 token 数(Anthropic cache_read / OpenAI cached_tokens)。</summary>
    public int CachedInputTokens { get; set; }
}

public class ChatCompletionResult
{
    public bool IsError { get; set; }
    public string? Error { get; set; }
    public string? Content { get; set; }
    public List<ToolCallData>? ToolCalls { get; set; }
    public ChatUsage Usage { get; set; } = new();
    public string FinishReason { get; set; } = string.Empty;
}

public enum StreamEventKind
{
    TextDelta,
    ThinkingDelta,
    ToolCallStarted,
    ToolCallCompleted,
    Done,
    Error
}

public class ChatStreamEvent
{
    public StreamEventKind Kind { get; set; }
    public string? Text { get; set; }

    /// <summary>思考内容增量(仅支持的模型, 遵循 OpenAI 规范: OpenAI chat 不返回, Anthropic thinking delta 等)。</summary>
    public string? Thinking { get; set; }

    public ToolCallData? ToolCall { get; set; }
    public ChatCompletionResult? Final { get; set; }
    public string? Error { get; set; }
}