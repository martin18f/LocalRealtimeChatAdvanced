namespace LocalRealtimeChat.Api.Models;

public static class WebSocketMessageTypes
{
    public const string UserJoined = "user_joined";
    public const string ChatMessage = "chat_message";
    public const string HistoryMessage = "history_message";
    public const string OnlineUsers = "online_users";
    public const string TypingStarted = "typing_started";
    public const string TypingStopped = "typing_stopped";
    public const string Error = "error";
}