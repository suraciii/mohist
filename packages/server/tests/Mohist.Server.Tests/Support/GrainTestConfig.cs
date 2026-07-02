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
using Mohist.Server.Tests.Specs.Issue.Profile;
using Orleans.TestingHost;

namespace Mohist.Server.Tests.Support;

public static class GrainTestConfig
{
    public static MohistDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            // Issue-318 T-002 vs T-004: see ConfigureSilo for context. The
            // raw context builder used outside the silo (e.g. in
            // BacklogFixture, MohistDbFixture) needs the same
            // suppression — the production warning would otherwise abort
            // the test before T-004's migration is added.
            .ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MohistDbContext(options);
    }

    public static void MigrateWithSchemaFix(MohistDbContext db)
    {
        // Issue-318 T-002: the model declares the WorkflowRuns STORED
        // status computed column + IX_WorkflowRuns_Status index, but
        // the migration that materializes them is produced in T-004.
        // PendingModelChangesWarning is suppressed at the DbContext
        // registration site (ConfigureSilo / MohistServiceRegistration)
        // so Migrate() here does not throw. The IF NOT EXISTS DDL below
        // ensures the test DB actually has the column / index so the
        // new filtering queries run against a real schema — mirrors
        // the Attachments fix-up pattern in MohistDbFixture. The
        // TRIGGER simulates the STORED computed column's
        // auto-population behavior at the SQL layer so writes through
        // IWorkflowRunStore (which only touch State JSON) leave the
        // column in sync with State.status; without this, persisted
        // status would stay NULL and the new status-filter queries
        // would match nothing.
        db.Database.Migrate();
        ApplyWorkflowRunsStatusSchemaFix(db);
    }

    /// <summary>
    /// Issue-318 T-002: applies the test-only DDL that materializes the
    /// STORED Status computed column and its index on the WorkflowRuns
    /// table, plus the trigger that simulates the column's auto-update
    /// on INSERT/UPDATE. The migration that produces the durable form is
    /// owned by T-004; this helper exists so any test fixture
    /// (MohistIntegrationFixture, BacklogFixture, MohistDbFixture, …) can
    /// get a schema-compatible test DB without going through
    /// MigrateWithSchemaFix. Idempotent — re-runnable across test classes
    /// sharing an in-memory database. See MigrateWithSchemaFix for the
    /// full rationale.
    /// </summary>
    public static void ApplyWorkflowRunsStatusSchemaFix(MohistDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            ALTER TABLE "WorkflowRuns" ADD COLUMN "Status" TEXT NULL;
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowRuns_Status" ON "WorkflowRuns" ("Status", "AssignedRunnerId");
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS "WorkflowRuns_AI_Status"
            AFTER INSERT ON "WorkflowRuns"
            BEGIN
                UPDATE "WorkflowRuns"
                SET "Status" = LOWER(COALESCE(json_extract(NEW."State", '$.status'), json_extract(NEW."State", '$.Status')))
                WHERE "WorkflowRunId" = NEW."WorkflowRunId";
            END;
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS "WorkflowRuns_AU_Status"
            AFTER UPDATE ON "WorkflowRuns"
            BEGIN
                UPDATE "WorkflowRuns"
                SET "Status" = LOWER(COALESCE(json_extract(NEW."State", '$.status'), json_extract(NEW."State", '$.Status')))
                WHERE "WorkflowRunId" = NEW."WorkflowRunId";
            END;
            """);
    }

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
            // Issue-318 T-002: the DbContext model now declares the
            // WorkflowRuns STORED status computed column and the
            // IX_WorkflowRuns_Status index. The migration that
            // materializes them is owned by T-004, so any test that
            // runs against a T-002-only build would otherwise fail at
            // Migrate() on this pending-changes warning. Suppress the
            // warning here (test-time only) and ensure the column /
            // index exist via raw DDL inside MigrateWithSchemaFix. The
            // trigger in MigrateWithSchemaFix stands in for the STORED
            // computed column's auto-population so the new status
            // filters run against a real schema. Once T-004 lands the
            // migration can be applied and this suppression becomes a
            // no-op (no warning because model == snapshot).
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
        siloBuilder.Services.Configure<WorkflowOptions>(opts =>
        {
            opts.WorkCompletionTimeout = TimeSpan.FromMinutes(10);
        });
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
