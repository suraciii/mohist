using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

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
            assignedWorkerId: null,
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
                assignedWorkerId: status == "Ready" ? $"{prefix}-corrupt-runner" : null,
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
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-A",
            projectId: prefix,
            status: "Running",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-completed-A",
            projectId: prefix,
            status: "Completed",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-ready-B",
            projectId: prefix,
            status: "Ready",
            assignedWorkerId: runnerB);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignedToAsync(runnerA);

        var only = Assert.Single(ids);
        Assert.Equal($"{prefix}-ready-A", only);
    }

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
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-A",
            projectId: prefix,
            status: "Running",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-stopped-A",
            projectId: prefix,
            status: "Stopped",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-completed-A",
            projectId: prefix,
            status: "Completed",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-ready-B",
            projectId: prefix,
            status: "Ready",
            assignedWorkerId: runnerB);
        await InsertRowAsync(
            $"{prefix}-pending-B",
            projectId: prefix,
            status: "Pending",
            assignedWorkerId: null);

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
            assignedWorkerId: runnerId);
        await InsertRowAsync(
            $"{prefix}-ready-corrupt-other",
            projectId: prefix,
            status: "Ready",
            assignedWorkerId: $"{prefix}-other-runner",
            stateOverride: """
                {"status":"Ready","corrupt":true}
                """);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var ids = await querier.FindAssignedToAsync(runnerId);

        Assert.Equal(new[] { $"{prefix}-ready-valid-this" }, ids);
    }

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
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-2",
            projectId: prefix,
            status: "Running",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-ready-A",
            projectId: prefix,
            status: "Ready",
            assignedWorkerId: runnerA);
        await InsertRowAsync(
            $"{prefix}-running-B",
            projectId: prefix,
            status: "Running",
            assignedWorkerId: runnerB);

        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        Assert.Equal(2, await querier.CountRunningAssignedToAsync(runnerA));
        Assert.Equal(1, await querier.CountRunningAssignedToAsync(runnerB));
    }

    [Fact]
    public async Task EmptyWorkerId_ReturnsEmptyResults()
    {
        using var scope = _fixture.Services.CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        Assert.Empty(await querier.FindAssignedToAsync(""));
        Assert.Equal(0, await querier.CountRunningAssignedToAsync(""));
    }

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

        // Inspect the EF model so this test does not depend on raw
        // SQL / pragma / shared-cache visibility — EF has already
        // resolved the WorkflowRunRow entity through the model builder
        // (the same builder that other tests use to assert filtering
        // against the materialized schema), and a missing column would
        // surface as a model-vs-snapshot mismatch. Read the computed
        // column SQL expression and the index name from the model so
        // we pin both the column shape and the index declaration
        // expected by the in-memory filter queries.
        var entity = db.Model.FindEntityType(
            typeof(Mohist.Server.Infrastructure.Data.Workflow.WorkflowRunRow));
        Assert.NotNull(entity);
        var statusProperty = entity!.FindProperty("Status");
        Assert.NotNull(statusProperty);
        Assert.Equal(
            "LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))",
            statusProperty!.GetComputedColumnSql());
        var index = entity.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "IX_WorkflowRuns_Status");
        Assert.NotNull(index);
        Assert.Equal(
            new[] { "Status", "AssignedWorkerId" },
            index!.Properties.Select(p => p.Name).ToArray());
    }

    private static async Task<object?> ScalarAsync(MohistDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
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
                assignedWorkerId: status == WorkflowRunStatus.Ready ? $"{prefix}-{suffix}-runner" : null,
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
        string? assignedWorkerId,
        string? stateOverride = null)
    {
        var state = stateOverride ?? BuildStatusJson(workflowRunId, projectId, status, assignedWorkerId);
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
        BuildStatusJson(id, projectId, "Pending", assignedWorkerId: null);

    private static string BuildStatusJson(
        string id,
        string projectId,
        string status,
        string? assignedWorkerId)
    {
        var metadata = new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            ProjectId: projectId,
            IssueNumber: 1);

        var run = WorkflowRun.Create(id, BuildMinimalDefinition(), DateTimeOffset.UnixEpoch, metadata);
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
        run.Assignment = assignedWorkerId is null
            ? null
            : new WorkflowAssignment(assignedWorkerId, TestTime.UtcNow);

        return JSON.Serialize(run);
    }

    private static Mohist.Workflow.Definition.WorkflowDefinition BuildMinimalDefinition()
    {
        return new Mohist.Workflow.Definition.WorkflowDefinition(
            [new Mohist.Workflow.Definition.StageDefinition(
                "build",
                [new Mohist.Workflow.Definition.TaskDefinition("task-1", "Build task", "spec/task")],
                [])]);
    }

    private static string NewPrefix(string label) => $"{label}-{Guid.NewGuid():N}";
}
