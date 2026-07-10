using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.ComponentSpecs.Specs.Issue.Profile;
using Orleans.TestingHost;

namespace Mohist.Server.ComponentSpecs.Support;

public static class GrainTestConfig
{
    public static void ConfigureSilo(
        ISiloBuilder siloBuilder,
        string connectionString,
        IEventPublisher eventBus,
        IEventStore eventStore,
        FakeTimeProvider? timeProvider = null)
    {
        siloBuilder.UseInMemoryReminderService();
        DecorateReminderTable(siloBuilder.Services);
        siloBuilder.AddMemoryGrainStorageAsDefault();
        siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options =>
        {
            options.UseSqlite(connectionString);
            // Issue-318 T-002 + T-004: the DbContext model now declares
            // the WorkflowRuns STORED status computed column and the
            // IX_WorkflowRuns_Status index. T-004
            // (20260702060000_WorkflowRunStatus) is the migration that
            // materializes them on disk and applies the historical
            // reclassification. Suppress the pending-changes warning
            // here (test-time only) so a T-002-only build that pre-dates
            // T-004 still migrates cleanly. With T-004 landed, the model
            // matches the snapshot and the warning is never generated,
            // making this Ignore a no-op.
            options.ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning));
        });
        siloBuilder.Services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
        siloBuilder.Services.AddScoped<IAgentSessionStore, AgentSessionStore>();
        siloBuilder.Services.AddScoped<IAgentSessionTranscriptStore, AgentSessionTranscriptStore>();
        siloBuilder.Services.AddScoped<WorkflowRunQuerier>();
        siloBuilder.Services.AddScoped<RunnerDefinitionStore>();
        siloBuilder.Services.AddScoped<RunnerWorkStore>();
        siloBuilder.Services.AddSingleton<ProjectQuerier>();
        siloBuilder.Services.AddSingleton<IPromptLoader>(_ => new FakePromptLoader());
        siloBuilder.Services.AddSingleton<PromptTemplateEngine>();
        siloBuilder.Services.AddSingleton(WorkflowGrainTestHelpers.CreateEmptyConfigService());
        siloBuilder.Services.AddScoped<WorkflowRunProfileManager>();
        siloBuilder.Services.AddScoped<WorkflowProfileManager>();
        siloBuilder.Services.AddScoped<WorkflowItemTranslator>();
        siloBuilder.Services.AddScoped<Mohist.Server.Runner.Services.DispatchService>();
        siloBuilder.Services.AddScoped<Mohist.Server.Runner.Services.WorkflowReportService>();
        siloBuilder.Services.AddScoped<WorkflowSessionHealthService>();
        siloBuilder.Services.AddScoped<IssueWorkflowProfileRegistry>();
        siloBuilder.Services.AddScoped<EffectiveWorkflowProfileResolver>();
        siloBuilder.Services.AddSingleton<FakeRunnerWorkspaceClient>();
        siloBuilder.Services.AddSingleton<IRunnerWorkspaceClient>(provider => provider.GetRequiredService<FakeRunnerWorkspaceClient>());
        siloBuilder.Services.AddSingleton(eventBus);
        siloBuilder.Services.AddSingleton(eventStore);
        siloBuilder.Services.AddSingleton<ITranscriptEventPublisher, NoopTranscriptEventPublisher>();
        siloBuilder.Services.AddSingleton<TimeProvider>(timeProvider ?? TimeProvider.System);
        siloBuilder.Services.AddScoped<IWorkflowArtifactBindService, WorkflowArtifactBindService>();
        siloBuilder.Services.AddScoped<AgentSessionQuery>();
        siloBuilder.Services.Configure<AgentJobOptions>(opts =>
        {
            opts.DispatchBackoffInitial = TimeSpan.FromMilliseconds(50);
            opts.DispatchBackoffCap = TimeSpan.FromMilliseconds(200);
            opts.DispatchRetryBound = TimeSpan.FromSeconds(5);
            opts.JobTimeout = TimeSpan.FromSeconds(10);
        });
        // WorkflowOptions is retained as a binding anchor; the former
        // WorkCompletionTimeout knob has been removed (no server-side
        // work-completion wall clock under the reconciliation model).
        siloBuilder.Services.Configure<WorkflowOptions>(_ => { });
    }

    private static void DecorateReminderTable(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(IReminderTable));
        services.Remove(descriptor);
        services.AddSingleton(provider => new ControllableReminderTable(CreateReminderTable(provider, descriptor)));
        services.AddSingleton<IReminderTable>(provider => provider.GetRequiredService<ControllableReminderTable>());
    }

    private static IReminderTable CreateReminderTable(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IReminderTable instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IReminderTable)descriptor.ImplementationFactory(provider)!;

        return (IReminderTable)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    private sealed class NoopTranscriptEventPublisher : ITranscriptEventPublisher
    {
        public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
