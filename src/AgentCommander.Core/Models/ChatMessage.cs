using System.Text.Json.Serialization;

namespace AgentCommander.Core.Models;

public class ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public MessageRole Role { get; init; } = MessageRole.User;
    public string Content { get; set; } = string.Empty;
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
