namespace LocalRealtimeChat.Api.Models;

public sealed class ChatMessage
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime SentAt { get; set; }
}