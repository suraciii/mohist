using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config;
using Mohist.Server.Events;
using Mohist.Server.Project.Queries;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Queries;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Storage;
using Mohist.Server.Storage.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workflow.Projection;
using Mohist.Server.Workflow.Queries;
using Mohist.Server.Workflow.Recovery;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workspace;

namespace Mohist.Server.Hosting;

public static class MohistServiceRegistration
{
    public static IServiceCollection AddMohistServerCore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveSqliteConnectionString(configuration);

        services.AddDbContextFactory<MohistDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IStateStore<Mohist.Server.Issue.Domain.Issue>, IssueStore>();
        services.AddScoped<IStateStore<IssueWorkflowProfile>, IssueProfileStore>();
        services.AddScoped<IStateStore<IssueCounterState>, IssueCounterStore>();
        services.AddScoped<IStateStore<WorkflowBacklogState>, WorkflowBacklogStore>();
        services.AddScoped<IStateStore<WorkflowRunProfile>, WorkflowRunProfileStore>();
        services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        services.AddScoped<IStateStore<WorkLease>, WorkflowLeaseStore>();
        services.AddScoped<IStateStore<WorkflowExecutionContext>, WorkflowVariablesStore>();
        services.AddScoped<IStateStore<WorkflowAgentSession>, WorkflowAgentSessionStore>();
        services.AddSingleton<ProjectQueryService>();
        services.AddScoped<IssueQueryService>();
        services.AddSingleton<Workflow.Prompts.IPromptLoader, Workflow.Prompts.FilePromptLoader>();
        services.AddSingleton<IssueWorkflowProfileRegistry>();
        services.AddSingleton<IWorkflowCompletionHook, IssueWorkflowCompletionHook>();
        services.AddScoped<IEventStore, EventStore>();
        services.AddScoped<WorkflowAgentSessionQueryService>();
        services.AddScoped<WorkflowProjectionService>();
        services.AddScoped<WorkflowQueryService>();
        services.AddHostedService<WorkflowBacklogRecoveryService>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<ConfigService>();
        var runnerRoot = ResolveRunnerRoot(configuration);
        services.AddSingleton<IGitService>(_ => new GitService(runnerRoot));

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
