namespace LocalRealtimeChat.Wpf.Models;

public sealed class ChatMessageDto
{
    public string Username { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime SentAt { get; set; }
}