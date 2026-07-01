using LocalRealtimeChat.Api.Data;
using LocalRealtimeChat.Api.WebSockets;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(8, 0, 0))
    );
});

builder.Services.AddSingleton<ChatWebSocketHandler>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.Map("/", () => "LocalRealtimeChat.Api is running.");

app.Map("/ws/chat", async context =>
{
    var handler = context.RequestServices.GetRequiredService<ChatWebSocketHandler>();
    await handler.HandleAsync(context);
});

app.Run();