using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

/// <summary>消息分段类型。</summary>
public enum MessageSegmentKind
{
    /// <summary>正文(Markdown 文本)。</summary>
    Text,

    /// <summary>思考过程(模型流式返回的 reasoning 内容, 纯文本)。</summary>
    Thinking,

    /// <summary>工具调用。</summary>
    Tool,

    /// <summary>图片(多模态输入)。</summary>
    Image
}

/// <summary>工具调用分段的数据细节。</summary>
public class ToolSegment
{
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;

    /// <summary>流式执行过程中输出的逐行日志。</summary>
    public List<string> OutputLines { get; set; } = [];

    public string Result { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public bool IsDone { get; set; }

    /// <summary>关联的 git 检查点 ID(新检查点系统)。</summary>
    public string? CheckpointId { get; set; }

    /// <summary>关联的 git 检查点步骤 ID(旧步骤系统兼容, 只读不再写入)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; set; }

    /// <summary>结构化卡片展示数据(按工具类型渲染专属卡体), 会话回放时重建。</summary>
    public ToolCardDetail? Detail { get; set; }
}

/// <summary>会话消息的一个分段(正文 / 思考 / 工具调用 / 图片)。</summary>
public class MessageSegment
{
    public MessageSegmentKind Kind { get; set; } = MessageSegmentKind.Text;

    /// <summary>Kind 为 Text / Thinking 时的内容文本。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Kind 为 Tool 时的工具调用细节。</summary>
    public ToolSegment? Tool { get; set; }

    /// <summary>Kind 为 Image 时的图片数据(base64, 不含 dataURL 前缀)。</summary>
    public string? ImageData { get; set; }

    /// <summary>Kind 为 Image 时的 MIME 类型(如 image/png)。</summary>
    public string? ImageMimeType { get; set; }

    /// <summary>Kind 为 Image 时的原始文件名(展示与提示用)。</summary>
    public string? ImageName { get; set; }
}

public class ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public MessageRole Role { get; init; } = MessageRole.User;

    /// <summary>关联的 git 检查点 ID(用户消息发送前自动标记, 或 AI 工具创建)。</summary>
    public string? CheckpointId { get; set; }

    /// <summary>结构化分段内容。历史遗留整段文本(单 Text 分段)也兼容展示。</summary>
    public List<MessageSegment> Segments { get; set; } = [];

    public DateTime Timestamp { get; init; } = DateTime.Now;

    [JsonIgnore]
    public string RoleLabel => Role switch
    {
        MessageRole.User => "User",
        MessageRole.Assistant => "Assistant",
        MessageRole.System => "System",
        MessageRole.Tool => "Tool",
        _ => "Unknown"
    };
}