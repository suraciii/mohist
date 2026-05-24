using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config.Domain;
using Mohist.Server.Events;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workspace;

namespace Mohist.Server.Hosting;

public static class MohistServiceRegistration
{
    public static IServiceCollection AddMohistServerCore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveSqliteConnectionString(configuration);

        services.AddDbContextFactory<MohistDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped(typeof(IStateStore<>), typeof(EfStateStore<>));
        services.AddScoped<IssueQueryService>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddScoped<ConfigService>();
        services.AddSingleton<IGitService, GitService>();

        return services;
    }

    public static string ResolveSqliteConnectionString(IConfiguration configuration)
    {
        var configured = configuration["Mohist:SqliteConnectionString"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var dbPath = configuration["Mohist:DbPath"]
            ?? Environment.GetEnvironmentVariable("MOHIST_DB_PATH");

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataDir = Path.Combine(home, ".mohist");
            Directory.CreateDirectory(dataDir);
            dbPath = Path.Combine(dataDir, "mohist.db");
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }

        return $"Data Source={dbPath}";
    }
}
