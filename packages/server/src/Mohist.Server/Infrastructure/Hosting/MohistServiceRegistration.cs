using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Querying;
using Mohist.Server.Project.Querying;
using Mohist.Server.Infrastructure.Persistence.Epic;
using Mohist.Server.Infrastructure.Persistence.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Querying;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Querying;
using Mohist.Server.Infrastructure.Persistence.Sessions;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workflow.Projection;
using Mohist.Server.Workflow.Querying;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Runner.Projection;
using Mohist.Server.Runner.SignalR;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Prompts;
using Mohist.Server.Workflow.Prompts.Infrastructure;
using Mohist.Server.Workflow.Prompts.Storage;

namespace Mohist.Server.Infrastructure.Hosting;

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
        services.AddScoped<IStateStore<EpicCounterState>, EpicCounterStore>();
        services.AddScoped<IStateStore<WorkflowBacklogState>, WorkflowBacklogStore>();
        services.AddScoped<IStateStore<WorkflowStageLockState>, WorkflowStageLockStore>();
        services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        services.AddScoped<IStateStore<WorkLease>, WorkflowLeaseStore>();
        services.AddScoped<IStateStore<WorkflowExecutionContext>, WorkflowVariablesStore>();
        services.AddScoped<IStateStore<WorkflowAgentSession>, WorkflowAgentSessionStore>();
        services.AddSingleton<ProjectQuerier>();
        services.AddSingleton<IssueRepositoryResolver>();
        services.AddScoped<IssueIdentityResolver>();
        services.AddScoped<IssueQuerier>();
        services.AddScoped<EpicQuerier>();
        services.AddSingleton<Mohist.Server.Workflow.Prompts.IPromptLoader, Mohist.Server.Workflow.Prompts.FilePromptLoader>();
        services.AddScoped<IProjectTemplateStore, ProjectTemplateStore>();
        services.AddSingleton<PromptTemplateEngine>();
        services.AddScoped<IssueWorkflowProfileRegistry>();
        services.AddSingleton<IWorkflowCompletionHook, IssueWorkflowCompletionHook>();
        services.AddScoped<IEventStore, EventStore>();
        services.AddScoped<WorkflowAgentSessionQuerier>();
        services.AddScoped<WorkflowProjectionService>();
        services.AddScoped<WorkflowQuerier>();
        services.AddScoped<WorkflowProfileManager>();
        services.AddScoped<ProjectWorkflowProfileManager>();
        services.AddScoped<IssueWorkflowProfileManager>();
        services.AddSingleton<IWorkflowBacklogDirectory, InMemoryWorkflowBacklogDirectory>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddHostedService<EventBridge>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<RuntimeBuildInfo>();
        services.AddSingleton<IRuntimeBuildInfo>(sp => sp.GetRequiredService<RuntimeBuildInfo>());
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<SystemdInstallDetector>();
        services.AddSingleton<IGitSourceInspector, GitSourceInspector>();
        services.AddSingleton<IServiceStatusChecker, SystemdServiceStatusChecker>();
        services.AddSingleton<SystemInfoService>();
        services.AddSingleton<ISystemUpdateStore, FileSystemSystemUpdateStore>();
        services.AddSingleton<ISystemUpdateCommandRunner, ProcessSystemUpdateCommandRunner>();
        services.AddHttpClient<ISystemReadinessProbe, HttpSystemReadinessProbe>(client =>
        {
            var serverUrl = configuration["Mohist:ServerUrl"]
                ?? Environment.GetEnvironmentVariable("MOHIST_SERVER_URL")
                ?? "http://127.0.0.1:3456";
            client.BaseAddress = new Uri(serverUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<SystemUpdateService>();
        var runnerRoot = ResolveRunnerRoot(configuration);
        services.AddSingleton<IGitService>(_ => new GitService(runnerRoot));
        services.AddSingleton<RunnerConnectionTracker>();
        services.AddScoped<RunnerStatusProjectionService>();
        services.AddSignalR();

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
