using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LocalRealtimeChat.Wpf.Models;

namespace LocalRealtimeChat.Wpf.Services;

public sealed class WebSocketChatClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private ClientWebSocket? _socket;

    public event Action<ChatMessageDto>? MessageReceived;

    public event Action<IReadOnlyList<string>>? OnlineUsersChanged;

    public event Action<string, bool>? TypingChanged;

    public event Action<string>? StatusChanged;

    public async Task ConnectAsync(string webSocketUrl, string username)
    {
        if (_socket is not null && _socket.State == WebSocketState.Open)
        {
            return;
        }

        _socket = new ClientWebSocket();

        await _socket.ConnectAsync(
            new Uri(webSocketUrl),
            _cancellationTokenSource.Token
        );

        StatusChanged?.Invoke("Connected");

        _ = Task.Run(() => ReceiveLoopAsync(_cancellationTokenSource.Token));

        await SendEnvelopeAsync(
            WebSocketMessageTypes.UserJoined,
            new
            {
                username
            }
        );
    }

    public async Task SendMessageAsync(ChatMessageDto message)
    {
        await SendEnvelopeAsync(WebSocketMessageTypes.ChatMessage, message);
    }

    public async Task SendTypingStartedAsync()
    {
        await SendEnvelopeAsync(WebSocketMessageTypes.TypingStarted, new { });
    }

    public async Task SendTypingStoppedAsync()
    {
        await SendEnvelopeAsync(WebSocketMessageTypes.TypingStopped, new { });
    }

    private async Task SendEnvelopeAsync(string type, object payload)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        string json = JsonSerializer.Serialize(new
        {
            type,
            payload
        });

        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            _cancellationTokenSource.Token
        );
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[1024 * 4];

        try
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var memoryStream = new MemoryStream();

                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken
                    );

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        StatusChanged?.Invoke("Disconnected");
                        return;
                    }

                    memoryStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                string receivedJson = Encoding.UTF8.GetString(memoryStream.ToArray());

                WebSocketEnvelope? envelope = DeserializeEnvelope(receivedJson);

                if (envelope is null)
                {
                    continue;
                }

                HandleEnvelope(envelope);
            }
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Disconnected");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error: {ex.Message}");
        }
    }

    private static WebSocketEnvelope? DeserializeEnvelope(string json)
    {
        try
        {
            WebSocketEnvelope? envelope = JsonSerializer.Deserialize<WebSocketEnvelope>(
                json,
                JsonOptions
            );

            if (envelope is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(envelope.Type))
            {
                return null;
            }

            if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return null;
            }

            return envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void HandleEnvelope(WebSocketEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case WebSocketMessageTypes.ChatMessage:
            case WebSocketMessageTypes.HistoryMessage:
                HandleChatMessagePayload(envelope.Payload);
                break;

            case WebSocketMessageTypes.OnlineUsers:
                HandleOnlineUsersPayload(envelope.Payload);
                break;

            case WebSocketMessageTypes.TypingStarted:
                HandleTypingPayload(envelope.Payload, true);
                break;

            case WebSocketMessageTypes.TypingStopped:
                HandleTypingPayload(envelope.Payload, false);
                break;
        }
    }

    private void HandleChatMessagePayload(JsonElement payload)
    {
        try
        {
            ChatMessageDto? message = payload.Deserialize<ChatMessageDto>(JsonOptions);

            if (message is not null)
            {
                MessageReceived?.Invoke(message);
            }
        }
        catch (JsonException)
        {
            // Invalid message payload is ignored on the client side.
        }
    }

    private void HandleOnlineUsersPayload(JsonElement payload)
    {
        try
        {
            List<string>? users = payload.Deserialize<List<string>>(JsonOptions);

            if (users is not null)
            {
                OnlineUsersChanged?.Invoke(users);
            }
        }
        catch (JsonException)
        {
            // Invalid online users payload is ignored on the client side.
        }
    }

    private void HandleTypingPayload(JsonElement payload, bool isTyping)
    {
        try
        {
            TypingPayload? typingPayload = payload.Deserialize<TypingPayload>(JsonOptions);

            if (typingPayload is not null && !string.IsNullOrWhiteSpace(typingPayload.Username))
            {
                TypingChanged?.Invoke(typingPayload.Username, isTyping);
            }
        }
        catch (JsonException)
        {
            // Invalid typing payload is ignored on the client side.
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        _socket?.Abort();
        _socket?.Dispose();

        _cancellationTokenSource.Dispose();
    }

    private sealed class TypingPayload
    {
        public string Username { get; set; } = string.Empty;
    }
}