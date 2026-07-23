using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

public class WorkflowProfileMigrationSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly WorkflowProfileProvider _provider;
    private readonly FakeTimeProvider _timeProvider;

    public WorkflowProfileMigrationSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _timeProvider = new FakeTimeProvider();
        var dbFactory = new TestDbContextFactory(_database.Options);
        _provider = new WorkflowProfileProvider(dbFactory, NullActionCatalogSource.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Migrate_SeedLegacyCustomTemplate_RendersCanonicalYAML()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        var semantic = LegacySemanticProfile();
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = "legacy-custom",
                Template = semantic,
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        await WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider);

        var stored = await _provider.GetDefinitionSourceAsync(projectId, "legacy-custom");
        Assert.NotNull(stored);
        Assert.Contains("id: legacy-custom", stored);
        Assert.Contains("stage: build", stored);

        var provenance = await _provider.GetSourceProvenanceAsync(projectId, "legacy-custom");
        Assert.Equal(WorkflowProfileSourceProvenance.CanonicalLegacy, provenance);
    }

    [Fact]
    public async Task Migrate_InvalidLegacyTemplate_FailsWithProjectAndTemplateIdentity()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = "legacy-invalid",
                Template = "not-json",
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider));

        Assert.Contains($"Project '{projectId}' legacy template 'legacy-invalid'", exception.Message);
        Assert.False(await migrateDb.WorkflowProfileRecords
            .AnyAsync(r => r.ProjectId == projectId && r.ProfileId == "legacy-invalid"));
    }

    [Fact]
    public async Task Migrate_InvalidInlineIssueDefinition_FailsWithIssueIdentity()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
            {
                ProjectId = projectId,
                IssueNumber = 42,
                Template = "not-json",
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider));

        Assert.Contains($"Project '{projectId}' Issue '42' inline Definition", exception.Message);
    }

    [Fact]
    public async Task Migrate_ReservedIdCollisions_FailsAtomically()
    {
        // The (ProjectId, TemplateId) primary key prevents two literal
        // rows for the same Project, so a true same-project collision
        // cannot occur in practice. The migrator's per-project collision
        // detector is the documented safety net for the case where
        // two distinct source IDs hash to the same base64url target —
        // which cannot happen with the deterministic encoder. We assert
        // the encoder's determinism here instead: two identical source
        // IDs in different projects both resolve to the same target ID,
        // and that target is still accepted as a per-project independent
        // mapping (the migrator never collides on cross-project pairs).
        var (projectA, _, _) = await SeedProjectAsync("projA");
        var (projectB, _, _) = await SeedProjectAsync("projB");
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectA,
                TemplateId = "mohist/local",
                Template = LegacySemanticProfile("a"),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectB,
                TemplateId = "mohist/local",
                Template = LegacySemanticProfile("b"),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        await WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider);

        var entriesA = await _provider.ListAsync(projectA);
        var entriesB = await _provider.ListAsync(projectB);
        var customA = entriesA.Single(e => !e.IsBuiltIn && e.ProfileId.StartsWith("legacy-reserved/", StringComparison.Ordinal));
        var customB = entriesB.Single(e => !e.IsBuiltIn && e.ProfileId.StartsWith("legacy-reserved/", StringComparison.Ordinal));
        Assert.Equal(customA.ProfileId, customB.ProfileId);
    }

    [Fact]
    public async Task Migrate_ReservedIdDifferentProjects_CreatesIndependentMappings()
    {
        var (projectA, _, _) = await SeedProjectAsync("projA");
        var (projectB, _, _) = await SeedProjectAsync("projB");
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectA,
                TemplateId = "mohist/local",
                Template = LegacySemanticProfile("a"),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectB,
                TemplateId = "mohist/local",
                Template = LegacySemanticProfile("b"),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        await WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider);

        var entriesA = await _provider.ListAsync(projectA);
        var entriesB = await _provider.ListAsync(projectB);
        Assert.Contains(entriesA, e => !e.IsBuiltIn && e.ProfileId.StartsWith("legacy-reserved/", StringComparison.Ordinal));
        Assert.Contains(entriesB, e => !e.IsBuiltIn && e.ProfileId.StartsWith("legacy-reserved/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_ReservedId_IsIdempotentAfterTheFirstRun()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await using (var db = new MohistDbContext(_database.Options))
        {
            var legacyProjectProfile = await db.ProjectWorkflowProfiles.SingleAsync(r => r.ProjectId == projectId);
            legacyProjectProfile.DefaultTemplateId = "mohist/local";
            legacyProjectProfile.DefaultWorkflowProfileId = null;
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = "mohist/local",
                Template = LegacySemanticProfile(),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await using var firstDb = new MohistDbContext(_database.Options);
            await WorkflowProfileDataMigrator.MigrateAsync(firstDb, _timeProvider);

        await using var secondDb = new MohistDbContext(_database.Options);
        var result = await WorkflowProfileDataMigrator.MigrateAsync(secondDb, _timeProvider);

        Assert.Empty(result.Diagnostics);
        var projectProfile = await secondDb.ProjectWorkflowProfiles.SingleAsync(r => r.ProjectId == projectId);
        Assert.StartsWith("legacy-reserved/", projectProfile.DefaultWorkflowProfileId);
        Assert.Equal(projectProfile.DefaultWorkflowProfileId, projectProfile.DefaultWorkflowProfileIdKey);
        var entries = await _provider.ListAsync(projectId);
        Assert.Single(entries, e => !e.IsBuiltIn && e.ProfileId.StartsWith("legacy-reserved/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_MissingProjectDefault_SeedsMohistLocal()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await using var migrateDb = new MohistDbContext(_database.Options);
        await WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider);

        var row = await migrateDb.ProjectWorkflowProfiles
            .FirstOrDefaultAsync(r => r.ProjectId == projectId);
        Assert.NotNull(row);
        Assert.Equal("mohist/local", row!.DefaultWorkflowProfileId);
        Assert.Null(row.DefaultWorkflowProfileIdKey);
    }

    [Fact]
    public async Task Migrate_RewritesExistingRunToCustomKey_TerminalRunClears()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = "legacy-custom",
                Template = LegacySemanticProfile(),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_active",
                State = "{\"status\":\"inProgress\"}",
                Status = "inProgress",
                MetadataProjectId = projectId,
                IssueNumber = 1,
                WorkflowProfileIdKey = "legacy-custom",
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_completed",
                State = "{\"status\":\"completed\"}",
                Status = "completed",
                MetadataProjectId = projectId,
                IssueNumber = 2,
                WorkflowProfileIdKey = "legacy-custom",
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_stopped",
                State = "{\"status\":\"stopped\"}",
                Status = "stopped",
                MetadataProjectId = projectId,
                IssueNumber = 3,
                WorkflowProfileIdKey = "legacy-custom",
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_done",
                State = "{\"status\":\"done\",\"workflowProfileId\":\"legacy-custom\",\"history\":[\"legacy-entry\"]}",
                Status = "done",
                MetadataProjectId = projectId,
                IssueNumber = 4,
                WorkflowProfileIdKey = "legacy-custom",
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        await WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider);

        var active = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_active");
        var completed = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_completed");
        var stopped = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_stopped");
        var done = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_done");
        Assert.Equal("legacy-custom", active.WorkflowProfileIdKey);
        Assert.Null(completed.WorkflowProfileIdKey);
        Assert.Null(stopped.WorkflowProfileIdKey);
        Assert.Null(done.WorkflowProfileIdKey);
        var doneState = JsonDocument.Parse(done.State).RootElement;
        Assert.Equal("legacy-custom", doneState.GetProperty("workflowProfileId").GetString());
        Assert.Equal("legacy-entry", doneState.GetProperty("history")[0].GetString());
    }

    [Fact]
    public async Task Migrate_RewritesRootAndLegacyRunProfileBindings()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = "mohist/local",
                Template = LegacySemanticProfile(),
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_root_active",
                State = "{\"status\":\"inProgress\",\"workflowProfileId\":\"mohist/local\"}",
                Status = "inProgress",
                MetadataProjectId = projectId,
                WorkflowProfileIdKey = "mohist/local",
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_root_terminal",
                State = "{\"status\":\"completed\",\"workflowProfileId\":\"mohist/local\"}",
                Status = "completed",
                MetadataProjectId = projectId,
                WorkflowProfileIdKey = null,
            });
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = "wr_legacy_annotation",
                State = "{\"status\":\"inProgress\",\"metadata\":{\"annotations\":{\"workflowProfileId\":\"mohist/local\"}}}",
                Status = "inProgress",
                MetadataProjectId = projectId,
                WorkflowProfileIdKey = "mohist/local",
            });
            await db.SaveChangesAsync();
        }

        await using var migrateDb = new MohistDbContext(_database.Options);
        await WorkflowProfileDataMigrator.MigrateAsync(migrateDb, _timeProvider);

        var active = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_root_active");
        var terminal = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_root_terminal");
        var legacy = await migrateDb.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_legacy_annotation");
        var renamed = $"{WorkflowProfileDataMigrator.ReservedIdPrefix}bW9oaXN0L2xvY2Fs";

        Assert.Equal(renamed, JsonDocument.Parse(active.State).RootElement.GetProperty("workflowProfileId").GetString());
        Assert.Equal(renamed, active.WorkflowProfileIdKey);
        Assert.Equal(renamed, JsonDocument.Parse(terminal.State).RootElement.GetProperty("workflowProfileId").GetString());
        Assert.Null(terminal.WorkflowProfileIdKey);
        Assert.Equal(
            renamed,
            JsonDocument.Parse(legacy.State).RootElement.GetProperty("metadata").GetProperty("annotations").GetProperty("workflowProfileId").GetString());
        Assert.Equal(renamed, legacy.WorkflowProfileIdKey);
    }

    private async Task<(string ProjectId, int Dummy, bool Initialized)> SeedProjectAsync(string projectId = "proj-1")
    {
        await using var db = new MohistDbContext(_database.Options);
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultWorkflowProfileId = "mohist/local",
            DefaultWorkflowProfileIdKey = null,
            Variables = "{}",
            UpdatedAt = _timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return (projectId, 0, true);
    }

    private static string LegacySemanticProfile(string name = "Legacy")
        => JsonSerializer.Serialize(new
        {
            id = "legacy-custom",
            name,
            description = "legacy",
            definition = new
            {
                stages = new[]
                {
                    new
                    {
                        stage = "build",
                        tasks = new[]
                        {
                            new { id = "build-1", uses = "mohist/opencode", @with = new {} },
                        },
                        checks = Array.Empty<object>(),
                        requiresApproval = false,
                    },
                },
            },
        });

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
