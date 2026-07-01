using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LocalRealtimeChat.Wpf.Models;
using LocalRealtimeChat.Wpf.Services;

namespace LocalRealtimeChat.Wpf;

public partial class MainWindow : Window
{
    private const string WebSocketUrl = "ws://localhost:5065/ws/chat";

    private readonly WebSocketChatClient _chatClient = new();

    private readonly ObservableCollection<DisplayChatMessage> _messages = new();

    private readonly ObservableCollection<string> _onlineUsers = new();

    private readonly HashSet<string> _typingUsers = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _typingStopTimer = new();

    private bool _isConnected;

    private bool _isTyping;

    public MainWindow()
    {
        InitializeComponent();

        MessagesListBox.ItemsSource = _messages;
        OnlineUsersListBox.ItemsSource = _onlineUsers;

        _chatClient.MessageReceived += OnMessageReceived;
        _chatClient.OnlineUsersChanged += OnOnlineUsersChanged;
        _chatClient.TypingChanged += OnTypingChanged;
        _chatClient.StatusChanged += OnStatusChanged;

        _typingStopTimer.Interval = TimeSpan.FromMilliseconds(900);
        _typingStopTimer.Tick += TypingStopTimer_Tick;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(
                "Zadaj meno používateľa.",
                "Chýba meno",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );

            return;
        }

        try
        {
            ConnectButton.IsEnabled = false;
            StatusTextBlock.Text = "Connecting...";

            await _chatClient.ConnectAsync(WebSocketUrl, username);

            _isConnected = true;

            UsernameTextBox.IsEnabled = false;
            MessageTextBox.IsEnabled = true;
            SendButton.IsEnabled = true;

            ConnectButton.Content = "Pripojené";
            StatusTextBlock.Text = "Connected";

            MessageTextBox.Focus();
        }
        catch (Exception ex)
        {
            _isConnected = false;

            ConnectButton.IsEnabled = true;
            StatusTextBlock.Text = "Connection failed";

            MessageBox.Show(
                $"Nepodarilo sa pripojiť na WebSocket server.\n\n{ex.Message}",
                "Chyba pripojenia",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    private async void MessageTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isConnected)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(MessageTextBox.Text))
        {
            if (!_isTyping)
            {
                _isTyping = true;
                await _chatClient.SendTypingStartedAsync();
            }

            _typingStopTimer.Stop();
            _typingStopTimer.Start();
        }
        else
        {
            await StopTypingAsync();
        }
    }

    private async void TypingStopTimer_Tick(object? sender, EventArgs e)
    {
        await StopTypingAsync();
    }

    private async Task StopTypingAsync()
    {
        _typingStopTimer.Stop();

        if (!_isConnected || !_isTyping)
        {
            return;
        }

        _isTyping = false;

        try
        {
            await _chatClient.SendTypingStoppedAsync();
        }
        catch
        {
            // Typing state is not critical for chat functionality.
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCurrentMessageAsync();
    }

    private async void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendCurrentMessageAsync();
        }
    }

    private async Task SendCurrentMessageAsync()
    {
        if (!_isConnected)
        {
            return;
        }

        string username = UsernameTextBox.Text.Trim();
        string content = MessageTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var message = new ChatMessageDto
        {
            Username = username,
            Content = content,
            SentAt = DateTime.Now
        };

        try
        {
            await _chatClient.SendMessageAsync(message);
            MessageTextBox.Clear();
            await StopTypingAsync();
            MessageTextBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Správu sa nepodarilo odoslať.\n\n{ex.Message}",
                "Chyba odoslania",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    private void OnMessageReceived(ChatMessageDto message)
    {
        Dispatcher.Invoke(() =>
        {
            string currentUsername = UsernameTextBox.Text.Trim();

            DateTime sentAt = message.SentAt == default
                ? DateTime.Now
                : message.SentAt.ToLocalTime();

            var displayMessage = new DisplayChatMessage
            {
                Username = message.Username,
                Content = message.Content,
                SentAtText = sentAt.ToString("HH:mm:ss"),
                IsOwnMessage = string.Equals(
                    message.Username,
                    currentUsername,
                    StringComparison.OrdinalIgnoreCase
                )
            };

            _messages.Add(displayMessage);

            MessagesListBox.ScrollIntoView(displayMessage);
        });
    }

    private void OnOnlineUsersChanged(IReadOnlyList<string> users)
    {
        Dispatcher.Invoke(() =>
        {
            _onlineUsers.Clear();

            foreach (string user in users)
            {
                _onlineUsers.Add(user);
            }

            OnlineCountTextBlock.Text = users.Count == 1
                ? "1 online"
                : $"{users.Count} online";
        });
    }

    private void OnTypingChanged(string username, bool isTyping)
    {
        Dispatcher.Invoke(() =>
        {
            string currentUsername = UsernameTextBox.Text.Trim();

            if (string.Equals(username, currentUsername, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (isTyping)
            {
                _typingUsers.Add(username);
            }
            else
            {
                _typingUsers.Remove(username);
            }

            UpdateTypingText();
        });
    }

    private void UpdateTypingText()
    {
        if (_typingUsers.Count == 0)
        {
            TypingTextBlock.Text = "";
            return;
        }

        if (_typingUsers.Count == 1)
        {
            TypingTextBlock.Text = $"{_typingUsers.First()} píše...";
            return;
        }

        TypingTextBlock.Text = "Viacerí používatelia píšu...";
    }

    private void OnStatusChanged(string status)
    {
        Dispatcher.Invoke(() =>
        {
            StatusTextBlock.Text = status;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _typingStopTimer.Stop();
        _chatClient.Dispose();
        base.OnClosed(e);
    }

    private sealed class DisplayChatMessage
    {
        public string Username { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public string SentAtText { get; set; } = string.Empty;

        public bool IsOwnMessage { get; set; }
    }
}