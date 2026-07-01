using LocalRealtimeChat.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LocalRealtimeChat.Api.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(message => message.Id);

            entity.Property(message => message.Username)
                .IsRequired()
                .HasMaxLength(80);

            entity.Property(message => message.Content)
                .IsRequired()
                .HasMaxLength(2000);

            entity.Property(message => message.SentAt)
                .IsRequired();
        });
    }
}