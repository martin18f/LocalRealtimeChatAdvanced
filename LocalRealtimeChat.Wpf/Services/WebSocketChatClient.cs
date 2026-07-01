using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LocalRealtimeChat.Wpf.Models;

namespace LocalRealtimeChat.Wpf.Services;

public sealed class WebSocketChatClient : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private ClientWebSocket? _socket;

    public event Action<ChatMessageDto>? MessageReceived;

    public event Action<string>? StatusChanged;

    public async Task ConnectAsync(string webSocketUrl)
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
    }

    public async Task SendMessageAsync(ChatMessageDto message)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        string json = JsonSerializer.Serialize(message);

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

                ChatMessageDto? message = JsonSerializer.Deserialize<ChatMessageDto>(
                    receivedJson,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                );

                if (message is not null)
                {
                    MessageReceived?.Invoke(message);
                }
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

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        _socket?.Abort();
        _socket?.Dispose();

        _cancellationTokenSource.Dispose();
    }
}