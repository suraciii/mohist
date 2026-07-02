using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Querier;

/// <summary>
/// Issue-318 T-002 specs for the two DB-layer scheduling queries plus the
/// runner-grain's <c>ActiveWorkflowCountAsync</c> count. Per design D4, every
/// scheduling query must filter on the STORED <c>Status</c> computed column
/// at the database layer; the in-memory <c>Status</c>/<c>Assignment</c>
/// re-filter loop and the <c>NextWork()</c> walk that the old code used are
/// gone. These specs seed <c>WorkflowRuns</c> rows directly so the test
/// asserts against the raw schema (the STORED column, the index, the
/// deserialization-or-not behavior) rather than the grain path. Grain-side
/// assertions live under <c>Specs/Runner/Grain</c>.
/// </summary>
[Collection("MohistDb")]
public class WorkflowRunQuerierSchedulingSpecs
{
    private readonly MohistDbFixture _fixture;

    public WorkflowRunQuerierSchedulingSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FindAssignableAsync_FiltersAtDbLayer_OnStatusPending()
    {
        var prefix = NewPrefix("sched-pending");
        await SeedRunsAcrossStatusesAsync(prefix);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignableAsync(projectId: prefix);

        var only = Assert.Single(ids);
        Assert.Equal($"{prefix}-pending", only);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FindAssignableAsync_ExcludesEveryNonPendingStatus()
    {
        var prefix = NewPrefix("sched-excludes");
        var (pendingId, nonPendingIds) = await SeedRunsAcrossStatusesAsync(prefix);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var returned = await querier.FindAssignableAsync(projectId: prefix);

        Assert.Equal(new[] { pendingId }, returned);
        foreach (var seeded in nonPendingIds)
            Assert.DoesNotContain(seeded, returned);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FindAssignableAsync_DoesNotDeserializeNonMatchingStateRows()
    {
        var prefix = NewPrefix("sched-corrupt");
        var pendingId = $"{prefix}-valid";

        // Pending row with valid State — should be returned.
        await InsertRowAsync(
            pendingId,
            projectId: prefix,
            status: "Pending",
            assignedRunnerId: null,
            stateOverride: BuildPendingJson(pendingId, prefix));

        // Rows in every other status with valid JSON but an un-deserializable
        // State shape — deserialization as WorkflowRun would throw on the
        // missing required fields (stages, metadata, etc.), so the query
        // succeeding proves the rows were never touched in-memory. The
        // trigger installs Status from the JSON `status` field, so the row
        // is reachable through the DB-layer filter but not through
        // deserialization.
        foreach (var status in new[]
                 {
                     "Created", "Ready", "Running", "AwaitingApproval",
                     "Paused", "Stopped", "Completed", "Failed"
                 })
        {
            var id = $"{prefix}-corrupt-{status.ToLowerInvariant()}";
            await InsertRowAsync(
                id,
                projectId: prefix,
                status: status,
                assignedRunnerId: status == "Ready" ? $"{prefix}-corrupt-runner" : null,
                stateOverride: $$"""
                    {"status":"{{status}}","corrupt":true}
                    """);
        }

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignableAsync(projectId: prefix);

        var only = Assert.Single(ids);
        Assert.Equal(pendingId, only);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FindAssignedToAsync_FiltersAtDbLayer_OnReadyAndRunner()
    {
        var prefix = NewPrefix("sched-assigned");
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        await InsertRowAsync(
            $"{prefix}-ready-A",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-A",
            projectId: prefix,
            status: "Running",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-completed-A",
            projectId: prefix,
            status: "Completed",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-ready-B",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: runnerB);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignedToAsync(runnerA);

        var only = Assert.Single(ids);
        Assert.Equal($"{prefix}-ready-A", only);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FindAssignedToAsync_ExcludesRunning_Terminal_AndOtherRunner()
    {
        var prefix = NewPrefix("sched-assigned-excl");
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        // Insert a Ready row for runner A so the query has at least one
        // candidate, then assert that the other-status rows for runner A
        // are filtered out. The runner-B rows must not leak through.
        await InsertRowAsync(
            $"{prefix}-ready-A",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-A",
            projectId: prefix,
            status: "Running",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-stopped-A",
            projectId: prefix,
            status: "Stopped",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-completed-A",
            projectId: prefix,
            status: "Completed",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-ready-B",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: runnerB);
        await InsertRowAsync(
            $"{prefix}-pending-B",
            projectId: prefix,
            status: "Pending",
            assignedRunnerId: null);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignedToAsync(runnerA);

        Assert.DoesNotContain($"{prefix}-running-A", ids);
        Assert.DoesNotContain($"{prefix}-stopped-A", ids);
        Assert.DoesNotContain($"{prefix}-completed-A", ids);
        Assert.DoesNotContain($"{prefix}-ready-B", ids);
        Assert.DoesNotContain($"{prefix}-pending-B", ids);
        var only = Assert.Single(ids);
        Assert.Equal($"{prefix}-ready-A", only);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FindAssignedToAsync_DoesNotDeserializeNonMatchingStateRows()
    {
        var prefix = NewPrefix("sched-corrupt-assigned");
        var runnerId = $"{prefix}-runner";

        // One Ready row with valid State for *this* runner, plus a Ready
        // row with an un-deserializable State shape for a different
        // runner. The corrupt-shape row is excluded by the runner
        // filter; if the deserialization path ran, the whole query
        // would throw on the missing required fields.
        await InsertRowAsync(
            $"{prefix}-ready-valid-this",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: runnerId);
        await InsertRowAsync(
            $"{prefix}-ready-corrupt-other",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: $"{prefix}-other-runner",
            stateOverride: """
                {"status":"Ready","corrupt":true}
                """);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignedToAsync(runnerId);

        Assert.Equal(new[] { $"{prefix}-ready-valid-this" }, ids);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CountRunningAssignedToAsync_CountsRunningRowsForTheRunner()
    {
        var prefix = NewPrefix("sched-count");
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        await InsertRowAsync(
            $"{prefix}-running-1",
            projectId: prefix,
            status: "Running",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-2",
            projectId: prefix,
            status: "Running",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-ready-A",
            projectId: prefix,
            status: "Ready",
            assignedRunnerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-B",
            projectId: prefix,
            status: "Running",
            assignedRunnerId: runnerB);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        Assert.Equal(2, await querier.CountRunningAssignedToAsync(runnerA));
        Assert.Equal(1, await querier.CountRunningAssignedToAsync(runnerB));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task EmptyRunnerId_ReturnsEmptyResults()
    {
        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        Assert.Empty(await querier.FindAssignedToAsync(""));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync(""));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowRunsRow_HasStoredStatusColumnAndIndex()
    {
        // The model declares the STORED status computed column (D3) and the
        // IX_WorkflowRuns_Status index. The migration that materializes
        // them at deploy time is owned by T-004; here we just assert the
        // schema in the test DB has the column and index so the new
        // filtering queries can run.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var statusColumn = await db.Database
            .SqlQueryRaw<string>("""
                SELECT "name" FROM pragma_table_info('WorkflowRuns') WHERE "name" = 'Status';
                """)
            .ToListAsync();
        Assert.Equal("Status", Assert.Single(statusColumn));

        var indexes = await db.Database
            .SqlQueryRaw<string>("""
                SELECT "name" FROM sqlite_master
                WHERE type = 'index' AND tbl_name = 'WorkflowRuns' AND "name" = 'IX_WorkflowRuns_Status';
                """)
            .ToListAsync();
        Assert.Equal("IX_WorkflowRuns_Status", Assert.Single(indexes));
    }

    private async Task<(string PendingId, List<string> NonPendingIds)> SeedRunsAcrossStatusesAsync(string prefix)
    {
        // One row per status, all unassigned. Only the Pending row should
        // surface from FindAssignableAsync.
        var statuses = new (string suffix, WorkflowRunStatus status)[]
        {
            ("created", WorkflowRunStatus.Created),
            ("pending", WorkflowRunStatus.Pending),
            ("ready", WorkflowRunStatus.Ready),
            ("running", WorkflowRunStatus.Running),
            ("awaiting-approval", WorkflowRunStatus.AwaitingApproval),
            ("paused", WorkflowRunStatus.Paused),
            ("stopped", WorkflowRunStatus.Stopped),
            ("completed", WorkflowRunStatus.Completed),
            ("failed", WorkflowRunStatus.Failed),
        };

        var pending = string.Empty;
        var nonPending = new List<string>(statuses.Length - 1);
        foreach (var (suffix, status) in statuses)
        {
            var id = $"{prefix}-{suffix}";
            await InsertRowAsync(
                id,
                projectId: prefix,
                status: status.ToString(),
                assignedRunnerId: status == WorkflowRunStatus.Ready ? $"{prefix}-{suffix}-runner" : null,
                stateOverride: status == WorkflowRunStatus.Pending
                    ? BuildPendingJson(id, prefix)
                    : null);
            if (status == WorkflowRunStatus.Pending)
                pending = id;
            else
                nonPending.Add(id);
        }
        return (pending, nonPending);
    }

    /// <summary>
    /// Inserts a <see cref="WorkflowRunRow"/> with the requested status and
    /// assignment. If <paramref name="stateOverride"/> is non-null, that
    /// JSON is used verbatim for the State column (so we can corrupt it on
    /// purpose for the no-deserialization tests); otherwise a JSON
    /// envelope that the production serializer would produce is used so the
    /// STORED Status column gets populated by the
    /// <c>WorkflowRuns_AI_Status</c> trigger installed by the test schema
    /// fix-up.
    /// </summary>
    private async Task InsertRowAsync(
        string workflowRunId,
        string projectId,
        string status,
        string? assignedRunnerId,
        string? stateOverride = null)
    {
        var state = stateOverride ?? BuildStatusJson(workflowRunId, projectId, status, assignedRunnerId);
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = state,
        });
        await db.SaveChangesAsync();
    }

    private static string BuildPendingJson(string id, string projectId) =>
        BuildStatusJson(id, projectId, "Pending", assignedRunnerId: null);

    private static string BuildStatusJson(
        string id,
        string projectId,
        string status,
        string? assignedRunnerId)
    {
        var metadata = new WorkflowRunMetadata(
            Name: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
                ["issueId"] = $"issue-{id}",
            });

        var run = WorkflowRun.Create(id, BuildMinimalDefinition(), metadata);
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks =
            {
                new TaskRun
                {
                    Id = "task-1",
                    DefinitionId = "task-1",
                    Attempt = 1,
                    Title = "Build task",
                    Status = TaskRunStatus.Pending,
                    Classification = TaskClassification.UserFacing,
                },
            },
        });
        run.CurrentStageId = "build";
        run.Status = Enum.Parse<WorkflowRunStatus>(status);
        run.Assignment = assignedRunnerId is null
            ? null
            : new WorkflowAssignment(assignedRunnerId, DateTimeOffset.UtcNow);

        return JSON.Serialize(run);
    }

    private static Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition BuildMinimalDefinition()
    {
        return new Mohist.Server.Workflow.Domain.Definition.WorkflowDefinition(
            "spec/workflow",
            [new Mohist.Server.Workflow.Domain.Definition.StageDefinition(
                "build",
                [new Mohist.Server.Workflow.Domain.Definition.TaskDefinition("task-1", "Build task", "spec/task")],
                [])]);
    }

    private static string NewPrefix(string label) => $"{label}-{Guid.NewGuid():N}";
}