namespace AIShikikan.Cli.Models;

public class Message
{
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string RoleLabel => Role switch
    {
        MessageRole.User => "You",
        MessageRole.Assistant => "Assistant",
        MessageRole.System => "System",
        MessageRole.Tool => "Tool",
        _ => "Unknown"
    };
}
