using Microsoft.EntityFrameworkCore;
using Mohist.Runner;
using Mohist.Runner.Actions;
using Mohist.Runner.Transport;
using Mohist.Server.Config.Domain;
using Mohist.Server.Events;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Runner.Embedded;
using Mohist.Server.Sessions;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workflow.Projection;
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
        services.AddSingleton<IssueWorkflowProfileRegistry>();
        services.AddSingleton<IWorkflowCompletionHook, IssueWorkflowCompletionHook>();
        services.AddScoped<IEventStore, EventStore>();
        services.AddScoped<AgentSessionService>();
        services.AddScoped<AgentActivityService>();
        services.AddScoped<WorkflowProjectionService>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddScoped<ConfigService>();
        var runnerRoot = ResolveRunnerRoot(configuration);
        services.AddSingleton<IGitService>(_ => new GitService(runnerRoot));
        services.AddSingleton<IAgentExecutor>(sp =>
            new ProcessAgentExecutor(sp.GetRequiredService<ILogger<ProcessAgentExecutor>>()));
        services.AddSingleton<IAgentCompletionVerifier, AgentCompletionVerifier>();
        services.AddSingleton<IAgentSessionRepairer, NoopAgentSessionRepairer>();
        services.AddSingleton<IWorkspaceManager>(sp =>
            new WorkspaceManager(sp.GetRequiredService<ILogger<WorkspaceManager>>(), runnerRoot));
        services.AddScoped<ISessionTelemetrySink, EmbeddedSessionTelemetrySink>();
        services.AddHostedService<EmbeddedRunnerService>();

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

    private static string? ResolveRunnerRoot(IConfiguration configuration)
    {
        var configured = configuration["Mohist:RunnerRoot"]
            ?? Environment.GetEnvironmentVariable("MOHIST_RUNNER_ROOT")
            ?? Environment.GetEnvironmentVariable("MOHIST_WORKSPACE_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }
}
