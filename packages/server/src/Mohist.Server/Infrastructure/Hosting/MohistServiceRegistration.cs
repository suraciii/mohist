using System.IO.Compression;
using Microsoft.AspNetCore.RequestDecompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Hosting;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Runner.Services;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Label.Services;
using Mohist.Server.Otel;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Logging;
using Mohist.Server.Notifications;

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
        services.AddMohistConventionalServices();
        services.TryAddSingleton<IBackgroundTaskLauncher, BackgroundTaskLauncher>();

        services.AddRouting(options =>
        {
            options.ConstraintMap["notstaticfile"] = typeof(NotStaticFileConstraint);
        });

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

        // IAgentLauncher is an interface on top of the concrete
        // AgentLauncher (registered by conventional services via
        // IScopedService/AsSelf). Forward-registration so route handlers
        // and bus-side dispatch handlers can depend on the interface
        // without taking on the concrete type. Lifetime matches the
        // concrete type — scoped, like IssueQuerier.
        services.AddScoped<IAgentLauncher>(sp => sp.GetRequiredService<AgentLauncher>());
        services.AddScoped<IAgentExecutionSnapshotResolver>(sp => sp.GetRequiredService<AgentExecutionSnapshotResolver>());
        services.AddScoped<IAgentRuntimeOverrideResolver>(sp =>
            sp.GetRequiredService<Mohist.Server.Workflow.Services.IssueWorkflowProfileManager>());
        services.AddSingleton<IAgentJobWorkCoordinator>(sp => sp.GetRequiredService<AgentJobWorkCoordinator>());
        services.AddSingleton<Mohist.Server.Sessions.Services.IAgentSessionConnectionRegistry>(sp =>
            sp.GetRequiredService<Mohist.Server.Runner.Services.SignalR.RunnerConnectionTracker>());

        var connectionString = ResolveSqliteConnectionString(configuration);

        services.AddMohistOpenTelemetry(configuration);

        services.AddDbContextFactory<MohistDbContext>(options =>
            options.UseSqlite(connectionString)
                .AddInterceptors(new RequestWorkDbCommandInterceptor())
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddTransient<IHttpMessageHandlerBuilderFilter, RequestWorkHttpMessageHandlerBuilderFilter>();

        services.AddScoped<IStateStore<Mohist.Server.Issue.Domain.Issue>>(sp => sp.GetRequiredService<IIssueStore>());
        services.AddScoped<IIssueStore, IssueStore>();
        services.AddScoped<IStateStore<Mohist.Server.Agent.Domain.Agent>, AgentStore>();
        services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        services.AddScoped<WorkflowRunQuerier>();
        services.AddScoped<IAgentSessionStore, AgentSessionStore>();
        services.AddScoped<AgentSessionReconcileQuerier>();
        services.AddScoped<IStateStore<AgentSession>>(sp => sp.GetRequiredService<IAgentSessionStore>());
        services.AddScoped<IAgentSessionTranscriptStore, AgentSessionTranscriptStore>();
        services.AddScoped<IAgentJobStore, AgentJobStore>();
        services.AddSingleton<Mohist.Server.Workflow.Services.Prompts.IPromptLoader, Mohist.Server.Workflow.Services.Prompts.FilePromptLoader>();
        services.AddSingleton<IEventStore, EventStore>();
        services.TryAddSingleton<IDeadLetterStore, DeadLetterStore>();
        services.AddSingleton<EventDispatcherService>();
        services.Configure<EventDispatcherOptions>(configuration.GetSection(EventDispatcherOptions.SectionName));
        services.Configure<HermesNotificationOptions>(configuration.GetSection(HermesNotificationOptions.SectionName));
        services.AddSingleton<HermesIssueNotificationRenderer>();
        services.AddSingleton<IHermesIssueNotificationDispatcher, BackgroundHermesIssueNotificationDispatcher>();
        services.AddHttpClient<IHermesWebhookClient, HermesWebhookClient>();
        services.AddCloudEventBus();
        services.AddCloudEventHandlersFromAssembly(typeof(MohistServiceRegistration).Assembly);
        services.AddSingleton<IEventTailSource>(sp => sp.GetRequiredService<Mohist.Server.Events.Hub.EventTailSource>());
        services.AddSingleton<IUserNotificationDispatcher, UserNotificationDispatcher>();
        services.AddSingleton<ITranscriptEventPublisher, SignalRTranscriptEventPublisher>();
        services.AddSingleton<ITaskLogDeltaPublisher, SignalRTaskLogDeltaPublisher>();
        services.AddHostedService<AttachmentCleanupService>();
        services.AddHostedService<DispatcherActivationService>();
        services.TryAddSingleton<IProcessStartTimeProvider, ProcessStartTimeProvider>();
        services.AddHostedService<SystemUpdateRecoveryService>();
        services.AddSingleton<IRuntimeBuildInfo>(sp => sp.GetRequiredService<RuntimeBuildInfo>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IRuntimeSourceIdentity, RuntimeSourceIdentity>();
        services.AddSingleton<IEnvironmentVariableProvider>(SystemEnvironmentVariableProvider.Instance);
        services.AddSingleton<IConfigDocumentStore, FileConfigDocumentStore>();
        services.AddSingleton<IWebContentProvider, WebContentProvider>();
        services.AddSingleton<ILogPathResolver, LogPathResolver>();
        services.AddSingleton<ILogTailSource, FileLogTailSource>();
        services.AddSingleton<IGitSourceInspector, GitSourceInspector>();
        services.AddSingleton<IServiceStatusChecker, SystemdServiceStatusChecker>();
        services.AddSingleton<ISystemUpdateStore, FileSystemSystemUpdateStore>();
        services.AddSingleton<IManagedAssetCatalog, FileSystemManagedAssetCatalog>();
        services.AddSingleton<ISystemUpdateCommandRunner, ProcessSystemUpdateCommandRunner>();
        services.AddHttpClient<ISystemReadinessProbe, HttpSystemReadinessProbe>(client =>
        {
            var serverUrl = configuration["Mohist:ServerUrl"]
                ?? SystemEnvironmentVariableProvider.Instance.GetEnvironmentVariable(ServerUrlEnvironmentVariable)
                ?? "http://127.0.0.1:3456";
            client.BaseAddress = new Uri(serverUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<IWorkflowArtifactStorage, FileSystemWorkflowArtifactStorage>();
        services.Configure<WorkflowArtifactStorageOptions>(configuration.GetSection(WorkflowArtifactStorageOptions.SectionName));
        services.AddSingleton<IAttachmentStorage, FileSystemAttachmentStorage>();
        services.Configure<AttachmentStorageOptions>(configuration.GetSection(AttachmentStorageOptions.SectionName));
        services.AddScoped<IWorkflowArtifactBindService, WorkflowArtifactBindService>();
        services.AddScoped<IWorkflowArtifactQuerier, WorkflowArtifactQuerier>();
        services.AddScoped<Mohist.Server.Workflow.Services.IWorkflowProfileProvider, Mohist.Server.Workflow.Services.WorkflowProfileProvider>();
        services.AddScoped<Mohist.Server.Workflow.Services.WorkflowProfileDeletionBlockerQuery>();
        services.Configure<AgentJobOptions>(configuration.GetSection(AgentJobOptions.SectionName));
        services.TryAddSingleton<IAgentJobDispatchObserver>(NoopAgentJobDispatchObserver.Instance);
        services.Configure<WorkflowOptions>(configuration.GetSection(WorkflowOptions.SectionName));
        services.Configure<CleanupPolicyOptions>(configuration.GetSection(CleanupPolicyOptions.SectionName));
        services.Configure<Mohist.Server.Otel.OtelOptions>(configuration.GetSection(Mohist.Server.Otel.OtelOptions.SectionName));
        services.PostConfigure<Mohist.Server.Otel.OtelOptions>(options =>
        {
            if (!string.IsNullOrWhiteSpace(options.DbPath))
                return;

            var mainDbPath = ResolveSqliteDatabasePath(configuration);
            var mainDbDirectory = Path.GetDirectoryName(Path.GetFullPath(mainDbPath));
            if (!string.IsNullOrWhiteSpace(mainDbDirectory))
                options.DbPath = Path.Combine(mainDbDirectory, OtelDb.DefaultDatabaseFileName);
        });
        services.TryAddSingleton<RuntimeEpoch>(sp =>
            RuntimeEpoch.Capture(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<RuntimeObservability>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Mohist.Server.Otel.OtelOptions>>().Value;
            return new RuntimeObservability(
                options.Enabled,
                sp.GetRequiredService<RuntimeEpoch>(),
                sp.GetRequiredService<TimeProvider>(),
                storageBudgetBytes: options.StorageBudgetBytes);
        });
        services.AddSingleton<OtelDb>();
        services.TryAddSingleton<IProcessResourceReader, ProcessResourceReader>();
        services.TryAddSingleton<IOtelStorageProbe, OtelStorageProbe>();
        services.TryAddSingleton<OtelStorageGuard>();
        services.TryAddSingleton<IOtelStorageReclaimer, SqliteOtelStorageReclaimer>();
        services.TryAddSingleton<IOtelDbPool, SqliteOtelDbPool>();
        // Maintenance callbacks run in registration order on every
        // enabled tick. Recovery is registered first so an oversized
        // database is rebuilt on the first tick before retention and
        // storage eviction waste work on a store that is about to be
        // discarded; recovery is a one-shot gate, so on subsequent
        // ticks the effective order is retention (time) -> storage
        // (size + reclaim + arbitrate), matching design D1.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOtelMaintenanceCallback, OtelStorageRecoveryMaintenance>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOtelMaintenanceCallback, OtelRetentionMaintenance>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOtelMaintenanceCallback, OtelStorageMaintenance>());
        services.AddHostedService<OtelDiagnosticsSampler>();
        services.AddSingleton<OtelCollectorStatus>();
        services.AddSingleton<IIngestProtectionDecision, BudgetAwareIngestProtectionDecision>();
        services.AddSingleton<OtlpTraceResponseWriter>();
        services.AddSingleton<TraceIngester>();
        services.AddSingleton<OtlpIngestGate>();
        services.AddSingleton<IOtlpIngestGate>(provider => provider.GetRequiredService<OtlpIngestGate>());
        services.AddSingleton<IOtlpIngestGateTestSeam>(provider => provider.GetRequiredService<OtlpIngestGate>());
        services.AddRequestDecompression();
        services.AddSingleton<TraceQuerier>();
        services.AddSingleton<IOtelQueryExecutor>(provider => provider.GetRequiredService<TraceQuerier>());
        services.AddScoped<IRunnerWorkspaceClient, RunnerWorkspaceClient>();
        services.AddScoped<ISessionCommandDispatcher, RunnerSessionCommandDispatcher>();
        services.AddScoped<IActionCatalogSource>(sp => sp.GetRequiredService<RunnerRegistryCatalogSource>());
        services.AddSingleton<IRunnerWorkflowStatusRouter, RunnerWorkflowStatusRouter>();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            CopyJsonOptions(JSON.Options, o.SerializerOptions);
        });
        services.AddScoped<RunnerDefinitionStore>();
        services.AddScoped<RunnerWorkStore>();
        services.AddScoped<TaskLogService>();
        services.AddScoped<TaskLogStore>();
        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions = JSON.Options;
            });

        return services;
    }

    public const string ServerUrlEnvironmentVariable = "MOHIST_SERVER_URL";
    public const string DbPathEnvironmentVariable = "MOHIST_DB_PATH";
    public const string HomeEnvironmentVariable = "HOME";

    public static string ResolveSqliteConnectionString(IConfiguration configuration)
    {
        var configured = configuration["Mohist:SqliteConnectionString"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var dbPath = ResolveSqliteDatabasePath(configuration);

        return $"Data Source={dbPath}";
    }

    public static string ResolveSqliteDatabasePath(IConfiguration configuration)
    {
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

        return dbPath;
    }

    private static void CopyJsonOptions(System.Text.Json.JsonSerializerOptions source, System.Text.Json.JsonSerializerOptions target)
    {
        target.DefaultIgnoreCondition = source.DefaultIgnoreCondition;
        target.PropertyNameCaseInsensitive = source.PropertyNameCaseInsensitive;
        target.Encoder = source.Encoder;

        target.Converters.Clear();
        foreach (var converter in source.Converters)
        {
            target.Converters.Add(converter);
        }
    }
}
