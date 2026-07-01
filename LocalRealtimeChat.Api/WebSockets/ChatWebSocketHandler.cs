using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LocalRealtimeChat.Api.Data;
using LocalRealtimeChat.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalRealtimeChat.Api.WebSockets;

public class ChatWebSocketHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ChatWebSocketHandler> _logger;

    public ChatWebSocketHandler(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<ChatWebSocketHandler> logger
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket request expected.");
            return;
        }

        using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();

        string clientId = Guid.NewGuid().ToString();
        var connectedClient = new ConnectedClient(webSocket);

        _clients.TryAdd(clientId, connectedClient);

        _logger.LogInformation(
            "Client connected: {ClientId}. Connected clients: {ClientCount}",
            clientId,
            _clients.Count
        );

        await SendRecentMessagesAsync(clientId, connectedClient, context.RequestAborted);

        try
        {
            await ReceiveLoopAsync(clientId, connectedClient, context.RequestAborted);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);

            await BroadcastOnlineUsersAsync(CancellationToken.None);

            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed.",
                    CancellationToken.None
                );
            }

            _logger.LogInformation(
                "Client disconnected: {ClientId}. Connected clients: {ClientCount}",
                clientId,
                _clients.Count
            );
        }
    }

    private async Task ReceiveLoopAsync(
        string clientId,
        ConnectedClient client,
        CancellationToken cancellationToken
    )
    {
        while (client.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            string? receivedText = await ReceiveTextMessageAsync(client.Socket, cancellationToken);

            if (receivedText is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(receivedText))
            {
                continue;
            }

            WebSocketEnvelope? envelope = DeserializeEnvelope(receivedText);

            if (envelope is null)
            {
                _logger.LogWarning("Invalid WebSocket envelope received from {ClientId}", clientId);
                continue;
            }

            switch (envelope.Type)
            {
                case WebSocketMessageTypes.UserJoined:
                    await HandleUserJoinedAsync(clientId, client, envelope, cancellationToken);
                    break;

                case WebSocketMessageTypes.ChatMessage:
                    await HandleChatMessageAsync(clientId, client, envelope, cancellationToken);
                    break;

                case WebSocketMessageTypes.TypingStarted:
                    await HandleTypingAsync(
                        clientId,
                        client,
                        WebSocketMessageTypes.TypingStarted,
                        cancellationToken
                    );
                    break;

                case WebSocketMessageTypes.TypingStopped:
                    await HandleTypingAsync(
                        clientId,
                        client,
                        WebSocketMessageTypes.TypingStopped,
                        cancellationToken
                    );
                    break;

                default:
                    _logger.LogWarning(
                        "Unsupported WebSocket message type from {ClientId}: {MessageType}",
                        clientId,
                        envelope.Type
                    );
                    break;
            }
        }
    }

    private async Task HandleUserJoinedAsync(
        string clientId,
        ConnectedClient client,
        WebSocketEnvelope envelope,
        CancellationToken cancellationToken
    )
    {
        UserJoinedPayload? payload = DeserializePayload<UserJoinedPayload>(envelope.Payload);

        if (payload is null)
        {
            return;
        }

        string username = payload.Username.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        if (username.Length > 30)
        {
            username = username[..30];
        }

        client.Username = username;

        _logger.LogInformation(
            "Client {ClientId} identified as {Username}",
            clientId,
            username
        );

        await BroadcastOnlineUsersAsync(cancellationToken);
    }

    private async Task HandleChatMessageAsync(
        string clientId,
        ConnectedClient client,
        WebSocketEnvelope envelope,
        CancellationToken cancellationToken
    )
    {
        ChatMessageDto? incomingMessage = DeserializePayload<ChatMessageDto>(envelope.Payload);

        if (incomingMessage is null)
        {
            _logger.LogWarning("Invalid chat message payload from {ClientId}", clientId);
            return;
        }

        string username = string.IsNullOrWhiteSpace(client.Username)
            ? incomingMessage.Username.Trim()
            : client.Username;

        string content = incomingMessage.Content.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (content.Length > 2000)
        {
            content = content[..2000];
        }

        var normalizedMessage = new ChatMessageDto
        {
            Username = username,
            Content = content,
            SentAt = DateTime.UtcNow
        };

        ChatMessageDto savedMessage = await SaveMessageAsync(normalizedMessage, cancellationToken);

        _logger.LogInformation(
            "Message saved and broadcasted from {Username}: {Content}",
            savedMessage.Username,
            savedMessage.Content
        );

        await BroadcastEnvelopeAsync(
            WebSocketMessageTypes.ChatMessage,
            savedMessage,
            cancellationToken
        );
    }

    private async Task HandleTypingAsync(
        string clientId,
        ConnectedClient client,
        string messageType,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(client.Username))
        {
            return;
        }

        await BroadcastEnvelopeExceptAsync(
            clientId,
            messageType,
            new
            {
                username = client.Username
            },
            cancellationToken
        );
    }

    private async Task SendRecentMessagesAsync(
        string clientId,
        ConnectedClient client,
        CancellationToken cancellationToken
    )
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();

        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<ChatMessageDto> recentMessages = await dbContext.ChatMessages
            .OrderByDescending(message => message.SentAt)
            .Take(50)
            .OrderBy(message => message.SentAt)
            .Select(message => new ChatMessageDto
            {
                Username = message.Username,
                Content = message.Content,
                SentAt = message.SentAt
            })
            .ToListAsync(cancellationToken);

        foreach (ChatMessageDto message in recentMessages)
        {
            await SendEnvelopeToClientAsync(
                clientId,
                client,
                WebSocketMessageTypes.HistoryMessage,
                message,
                cancellationToken
            );
        }
    }

    private async Task BroadcastOnlineUsersAsync(CancellationToken cancellationToken)
    {
        List<string> onlineUsers = _clients.Values
            .Select(client => client.Username)
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username)
            .ToList();

        await BroadcastEnvelopeAsync(
            WebSocketMessageTypes.OnlineUsers,
            onlineUsers,
            cancellationToken
        );
    }

    private async Task<ChatMessageDto> SaveMessageAsync(
        ChatMessageDto incomingMessage,
        CancellationToken cancellationToken
    )
    {
        await using AsyncServiceScope scope = _serviceScopeFactory.CreateAsyncScope();

        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var chatMessage = new ChatMessage
        {
            Username = incomingMessage.Username,
            Content = incomingMessage.Content,
            SentAt = DateTime.UtcNow
        };

        dbContext.ChatMessages.Add(chatMessage);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new ChatMessageDto
        {
            Username = chatMessage.Username,
            Content = chatMessage.Content,
            SentAt = chatMessage.SentAt
        };
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

    private static T? DeserializePayload<T>(JsonElement payload)
        where T : class
    {
        try
        {
            return payload.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[1024 * 4];

        using var memoryStream = new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken
            );

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return null;
            }

            memoryStream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    private async Task BroadcastEnvelopeAsync(
        string type,
        object payload,
        CancellationToken cancellationToken
    )
    {
        byte[] messageBytes = CreateEnvelopeBytes(type, payload);

        var sendTasks = _clients
            .Where(client => client.Value.Socket.State == WebSocketState.Open)
            .Select(client => SendToClientAsync(client.Key, client.Value, messageBytes, cancellationToken));

        await Task.WhenAll(sendTasks);
    }

    private async Task BroadcastEnvelopeExceptAsync(
        string excludedClientId,
        string type,
        object payload,
        CancellationToken cancellationToken
    )
    {
        byte[] messageBytes = CreateEnvelopeBytes(type, payload);

        var sendTasks = _clients
            .Where(client =>
                client.Key != excludedClientId &&
                client.Value.Socket.State == WebSocketState.Open
            )
            .Select(client => SendToClientAsync(client.Key, client.Value, messageBytes, cancellationToken));

        await Task.WhenAll(sendTasks);
    }

    private async Task SendEnvelopeToClientAsync(
        string clientId,
        ConnectedClient client,
        string type,
        object payload,
        CancellationToken cancellationToken
    )
    {
        byte[] messageBytes = CreateEnvelopeBytes(type, payload);

        await SendToClientAsync(clientId, client, messageBytes, cancellationToken);
    }

    private static byte[] CreateEnvelopeBytes(string type, object payload)
    {
        string json = JsonSerializer.Serialize(new
        {
            type,
            payload
        });

        return Encoding.UTF8.GetBytes(json);
    }

    private async Task SendToClientAsync(
        string clientId,
        ConnectedClient client,
        byte[] messageBytes,
        CancellationToken cancellationToken
    )
    {
        await client.SendLock.WaitAsync(cancellationToken);

        try
        {
            if (client.Socket.State != WebSocketState.Open)
            {
                return;
            }

            await client.Socket.SendAsync(
                new ArraySegment<byte>(messageBytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to client {ClientId}", clientId);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private sealed class ConnectedClient
    {
        public ConnectedClient(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        public string Username { get; set; } = string.Empty;

        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private sealed class UserJoinedPayload
    {
        public string Username { get; set; } = string.Empty;
    }
}