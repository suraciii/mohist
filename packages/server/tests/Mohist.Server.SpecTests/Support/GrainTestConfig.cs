using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Events.Grains;
using Mohist.Server.Events.Subscriptions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.SpecTests.Specs.Issue.Profile;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Mohist.Server.SpecTests.Support;

public static class GrainTestConfig
{
    public static MohistDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            // Issue-318 T-002 + T-004: see ConfigureSilo for context. The
            // raw context builder used outside the silo (e.g. in
            // BacklogFixture, MohistDbFixture) needs the same
            // suppression — the production warning would otherwise abort
            // the test on a T-002-only build that pre-dates T-004.
            .ConfigureWarnings(w => w.Ignore(
                RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MohistDbContext(options);
    }

    public static void MigrateWithSchemaFix(MohistDbContext db)
    {
        // Issue-318 T-004: the WorkflowRunStatus migration (added in
        // T-004) materializes the STORED Status computed column, the
        // IX_WorkflowRuns_Status index, and the historical
        // reclassification. PendingModelChangesWarning is suppressed at
        // the DbContext registration site
        // (ConfigureSilo / MohistServiceRegistration) so Migrate() here
        // does not throw, even on a T-002-only build that pre-dates the
        // migration. ApplyWorkflowRunsStatusSchemaFix stays idempotent so
        // fixtures that pre-create the schema (e.g. via EnsureCreated)
        // without going through Migrate() still get a working column /
        // index. Once T-004 lands, the model matches the snapshot and
        // the warning suppression becomes a no-op for any test that
        // exercises the full Migrate() path.
        db.Database.Migrate();
        ApplyWorkflowRunsStatusSchemaFix(db);
    }

    /// <summary>
    /// Issue-318 T-002: applies the test-only DDL that materializes the
    /// STORED Status computed column and its index on the WorkflowRuns
    /// table, plus the trigger that simulates the column's auto-update
    /// on INSERT/UPDATE. The migration that produces the durable form
    /// is owned by T-004 (<c>20260702060000_WorkflowRunStatus</c>); this
    /// helper exists so any test fixture (MohistIntegrationFixture,
    /// BacklogFixture, MohistDbFixture, …) that pre-creates the schema
    /// without going through <c>Migrate()</c> (e.g. via
    /// <c>EnsureCreatedAsync</c>) still gets a working column / index.
    /// Idempotent — re-runnable across test classes sharing an in-memory
    /// database. See <c>MigrateWithSchemaFix</c> for the full rationale.
    /// </summary>
    public static void ApplyWorkflowRunsStatusSchemaFix(MohistDbContext db)
    {
        // After T-004 the migration has already added Status as a STORED
        // computed column. The legacy plain-Text + trigger path here
        // only runs against pre-T-004 fixtures (or any test that
        // pre-created the schema without applying the migration). The
        // AddColumn and trigger creates are wrapped in try/catch so a
        // T-004 DB (where the column is STORED and the trigger cannot
        // update a generated column) does not fail — pragma checks via
        // a second connection can return stale results in shared-cache
        // mode, so the DDL itself is the source of truth.
        try
        {
            db.Database.ExecuteSqlRaw("""
                ALTER TABLE "WorkflowRuns" ADD COLUMN "Status" TEXT NULL;
                """);

            // The trigger is only needed for the plain-Text fallback
            // path — a STORED computed column auto-recomputes on
            // INSERT/UPDATE so the trigger would be a no-op there (and
            // SQLite rejects "UPDATE generated column Status" with an
            // error). Install it under the same try so a T-004 DB
            // skips it without a separate guard.
            db.Database.ExecuteSqlRaw("""
                CREATE TRIGGER "WorkflowRuns_AI_Status"
                AFTER INSERT ON "WorkflowRuns"
                BEGIN
                    UPDATE "WorkflowRuns"
                    SET "Status" = LOWER(COALESCE(json_extract(NEW."State", '$.status'), json_extract(NEW."State", '$.Status')))
                    WHERE "WorkflowRunId" = NEW."WorkflowRunId";
                END;
                """);

            db.Database.ExecuteSqlRaw("""
                CREATE TRIGGER "WorkflowRuns_AU_Status"
                AFTER UPDATE ON "WorkflowRuns"
                BEGIN
                    UPDATE "WorkflowRuns"
                    SET "Status" = LOWER(COALESCE(json_extract(NEW."State", '$.status'), json_extract(NEW."State", '$.Status')))
                    WHERE "WorkflowRunId" = NEW."WorkflowRunId";
                END;
                """);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.Ordinal)
            || ex.Message.Contains("generated column", StringComparison.Ordinal))
        {
        }

        // Epic #44: ReadySince VIRTUAL generated column (fairness ordering
        // key). Fixtures that pre-create the schema without the migration
        // (EnsureCreated path, or raw DDL) do not materialize it, so add it
        // here idempotently. VIRTUAL so it is computed on read; the catch
        // swallows the "duplicate column" / "generated column" errors a
        // migration-built DB raises.
        try
        {
            db.Database.ExecuteSqlRaw("""
                ALTER TABLE "WorkflowRuns"
                ADD COLUMN "ReadySince" TEXT NULL
                GENERATED ALWAYS AS (COALESCE(json_extract("State", '$.readySince'), json_extract("State", '$.ReadySince'))) VIRTUAL;
                """);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.Ordinal)
            || ex.Message.Contains("generated column", StringComparison.Ordinal))
        {
        }

