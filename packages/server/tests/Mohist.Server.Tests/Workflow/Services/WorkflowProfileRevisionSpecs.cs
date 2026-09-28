using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Services;

/// <summary>
/// Specs for the Profile content revision (issue #1098): the opaque token
/// that gates updates at the provider's storage boundary. Covers coherent
/// reads, stale/missing preconditions, token non-reuse across edits,
/// deletion/recreation, unrelated Project operations, and the removal of
/// the active-run structural guard (runs execute their own binding
/// snapshot, so a future definition may drop or change stages).
/// </summary>
[Trait("level", "L0")]
public class WorkflowProfileRevisionSpecs : IAsyncLifetime
{
    private readonly TestSqliteDatabase _database;
    private readonly WorkflowProfileProvider _provider;
    private readonly FakeTimeProvider _timeProvider;

    public WorkflowProfileRevisionSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _timeProvider = new FakeTimeProvider();
        _provider = new WorkflowProfileProvider(
            new TestDbContextFactory(_database.Options),
            NullActionCatalogSource.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private const string StageBuild = """
        id: {0}
        stages:
          - stage: build
            tasks:
              - id: t
                uses: test/action
                with: {}
            checks: []
        """;

    private const string StageBuildAndShip = """
        id: {0}
        stages:
          - stage: build
            tasks:
              - id: t
                uses: test/action
                with: {}
            checks: []
          - stage: ship
            requiresApproval: true
            tasks:
              - id: s
                uses: test/action
                with: {}
            checks: []
        """;

    private static string Source(string template, string profileId) =>
        template.Replace("{0}", profileId, StringComparison.Ordinal);

    private static WorkflowProfileCollectionEntry Request(
        string profileId,
        string yaml,
        string name = "Custom") =>
        new(
            ProjectId: string.Empty,
            ProfileId: profileId,
            Name: name,
            Description: string.Empty,
            SourceProvenance: WorkflowProfileSourceProvenance.Verbatim,
            IsBuiltIn: false,
            DefinitionSource: yaml);

    [Fact]
    public async Task CreateAndDetailRead_ExposeCoherentContentStructureAndRevision()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "coherent";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));

        Assert.NotNull(created.Profile.Revision);
        Assert.Equal(created.Profile.Revision, (await _provider.GetAsync(projectId, profileId))!.Revision);

        // One detail read: content, derived structure, and revision of the
        // same stored version.
        var detail = (await _provider.GetDetailAsync(projectId, profileId))!;
        Assert.Equal(created.Profile.Revision, detail.Profile.Revision);
        Assert.Equal(Source(StageBuild, profileId), detail.Profile.DefinitionSource);
        Assert.Equal(["build"], detail.Definition.Stages.Select(stage => stage.Stage).ToArray());
    }

    [Fact]
    public async Task GetDetail_BuiltInProfile_AnswersWithoutRevision()
    {
        var projectId = await SeedProjectAsync();

        var detail = (await _provider.GetDetailAsync(projectId, "mohist/local"))!;

        Assert.True(detail.Profile.IsBuiltIn);
        Assert.Null(detail.Profile.Revision);
        Assert.NotEmpty(detail.Definition.Stages);
    }

    [Fact]
    public async Task GetDetail_UnknownProfile_ReturnsNull()
    {
        var projectId = await SeedProjectAsync();

        Assert.Null(await _provider.GetDetailAsync(projectId, "ghost"));
    }

    [Fact]
    public async Task Update_WithReadRevision_ReplacesContentAndIssuesNewRevision()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "rotate";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));
        var firstRevision = created.Profile.Revision;

        var updated = await _provider.UpdateAsync(
            projectId,
            Request(profileId, Source(StageBuildAndShip, profileId)),
            firstRevision!);

        Assert.NotEqual(firstRevision, updated.Profile.Revision);
        var detail = (await _provider.GetDetailAsync(projectId, profileId))!;
        Assert.Equal(updated.Profile.Revision, detail.Profile.Revision);
        Assert.Contains("stage: ship", detail.Profile.DefinitionSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_StaleRevision_ConflictsWithoutPartialEffect()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "stale";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));
        var winner = await _provider.UpdateAsync(
            projectId,
            Request(profileId, Source(StageBuildAndShip, profileId)),
            created.Profile.Revision!);

        var conflict = await Assert.ThrowsAsync<WorkflowProfileRevisionConflictException>(() =>
            _provider.UpdateAsync(
                projectId,
                Request(profileId, Source(StageBuild, profileId), name: "Loser"),
                created.Profile.Revision!));

        // The loser learns the current token and nothing was written.
        Assert.Equal(winner.Profile.Revision, conflict.CurrentRevision);
        var detail = (await _provider.GetDetailAsync(projectId, profileId))!;
        Assert.Contains("stage: ship", detail.Profile.DefinitionSource, StringComparison.Ordinal);
        Assert.Equal("Custom", detail.Profile.Name);
        Assert.Equal(winner.Profile.Revision, detail.Profile.Revision);
    }

    [Fact]
    public async Task Update_UnknownProfile_StillReportsNotFound()
    {
        var projectId = await SeedProjectAsync();

        await Assert.ThrowsAsync<WorkflowProfileNotFoundException>(() =>
            _provider.UpdateAsync(
                projectId,
                Request("missing", Source(StageBuild, "missing")),
                expectedRevision: "any-token"));
    }

    [Fact]
    public async Task Update_BlankPrecondition_IsRejectedWithoutWrite()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "blank";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _provider.UpdateAsync(projectId, Request(profileId, Source(StageBuildAndShip, profileId)), " "));

        Assert.Equal(
            created.Profile.Revision,
            (await _provider.GetDetailAsync(projectId, profileId))!.Profile.Revision);
    }

    [Fact]
    public async Task ChangeAwayAndBack_OldRevisionsNeverValidateAgain()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "away-back";
        var original = Source(StageBuild, profileId);
        var changed = Source(StageBuildAndShip, profileId);

        var created = await _provider.CreateAsync(projectId, Request(profileId, original));
        var second = await _provider.UpdateAsync(projectId, Request(profileId, changed), created.Profile.Revision!);
        // Change back to the original content: the state equals the first
        // version, but the earlier tokens stay dead.
        var third = await _provider.UpdateAsync(projectId, Request(profileId, original), second.Profile.Revision!);

        Assert.Equal(original, (await _provider.GetDetailAsync(projectId, profileId))!.Profile.DefinitionSource);
        await Assert.ThrowsAsync<WorkflowProfileRevisionConflictException>(() =>
            _provider.UpdateAsync(projectId, Request(profileId, changed), created.Profile.Revision!));
        await Assert.ThrowsAsync<WorkflowProfileRevisionConflictException>(() =>
            _provider.UpdateAsync(projectId, Request(profileId, changed), second.Profile.Revision!));
        var fourth = await _provider.UpdateAsync(projectId, Request(profileId, changed), third.Profile.Revision!);
        Assert.NotEqual(third.Profile.Revision, fourth.Profile.Revision);
    }

    [Fact]
    public async Task DeleteAndRecreate_IssuesFreshRevisionAndRejectsOldToken()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "recreate";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));

        Assert.True(await _provider.DeleteAsync(projectId, profileId));
        var recreated = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));

        Assert.NotEqual(created.Profile.Revision, recreated.Profile.Revision);
        await Assert.ThrowsAsync<WorkflowProfileRevisionConflictException>(() =>
            _provider.UpdateAsync(
                projectId,
                Request(profileId, Source(StageBuildAndShip, profileId)),
                created.Profile.Revision!));
        Assert.Equal(
            recreated.Profile.Revision,
            (await _provider.GetDetailAsync(projectId, profileId))!.Profile.Revision);
    }

    [Fact]
    public async Task UnrelatedProjectOperations_DoNotChangeRevision()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "unrelated";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuild, profileId)));

        // Enablement and default-selection writes touch other rows; the
        // Profile's replaceable content version stays put, so concurrent
        // editors are not failed by unrelated operations.
        await _provider.SetProfileEnabledAsync(projectId, "mohist/github-pr", enabled: false);
        await using (var db = new MohistDbContext(_database.Options))
        {
            var row = await db.ProjectWorkflowProfiles.SingleAsync(r => r.ProjectId == projectId);
            row.DefaultWorkflowProfileId = profileId;
            row.DefaultWorkflowProfileIdKey = profileId;
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            created.Profile.Revision,
            (await _provider.GetDetailAsync(projectId, profileId))!.Profile.Revision);
        var updated = await _provider.UpdateAsync(
            projectId,
            Request(profileId, Source(StageBuildAndShip, profileId)),
            created.Profile.Revision!);
        Assert.True(updated.ValidationResult.IsValid);
    }

    [Fact]
    public async Task Update_RemovingStageUsedByActiveRun_SucceedsForFutureRuns()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "active-run";
        var created = await _provider.CreateAsync(
            projectId, Request(profileId, Source(StageBuildAndShip, profileId)));
        await SeedActiveRunAsync(projectId, "wr_live", profileId);

        // The run executes its own binding snapshot; the future definition
        // may drop the stage and flip approval settings it no longer needs.
        var updated = await _provider.UpdateAsync(
            projectId,
            Request(profileId, Source(StageBuild, profileId)),
            created.Profile.Revision!);

        Assert.True(updated.ValidationResult.IsValid);
        var detail = (await _provider.GetDetailAsync(projectId, profileId))!;
        Assert.DoesNotContain("stage: ship", detail.Profile.DefinitionSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowWithoutRevision_FailsClosedAgainstOverwrite()
    {
        var projectId = await SeedProjectAsync();
        const string profileId = "external";
        await using (var db = new MohistDbContext(_database.Options))
        {
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = profileId,
                Name = "External",
                Description = string.Empty,
                DefinitionSource = Source(StageBuild, profileId),
                SourceProvenance = "Verbatim",
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        // A row written outside the provider exposes no revision; no
        // precondition can match, so an update fails closed rather than
        // overwriting unprotected content.
        Assert.Null((await _provider.GetDetailAsync(projectId, profileId))!.Profile.Revision);
        await Assert.ThrowsAsync<WorkflowProfileRevisionConflictException>(() =>
            _provider.UpdateAsync(
                projectId,
                Request(profileId, Source(StageBuildAndShip, profileId)),
                "any-token"));
    }

    private async Task<string> SeedProjectAsync(string projectId = "proj-1")
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
        return projectId;
    }

    private async Task SeedActiveRunAsync(string projectId, string runId, string profileId)
    {
        await using var db = new MohistDbContext(_database.Options);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = $$"""
                {"id":"{{runId}}","status":"inProgress","workflowProfileId":"{{profileId}}"}
                """,
            Status = "inProgress",
            MetadataProjectId = projectId,
            IssueNumber = 1,
            WorkflowProfileIdKey = profileId,
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
