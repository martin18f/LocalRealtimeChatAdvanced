using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using LocalRealtimeChat.Wpf.Models;
using LocalRealtimeChat.Wpf.Services;

namespace LocalRealtimeChat.Wpf;

public partial class MainWindow : Window
{
    private const string WebSocketUrl = "ws://localhost:5065/ws/chat";

    private readonly WebSocketChatClient _chatClient = new();

    private readonly ObservableCollection<string> _messages = new();

    private bool _isConnected;

    public MainWindow()
    {
        InitializeComponent();

        MessagesListBox.ItemsSource = _messages;

        _chatClient.MessageReceived += OnMessageReceived;
        _chatClient.StatusChanged += OnStatusChanged;
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

            await _chatClient.ConnectAsync(WebSocketUrl);

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
            string sentAt = message.SentAt == default
                ? DateTime.Now.ToString("HH:mm:ss")
                : message.SentAt.ToString("HH:mm:ss");

            string displayMessage = $"[{sentAt}] {message.Username}: {message.Content}";

            _messages.Add(displayMessage);

            MessagesListBox.ScrollIntoView(displayMessage);
        });
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
        _chatClient.Dispose();
        base.OnClosed(e);
    }
}