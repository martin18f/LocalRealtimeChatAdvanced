using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LocalRealtimeChat.Api.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        string currentDirectory = Directory.GetCurrentDirectory();

        string projectDirectory = Directory.Exists(Path.Combine(currentDirectory, "LocalRealtimeChat.Api"))
            ? Path.Combine(currentDirectory, "LocalRealtimeChat.Api")
            : currentDirectory;

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(projectDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        string connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        optionsBuilder.UseMySql(
            connectionString,
            new MySqlServerVersion(new Version(8, 0, 0))
        );

        return new AppDbContext(optionsBuilder.Options);
    }
}