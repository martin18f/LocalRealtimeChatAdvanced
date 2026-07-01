using System.Text.Json;

namespace LocalRealtimeChat.Wpf.Models;

public sealed class WebSocketEnvelope
{
    public string Type { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }
}