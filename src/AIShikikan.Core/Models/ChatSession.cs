using System.Text.Json.Serialization;

namespace AIShikikan.Core.Models;

public class ChatSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "New Session";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrEmpty(Title) ? "New Session" : Title;
}