        try
        {
            db.Database.ExecuteSqlRaw("""
                ALTER TABLE "WorkflowRuns"
                ADD COLUMN "AssignedWorkerId" TEXT NULL
                GENERATED ALWAYS AS (
                    COALESCE(
                        json_extract("State", '$.assignment.workerId'),
                        json_extract("State", '$.assignment.runnerId'),
                        json_extract("State", '$.claim.runnerId')
                    )
                ) VIRTUAL;
                """);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (
            ex.Message.Contains("duplicate column name", StringComparison.Ordinal)
            || ex.Message.Contains("generated column", StringComparison.Ordinal))
        {
        }

        // The IX_WorkflowRuns_Status index is unconditional and
        // idempotent — CREATE INDEX IF NOT EXISTS skips the second
        // create on a T-004 DB.
        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowRuns_Status" ON "WorkflowRuns" ("Status", "AssignedWorkerId");
            """);

        // Epic #44: fairness covering index for FindAssignedToAsync's
        // ReadySince ASC round-robin. Idempotent; the column itself is
        // declared in the DbContext model so EnsureCreated already
        // materializes it — this only closes the gap for fixtures that
        // pre-created the schema before this index existed.
        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowRuns_Status_ReadySince"
            ON "WorkflowRuns" ("Status", "AssignedWorkerId", "ReadySince");
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
        // Issue-362: the dispatcher grain registers a ~1s reminder; the
        // in-memory reminder service still enforces MinimumReminderPeriod
        // by default, so lower the floor for the test silo as the
        // production silo does.
        siloBuilder.Configure<ReminderOptions>(options =>
        {
            options.MinimumReminderPeriod = TimeSpan.FromMilliseconds(100);
        });
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
        siloBuilder.Services.AddScoped<RepositoryDeletionBlockerQuery>();
        siloBuilder.Services.AddSingleton<IAgentJobWorkCoordinator, AgentJobWorkCoordinator>();
        siloBuilder.Services.AddScoped<IssueWorkflowProfileRegistry>();
        siloBuilder.Services.AddScoped<EffectiveWorkflowProfileResolver>();
        siloBuilder.Services.AddSingleton<FakeRunnerWorkspaceClient>();
        siloBuilder.Services.AddSingleton<IRunnerWorkspaceClient>(provider => provider.GetRequiredService<FakeRunnerWorkspaceClient>());
        siloBuilder.Services.AddSingleton(eventBus);
        siloBuilder.Services.AddSingleton(eventStore);
        siloBuilder.Services.AddSingleton<IDeadLetterStore, NoopDeadLetterStore>();
        siloBuilder.Services.AddSingleton<WorkflowStageLockReleaseHandler>();
        siloBuilder.Services.AddSingleton<IEnumerable<Subscription>>(services =>
        {
            var handler = services.GetRequiredService<WorkflowStageLockReleaseHandler>();
            return
            [
                new Subscription(
                    "com.mohist.workflow.stage.completed|com.mohist.workflow.stage.failed",
                    handler,
                    (instance, envelope, ct) =>
                        ((WorkflowStageLockReleaseHandler)instance).HandleAsync(envelope, ct)),
            ];
        });
        siloBuilder.Services.Configure<EventDispatcherOptions>(options =>
        {
            options.BatchSize = 100;
            options.MaxAttempts = 3;
        });
        siloBuilder.Services.AddSingleton<EventDispatcherService>();
        siloBuilder.Services.AddSingleton<ITranscriptEventPublisher, NoopTranscriptEventPublisher>();
        siloBuilder.Services.AddSingleton<TimeProvider>(timeProvider ?? new FakeTimeProvider(TestTime.UtcNow));
         siloBuilder.Services.AddSingleton<RunnerConnectionTracker>();
         siloBuilder.Services.AddSingleton<IAgentSessionConnectionRegistry>(sp =>
             sp.GetRequiredService<RunnerConnectionTracker>());
        siloBuilder.Services.AddScoped<IWorkflowArtifactBindService, WorkflowArtifactBindService>();
        siloBuilder.Services.AddScoped<AgentSessionQuery>();
        siloBuilder.Services.Configure<AgentJobOptions>(opts =>
        {
            opts.DispatchBackoffInitial = TimeSpan.FromMilliseconds(50);
            opts.DispatchBackoffCap = TimeSpan.FromMilliseconds(200);
            opts.DispatchRetryBound = TimeSpan.FromSeconds(5);
            opts.JobTimeout = TimeSpan.FromSeconds(10);
        });
        siloBuilder.Services.AddSingleton<IAgentJobDispatchObserver>(NoopAgentJobDispatchObserver.Instance);
        // WorkflowOptions is retained as a binding anchor; the former
        // WorkCompletionTimeout knob has been removed (no server-side
        // work-completion wall clock under the reconciliation model).
        siloBuilder.Services.Configure<WorkflowOptions>(_ => { });
    }

    private sealed class NoopTranscriptEventPublisher : ITranscriptEventPublisher
    {
        public Task PublishAsync(TranscriptEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
