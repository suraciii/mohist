using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow;

public class WorkflowProfileCollectionSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly WorkflowProfileProvider _provider;
    private readonly WorkflowProfileDeletionBlockerQuery _blockerQuery;
    private readonly FakeTimeProvider _timeProvider;

    public WorkflowProfileCollectionSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _timeProvider = new FakeTimeProvider();
        var dbFactory = new TestDbContextFactory(_database.Options);
        _provider = new WorkflowProfileProvider(dbFactory, NullActionCatalogSource.Instance);
        _blockerQuery = new WorkflowProfileDeletionBlockerQuery(dbFactory);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static WorkflowProfileCollectionEntry BuildCustom(
        string profileId,
        string name = "Custom",
        string yaml = """
            id: dummy
            stages:
              - stage: build
                tasks:
                  - id: t
                    uses: mohist/opencode
                    with: {}
                checks: []
            """)
        => new(
            ProjectId: string.Empty,
            ProfileId: profileId,
            Name: name,
            Description: string.Empty,
            SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
            IsBuiltIn: false,
            DefinitionSource: yaml);

    [Fact]
    public async Task List_MergesBuiltInAndCustomProfiles()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));

        var entries = await _provider.ListAsync(projectId);

        Assert.Contains(entries, e => e.IsBuiltIn && e.ProfileId == "mohist/local");
        Assert.Contains(entries, e => e.IsBuiltIn && e.ProfileId == "mohist/github-pr");
        Assert.Contains(entries, e => !e.IsBuiltIn && e.ProfileId == "delivery/review");
    }

    [Fact]
    public async Task BuiltInProfile_ReturnsCanonicalDefinitionSource()
    {
        var (projectId, _, _) = await SeedProjectAsync();

        var source = await _provider.GetDefinitionSourceAsync(projectId, WorkflowProfileCatalog.LocalId);

        Assert.NotNull(source);
        Assert.Contains("id: mohist/local", source);
        Assert.Contains("stages:", source);
    }

    [Fact]
    public async Task List_SameIdInDifferentProjects_ResolvesIndependently()
    {
        var (projectA, _, _) = await SeedProjectAsync("projA");
        var (projectB, _, _) = await SeedProjectAsync("projB");

        await _provider.CreateAsync(projectA, BuildCustom("shared"));
        await _provider.CreateAsync(projectB, BuildCustom("shared"));

        var entriesA = await _provider.ListAsync(projectA);
        var entriesB = await _provider.ListAsync(projectB);

        var customA = entriesA.Single(e => !e.IsBuiltIn && e.ProfileId == "shared");
        var customB = entriesB.Single(e => !e.IsBuiltIn && e.ProfileId == "shared");

        Assert.Equal(projectA, customA.ProjectId);
        Assert.Equal(projectB, customB.ProjectId);
    }

    [Fact]
    public async Task Create_BuiltInId_RejectedWithReadOnlyException()
    {
        var (projectId, _, _) = await SeedProjectAsync();

        await Assert.ThrowsAsync<WorkflowProfileReadOnlyException>(() =>
            _provider.CreateAsync(projectId, BuildCustom("mohist/local")));
    }

    [Fact]
    public async Task Update_BuiltInId_RejectedWithReadOnlyException()
    {
        var (projectId, _, _) = await SeedProjectAsync();

        await Assert.ThrowsAsync<WorkflowProfileReadOnlyException>(() =>
            _provider.UpdateAsync(projectId, BuildCustom("mohist/github-pr")));
    }

    [Fact]
    public async Task Delete_BuiltInId_RejectedWithReadOnlyException()
    {
        var (projectId, _, _) = await SeedProjectAsync();

        await Assert.ThrowsAsync<WorkflowProfileReadOnlyException>(() =>
            _provider.DeleteAsync(projectId, "mohist/local"));
    }

    [Fact]
    public async Task Create_InvalidDefinition_BlockedWithoutPersisting()
    {
        var (projectId, _, _) = await SeedProjectAsync();

        var yaml = """
            stages:
              - tasks:
                  - id: bad
                    uses: mohist/opencode
                    with: {}
                checks: []
            """;
        var exception = await Assert.ThrowsAsync<WorkflowDefinitionValidationException>(() =>
            _provider.CreateAsync(projectId, BuildCustom("bad", yaml: yaml)));

        Assert.NotEmpty(exception.Errors);
        Assert.Contains(exception.Errors, e => e.Source == ValidationSource.Definition);

        await using var db = new MohistDbContext(_database.Options);
        Assert.False(await db.WorkflowProfileRecords
            .AnyAsync(r => r.ProjectId == projectId && r.ProfileId == "bad"));
    }

    [Fact]
    public async Task Create_ActionContractViolation_ReportsActionValidationError()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        var provider = new WorkflowProfileProvider(
            new TestDbContextFactory(_database.Options),
            new StubActionCatalogSource(SimpleCatalog()));

        var result = await provider.CreateAsync(projectId, BuildCustom("bad-action", yaml: """
            id: bad-action
            stages:
              - stage: build
                tasks:
                  - id: t
                    uses: mohist/ghost
                    with: {}
                checks: []
            """));

        Assert.True(result.ValidationResult.HasActionErrors);
        Assert.Equal(ValidationSource.Action, result.ValidationResult.ActionErrors[0].Source);
        Assert.Contains("mohist/ghost", result.ValidationResult.ActionErrors[0].Message);

        await using var db = new MohistDbContext(_database.Options);
        Assert.False(await db.WorkflowProfileRecords
            .AnyAsync(r => r.ProjectId == projectId && r.ProfileId == "bad-action"));
    }

    [Fact]
    public async Task Create_VerbatimSource_PreservedOnSaveAndRead()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        var yaml = """
            id: verbatim-test
            stages:
              - stage: build
                tasks:
                  - id: t
                    uses: mohist/opencode
                    with: {}
                checks: []
            """;

        var result = await _provider.CreateAsync(projectId, BuildCustom("verbatim-test", yaml: yaml));

        Assert.Equal(WorkflowProfileSourceProvenance.Verbatim, result.Profile.SourceProvenance);
        Assert.Equal(yaml, result.Profile.DefinitionSource);

        var stored = await _provider.GetDefinitionSourceAsync(projectId, "verbatim-test");
        Assert.Equal(yaml, stored);
    }

    [Fact]
    public async Task Update_PreservesVerbatimSource()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        var original = """
            id: upd
            stages:
              - stage: build
                tasks:
                  - id: t
                    uses: mohist/opencode
                    with: {}
                checks: []
            """;
        await _provider.CreateAsync(projectId, BuildCustom("upd", yaml: original));

        var updated = """
            id: upd
            stages:
              - stage: build
                tasks:
                  - id: t2
                    uses: mohist/opencode
                    with: {}
                checks: []
            """;
        var result = await _provider.UpdateAsync(projectId, BuildCustom("upd", yaml: updated));

        Assert.Equal(WorkflowProfileSourceProvenance.Verbatim, result.Profile.SourceProvenance);
        Assert.Equal(updated, result.Profile.DefinitionSource);
    }

    [Fact]
    public async Task Delete_ProjectDefault_RejectedWithBlocker()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SetProjectDefaultAsync(projectId, "delivery/review");

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");
        Assert.True(blockers.ProjectDefault);
        Assert.True(blockers.HasAnyBlocker);
    }

    [Fact]
    public async Task Delete_IssueSelection_RejectedWithBlocker()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedIssueAsync(projectId, 1, "delivery/review");

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");

        Assert.Single(blockers.IssueSelections);
        Assert.Equal(1, blockers.IssueSelections[0].IssueNumber);
    }

    [Fact]
    public async Task Delete_TerminalIssue_StillBlocks()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedIssueAsync(projectId, 1, "delivery/review", status: "done");

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");

        Assert.True(blockers.HasAnyBlocker);
        Assert.Single(blockers.IssueSelections);
        Assert.Equal("done", blockers.IssueSelections[0].Status);
    }

    [Fact]
    public async Task Delete_ActiveRun_RejectedWithBlocker()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedWorkflowRunAsync(projectId, "wr_active", "delivery/review", status: "inProgress");

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");

        Assert.Single(blockers.ActiveRuns);
        Assert.Equal("wr_active", blockers.ActiveRuns[0].WorkflowRunId);
    }

    [Fact]
    public async Task Delete_ActiveRunWithMissingBackingKey_StillReportsBlocker()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedWorkflowRunAsync(projectId, "wr_unbound", "delivery/review", status: "inProgress");

        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_unbound");
            row.WorkflowProfileIdKey = null;
            await db.SaveChangesAsync();
        }

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");

        Assert.Contains(blockers.ActiveRuns, run => run.WorkflowRunId == "wr_unbound");
    }

    [Fact]
    public async Task Delete_MultipleActiveRuns_ReportsEveryRun()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedWorkflowRunAsync(projectId, "wr_active_a", "delivery/review", status: "inProgress");
        await SeedWorkflowRunAsync(projectId, "wr_active_b", "delivery/review", status: "paused");

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");

        Assert.Equal(
            ["wr_active_a", "wr_active_b"],
            blockers.ActiveRuns.Select(run => run.WorkflowRunId).OrderBy(id => id).ToArray());
        Assert.Equal(
            ["inprogress", "paused"],
            blockers.ActiveRuns.Select(run => run.Status).OrderBy(status => status).ToArray());
    }

    [Fact]
    public async Task Delete_UnreferencedCustom_RemovesRow()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));

        var deleted = await _provider.DeleteAsync(projectId, "delivery/review");

        Assert.True(deleted);
        await using var db = new MohistDbContext(_database.Options);
        Assert.False(await db.WorkflowProfileRecords
            .AnyAsync(r => r.ProjectId == projectId && r.ProfileId == "delivery/review"));
    }

    [Fact]
    public async Task Delete_TerminalRunOnly_AllowsDeletion()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedWorkflowRunAsync(projectId, "wr_done", "delivery/review", status: "done");

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");
        Assert.False(blockers.HasAnyBlocker);
        Assert.Empty(blockers.ActiveRuns);

        var deleted = await _provider.DeleteAsync(projectId, "delivery/review");
        Assert.True(deleted);
    }

    [Fact]
    public async Task TerminalRun_LosesBackingKey_RetainsPublicProfileId()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedWorkflowRunAsync(projectId, "wr_active", "delivery/review", status: "inProgress");

        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_active");
            row.Status = "done";
            row.State = row.State.Replace("\"status\":\"inProgress\"", "\"status\":\"done\"", StringComparison.Ordinal);
            row.WorkflowProfileIdKey = null;
            await db.SaveChangesAsync();
        }

        var blockers = await _blockerQuery.GetBlockersAsync(projectId, "delivery/review");
        Assert.Empty(blockers.ActiveRuns);

        var deleted = await _provider.DeleteAsync(projectId, "delivery/review");
        Assert.True(deleted);

        await using var verify = new MohistDbContext(_database.Options);
        var run = await verify.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_active");
        Assert.Null(run.WorkflowProfileIdKey);
    }

    [Fact]
    public async Task BindingReplay_AfterTerminalization_DoesNotRestoreBackingKey()
    {
        var (projectId, _, _) = await SeedProjectAsync();
        await _provider.CreateAsync(projectId, BuildCustom("delivery/review"));
        await SeedWorkflowRunAsync(projectId, "wr_replayed", "delivery/review", status: "inProgress");

        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_replayed");
            row.Status = "done";
            row.State = row.State.Replace("\"status\":\"inProgress\"", "\"status\":\"done\"", StringComparison.Ordinal);
            row.WorkflowProfileIdKey = null;
            await db.SaveChangesAsync();
        }

        var participant = new WorkflowRunBindingParticipant(new TestDbContextFactory(_database.Options));
        var outcome = await participant.BindAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(projectId, "wr_replayed", "delivery/review"),
            "replay-command",
            expectedRevision: null);

        Assert.Equal(WorkflowRunBindingOutcome.AlreadyApplied, outcome);

        await using var verify = new MohistDbContext(_database.Options);
        var run = await verify.WorkflowRuns.SingleAsync(r => r.WorkflowRunId == "wr_replayed");
        Assert.Equal("done", run.Status);
        Assert.Contains("delivery/review", run.State);
        Assert.Null(run.WorkflowProfileIdKey);
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

    private async Task SetProjectDefaultAsync(string projectId, string profileId)
    {
        await using var db = new MohistDbContext(_database.Options);
        var row = await db.ProjectWorkflowProfiles.FirstAsync(r => r.ProjectId == projectId);
        row.DefaultWorkflowProfileId = profileId;
        row.DefaultWorkflowProfileIdKey = WorkflowProfileCatalog.IsSystemProfile(profileId)
            ? null
            : profileId;
        await db.SaveChangesAsync();
    }

    private async Task SeedIssueAsync(string projectId, int issueNumber, string? selectedProfile, string status = "backlog")
    {
        await using var db = new MohistDbContext(_database.Options);
        var state = JsonSerializer.Serialize(new
        {
            projectId,
            number = issueNumber,
            status,
            workflowProfileId = selectedProfile,
        });
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            Status = status,
            State = state,
            WorkflowProfileIdKey = selectedProfile is null
                ? null
                : WorkflowProfileCatalog.IsSystemProfile(selectedProfile)
                    ? null
                    : selectedProfile,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWorkflowRunAsync(string projectId, string runId, string selectedProfile, string status)
    {
        await using var db = new MohistDbContext(_database.Options);
        var state = JsonSerializer.Serialize(new
        {
            status,
            metadata = new { annotations = new { projectId, workflowProfileId = (string?)selectedProfile } },
        });
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = state,
            Status = status,
            MetadataProjectId = projectId,
            IssueNumber = 1,
            WorkflowProfileIdKey = status is "done" or "failed" or "cancelled"
                ? null
                : WorkflowProfileCatalog.IsSystemProfile(selectedProfile)
                ? null
                : selectedProfile,
        });
        await db.SaveChangesAsync();
    }

    private static ActionCatalog SimpleCatalog() =>
        new([new ActionCatalogEntry("mohist/opencode", [], [], [])], []);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
