using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow.Prompts;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistServiceRegistration
{
    public static IServiceCollection AddMohistServerCore(this IServiceCollection services, IConfiguration configuration)
    {
        return services.ConfigureMohistServices(configuration);
    }

    /// <summary>
    /// Registers the full Mohist service graph on the given
    /// <see cref="IServiceCollection"/>. Production code calls this via
    /// <see cref="AddMohistServerCore"/>; test fixtures (e.g.
    /// <c>MohistDbFixture</c>) call it directly to mirror the production
    /// service registration without spinning up a <c>WebApplicationFactory</c>.
    /// </summary>
    /// <remarks>
    /// Any new service the production app needs MUST be added here so the
    /// test fixture picks it up automatically and does not drift from
    /// production.
    /// </remarks>
    public static IServiceCollection ConfigureMohistServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveSqliteConnectionString(configuration);

        services.AddDbContextFactory<MohistDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IStateStore<Mohist.Server.Issue.Domain.Issue>, IssueStore>();
        services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        services.AddScoped<IAgentSessionStore, AgentSessionStore>();
        services.AddScoped<IStateStore<AgentSession>>(sp => sp.GetRequiredService<IAgentSessionStore>());
        services.AddSingleton<ProjectQuerier>();
        services.AddSingleton<ProjectRefResolver>();
        services.AddSingleton<IssueRepositoryResolver>();
        services.AddScoped<IssueIdentityResolver>();
        services.AddScoped<IssueQuerier>();
        services.AddScoped<EpicQuerier>();
        services.AddSingleton<Mohist.Server.Workflow.Services.Prompts.IPromptLoader, Mohist.Server.Workflow.Services.Prompts.FilePromptLoader>();
        services.AddSingleton<PromptTemplateEngine>();
        services.AddScoped<IssueWorkflowProfileRegistry>();
        services.AddScoped<IEventStore, EventStore>();
        services.AddScoped<AgentSessionQuerier>();
        services.AddScoped<WorkflowActivityQuerier>();
        services.AddScoped<WorkflowQuerier>();
        services.AddScoped<WorkflowProfileManager>();
        services.AddScoped<ProjectWorkflowProfileManager>();
        services.AddScoped<IssueWorkflowProfileManager>();
        services.AddSingleton<IWorkflowBacklogDirectory, InMemoryWorkflowBacklogDirectory>();
        services.AddCloudEventBus();
        services.AddCloudEventHandlersFromAssembly(typeof(MohistServiceRegistration).Assembly);
        services.AddSingleton<ConnectionSubscriptionRegistry>();
        services.AddSingleton<IUserNotificationDispatcher, UserNotificationDispatcher>();
        services.AddHostedService<IssueWorkflowReconciliationService>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<RuntimeBuildInfo>();
        services.AddSingleton<IRuntimeBuildInfo>(sp => sp.GetRequiredService<RuntimeBuildInfo>());
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IEnvironmentVariableProvider>(SystemEnvironmentVariableProvider.Instance);
        services.AddSingleton<SystemdInstallDetector>();
        services.AddSingleton<IGitSourceInspector, GitSourceInspector>();
        services.AddSingleton<IServiceStatusChecker, SystemdServiceStatusChecker>();
        services.AddSingleton<SystemInfoService>();
        services.AddSingleton<ISystemUpdateStore, FileSystemSystemUpdateStore>();
        services.AddSingleton<ISystemUpdateCommandRunner, ProcessSystemUpdateCommandRunner>();
        services.AddHttpClient<ISystemReadinessProbe, HttpSystemReadinessProbe>(client =>
        {
            var serverUrl = configuration["Mohist:ServerUrl"]
                ?? SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(ServerUrlEnvironmentVariable)
                ?? "http://127.0.0.1:3456";
            client.BaseAddress = new Uri(serverUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<SystemUpdateService>();
        var runnerRoot = ResolveRunnerRoot(configuration);
        services.AddSingleton<IGitService>(_ => new GitService(runnerRoot));
        services.AddSingleton<RunnerConnectionTracker>();
        services.AddScoped<RunnerStatusService>();
        services.AddSignalR();

        return services;
    }

    public const string ServerUrlEnvironmentVariable = "MOHIST_SERVER_URL";
    public const string DbPathEnvironmentVariable = "MOHIST_DB_PATH";
    public const string HomeEnvironmentVariable = "HOME";
    public const string RunnerRootEnvironmentVariable = "MOHIST_RUNNER_ROOT";
    public const string WorkspaceRootEnvironmentVariable = "MOHIST_WORKSPACE_ROOT";

    public static string ResolveSqliteConnectionString(IConfiguration configuration)
    {
        var configured = configuration["Mohist:SqliteConnectionString"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var dbPath = configuration["Mohist:DbPath"]
            ?? SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(DbPathEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var home = SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(HomeEnvironmentVariable)
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
            ?? SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(RunnerRootEnvironmentVariable)
            ?? SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(WorkspaceRootEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }
}
