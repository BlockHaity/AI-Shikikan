using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

public class ChatSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "New Session";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>会话绑定的工作目录(首次发送消息时记录, 用于按目录整理会话与切换会话时恢复)。</summary>
    public string WorkDir { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? "New Session" : Title;

    /// <summary>消息是否已从会话文件加载(懒加载: 启动时仅解析元数据, 打开会话时才加载消息)。</summary>
    [JsonIgnore]
    public bool IsLoaded { get; set; } = true;

    /// <summary>未加载消息时由元数据扫描得到的消息条数。</summary>
    [JsonIgnore]
    public int MetadataMessageCount { get; set; }

    /// <summary>消息条数(优先内存, 未加载时用元数据)。</summary>
    [JsonIgnore]
    public int MessageCount => IsLoaded ? Messages.Count : MetadataMessageCount;
}
