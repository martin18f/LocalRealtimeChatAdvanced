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
        string json = JsonSerializer.Serialize(message);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await SendToClientAsync(clientId, client, bytes, cancellationToken);
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
            string? receivedMessage = await ReceiveTextMessageAsync(client.Socket, cancellationToken);

            if (receivedMessage is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(receivedMessage))
            {
                continue;
            }

            ChatMessageDto? incomingMessage = DeserializeMessage(receivedMessage);

            if (incomingMessage is null)
            {
                _logger.LogWarning("Invalid message received from {ClientId}", clientId);
                continue;
            }

            ChatMessageDto savedMessage = await SaveMessageAsync(incomingMessage, cancellationToken);

            string outgoingJson = JsonSerializer.Serialize(savedMessage);

            _logger.LogInformation(
                "Message saved and broadcasted from {Username}: {Content}",
                savedMessage.Username,
                savedMessage.Content
            );

            await BroadcastAsync(outgoingJson, cancellationToken);
        }
    }

    private static ChatMessageDto? DeserializeMessage(string json)
    {
        try
        {
            ChatMessageDto? message = JsonSerializer.Deserialize<ChatMessageDto>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }
            );

            if (message is null)
            {
                return null;
            }

            message.Username = message.Username.Trim();
            message.Content = message.Content.Trim();

            if (string.IsNullOrWhiteSpace(message.Username))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(message.Content))
            {
                return null;
            }

            return message;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private async Task BroadcastAsync(string message, CancellationToken cancellationToken)
    {
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        var sendTasks = _clients
            .Where(client => client.Value.Socket.State == WebSocketState.Open)
            .Select(client => SendToClientAsync(client.Key, client.Value, messageBytes, cancellationToken));

        await Task.WhenAll(sendTasks);
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

        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}