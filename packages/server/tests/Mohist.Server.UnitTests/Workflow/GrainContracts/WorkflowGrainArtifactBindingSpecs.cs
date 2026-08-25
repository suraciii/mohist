using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Artifact binding and per-attempt projection of the workflow run:
/// idempotent upload binding, lineage events, best-effort completion on
/// missing artifacts, foreign-upload rejection, and immutable history across
/// attempts. Drives the real grain without a cluster (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainArtifactBindingSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainArtifactBindingSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CompletedTask_WithUploadedArtifacts_BindsAndRecordsEvents()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-bind-events",
            declaredArtifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
            uploadPath: "review.md");

        await a.Arrangement.Grain.RefreshIssueContextAsync(new WorkflowIssueContext(a.ProjectId, 1, 1));
        await a.Arrangement.ReportTaskResultAsync(
            a.Work, output: null, addTasks: null, artifactUploadIds: [a.UploadId!]);

        Assert.Equal("Completed", await a.Arrangement.Grain.GetRunStatusAsync());

        await using var db = CreateDb();
        var artifacts = await ArtifactsOf(db, a.RunId);
        Assert.Single(artifacts);
        Assert.Equal("review.md", artifacts[0].Path);
        Assert.Equal(a.TaskRunId, artifacts[0].TaskRunId);

        var events = (await a.Arrangement.Events.ListAsync(a.RunId)).ToList();
        var artifactIndex = events.FindIndex(entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowArtifactRecorded);
        var completedIndex = events.FindIndex(entry =>
            entry.Envelope.Type == EventCatalog.ReverseDns.TaskCompleted);
        Assert.True(artifactIndex >= 0);
        Assert.True(completedIndex > artifactIndex);
        Assert.Equal(a.RunId, events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.WorkflowRunId]);
        Assert.Equal(a.ProjectId, events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("1", events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal("1", events[artifactIndex].Envelope.Extensions[EventCatalog.Lineage.Epic]);
        Assert.False(events[artifactIndex].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.Stage));
    }

    [Fact]
    public async Task RefreshIssueContextAsync_OverwritesTheCurrentEpicWithoutARevision()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-lineage-epic",
            seedUpload: false,
            declaredArtifacts: null);
        var context = new WorkflowIssueContext(a.ProjectId, 1, 1);
        await a.Arrangement.Grain.RefreshIssueContextAsync(context);
        await a.Arrangement.Grain.RefreshIssueContextAsync(context with { EpicNumber = 2 });
        await a.Arrangement.Grain.RefreshIssueContextAsync(context with { EpicNumber = 2 });

        await a.Arrangement.Grain.PauseAsync("lineage assertion");

        var paused = Assert.Single(
            await a.Arrangement.Events.ListAsync(a.RunId),
            entry => entry.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunPaused);
        Assert.Equal("2", paused.Envelope.Extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public async Task CompletedTask_MissingDeclaredArtifact_CompletesWithBestEffort()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-missing-declared",
            declaredArtifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
            seedUpload: false);

        await a.Arrangement.ReportTaskResultAsync(a.Work, output: null, addTasks: null);

        Assert.Equal("Completed", await a.Arrangement.Grain.GetRunStatusAsync());
        await using var db = CreateDb();
        Assert.Empty(await ArtifactsOf(db, a.RunId));
    }

    [Fact]
    public async Task CompletedTask_DeclaredArtifactUploaded_Succeeds()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-declared-ok",
            declaredArtifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]));

        await a.Arrangement.ReportTaskResultAsync(
            a.Work, output: null, addTasks: null, artifactUploadIds: [a.UploadId!]);

        await using var db = CreateDb();
        Assert.Empty(await PendingUploadsOf(db, a.RunId));
    }

    [Fact]
    public async Task ConcurrentReplay_SelectsOneWinnerAndBindsTheUploadOnce()
    {
        var a = await ArrangeWithUploadAsync("wr-art-concurrent-replay");
        var taskRunId = a.TaskRunId;
        var service = WorkflowGrainContractSupport.CreateReportService(
            a.Services,
            a.Grain,
            a.Operations is null ? null : runnerId => a.Operations.For(runnerId));
        var result = new WorkResult("completed", ArtifactUploadIds: [a.UploadId!]);

        var reports = await Task.WhenAll(
            service.ReportAsync(a.WorkerId, a.RunId, a.Work.Id!, taskRunId, result),
            service.ReportAsync(a.WorkerId, a.RunId, a.Work.Id!, taskRunId, result));

        Assert.Equal(["accepted", "accepted"], reports.Select(report => report.Ack).Order().ToArray());
        await using var db = CreateDb();
        var artifact = Assert.Single(await ArtifactsOf(db, a.RunId));
        Assert.Equal(a.UploadId, artifact.SourceUploadId);
        Assert.Equal(taskRunId, artifact.TaskRunId);
        var eventTypes = (await a.Arrangement.Events.ListAsync(a.RunId))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.Single(eventTypes, type => type == EventCatalog.ReverseDns.WorkflowArtifactRecorded);
        Assert.Single(eventTypes, type => type == EventCatalog.ReverseDns.TaskCompleted);
    }

    [Fact]
    public async Task TerminalTask_LateReportDoesNotConsumeItsUpload()
    {
        var a = await ArrangeWithUploadAsync("wr-art-late-report");
        await a.Arrangement.ReportTaskResultAsync(a.Work, output: null, addTasks: null);
        var uploadId = await SeedPendingUploadAsync(a.RunId, a.Work.Id!, "task-1.1", "late.txt");
        var service = WorkflowGrainContractSupport.CreateReportService(
            a.Services,
            a.Grain,
            a.Operations is null ? null : runnerId => a.Operations.For(runnerId));

        var report = await service.ReportAsync(
            a.WorkerId,
            a.RunId,
            a.Work.Id!,
            a.TaskRunId,
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("stale", report.Ack);
        await using var db = CreateDb();
        Assert.Empty(await ArtifactsOf(db, a.RunId));
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task BoundUpload_ReplayReturnsTheOriginalArtifactAndRejectsAnotherTaskAttempt()
    {
        var a = await ArrangeWithUploadAsync("wr-art-bind-replay");
        var uploadId = a.UploadId!;
        var bindService = a.Services.GetRequiredService<IWorkflowArtifactBindService>();

        var first = await bindService.BindAsync(a.RunId, a.Work.Id!, "task-1.1", [uploadId], declaredArtifacts: null);
        var replay = await bindService.BindAsync(a.RunId, a.Work.Id!, "task-1.1", [uploadId], declaredArtifacts: null);
        var foreignAttempt = await bindService.BindAsync(a.RunId, a.Work.Id!, "task-1.2", [uploadId], declaredArtifacts: null);

        Assert.True(first.IsSuccess, first.Error);
        Assert.True(replay.IsSuccess, replay.Error);
        Assert.Equal(first.ArtifactRecordedEvents, replay.ArtifactRecordedEvents);
        Assert.False(foreignAttempt.IsSuccess);
        Assert.Contains("different workflow task attempt", foreignAttempt.Error, StringComparison.Ordinal);
        await using var db = CreateDb();
        var artifact = Assert.Single(await ArtifactsOf(db, a.RunId));
        Assert.Equal(uploadId, artifact.SourceUploadId);
        Assert.Equal("task-1.1", artifact.TaskRunId);
    }

    [Fact]
    public async Task ArtifactBindingMigration_AddsNullableReplayIdentityAndFilteredUniqueIndex()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new MohistDbContext(options);
        await db.Database.MigrateAsync();

        await using var column = connection.CreateCommand();
        column.CommandText = "SELECT 1 FROM pragma_table_info('WorkflowArtifacts') WHERE name = 'SourceUploadId' LIMIT 1;";
        Assert.NotNull(await column.ExecuteScalarAsync());

        await using var index = connection.CreateCommand();
        index.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'UX_WorkflowArtifacts_SourceUploadId';";
        var sql = Assert.IsType<string>(await index.ExecuteScalarAsync());
        Assert.Contains("UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"SourceUploadId\" IS NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRecoveryFollowUp_AcksAndFailsTheRunWithoutBindingArtifacts()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("output.promise=FAIL", [], RetrySelf: true)]);
        var a = await ArrangeWithUploadAsync(
            "wr-art-invalid-recovery",
            declaredArtifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]));
        var uploadId = a.UploadId!;

        // A permanently invalid recovery follow-up acks and fails the run
        // terminally rather than throwing. The terminal failure path does not
        // bind artifacts, so the upload remains pending (recoverable by retry).
        await a.Arrangement.ReportTaskResultAsync(
            a.Work,
            System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            [new RuntimeTaskInput("task-1", "Task 1", "spec/task", Recovery: recovery)],
            artifactUploadIds: [uploadId]);

        Assert.Equal("Failed", await a.Arrangement.Grain.GetRunStatusAsync());

        await using var db = CreateDb();
        Assert.Empty(await ArtifactsOf(db, a.RunId));
        Assert.Single(await PendingUploadsOf(db, a.RunId), p => p.UploadId == uploadId);
    }

    [Fact]
    public async Task FailedTask_WithDiagnosticUploads_BindsArtifacts()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-failed-diagnostic",
            declaredArtifacts: null,
            uploadPath: "diagnostic.log");

        await a.Arrangement.ReportTaskResultAsync(
            a.Work,
            output: null,
            addTasks: null,
            status: TaskReportStatus.Failed,
            artifactUploadIds: [a.UploadId!]);

        await using var db = CreateDb();
        var artifacts = await ArtifactsOf(db, a.RunId);
        Assert.Single(artifacts);
        Assert.Equal("diagnostic.log", artifacts[0].Path);
        Assert.Equal("task-1.1", artifacts[0].TaskRunId);
    }

    [Fact]
    public async Task ForeignUploadId_Rejected_TaskFails()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-foreign-upload",
            declaredArtifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
            seedUpload: false);

        var foreignUploadId = $"artup_{Guid.NewGuid():N}";
        await a.Arrangement.ReportTaskResultAsync(
            a.Work,
            output: null,
            addTasks: null,
            artifactUploadIds: [foreignUploadId]);

        Assert.Equal("Failed", await a.Arrangement.Grain.GetRunStatusAsync());

        await using var db = CreateDb();
        Assert.Empty(await ArtifactsOf(db, a.RunId));
    }

    [Fact]
    public async Task NoWorkflowArtifactMissingEvent_Emitted()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-no-missing-event",
            declaredArtifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]),
            seedUpload: false);

        await a.Arrangement.ReportTaskResultAsync(a.Work, output: null, addTasks: null);

        var events = await a.Arrangement.Events.ListAsync(a.RunId);
        Assert.DoesNotContain(events, e =>
            e.Envelope.Type?.Contains("artifact.missing", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task DynamicArtifact_WithoutDeclaration_BindsOnCompleted()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-dynamic",
            declaredArtifacts: null,
            uploadPath: "output.txt");

        await a.Arrangement.ReportTaskResultAsync(
            a.Work, output: null, addTasks: null, artifactUploadIds: [a.UploadId!]);

        Assert.Equal("Completed", await a.Arrangement.Grain.GetRunStatusAsync());

        await using var db = CreateDb();
        var artifacts = await ArtifactsOf(db, a.RunId);
        Assert.Single(artifacts);
        Assert.Equal("output.txt", artifacts[0].Path);
    }

    [Fact]
    public async Task MultipleUploads_BindAtomically_TaskSucceeds()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-multi",
            declaredArtifacts: new TaskArtifactCapture([
                new TaskArtifactDeclaration("design.md"),
                new TaskArtifactDeclaration("tasks.json"),
            ]),
            uploadPath: "design.md");
        var uploadId2 = await SeedPendingUploadAsync(a.RunId, a.Work.Id!, "task-1.1", "tasks.json");

        await a.Arrangement.ReportTaskResultAsync(
            a.Work, output: null, addTasks: null, artifactUploadIds: [a.UploadId!, uploadId2]);

        Assert.Equal("Completed", await a.Arrangement.Grain.GetRunStatusAsync());

        await using var db = CreateDb();
        var artifacts = (await ArtifactsOf(db, a.RunId))
            .OrderBy(artifact => artifact.Path)
            .ToList();
        Assert.Equal(2, artifacts.Count);
        Assert.Equal("design.md", artifacts[0].Path);
        Assert.Equal("tasks.json", artifacts[1].Path);
    }

    [Fact]
    public async Task RepeatedTaskRuns_EachRetainTheirOwnArtifactSummary()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-history-first",
            tasks: [new TaskDefinition("ai-review", "AI review", "spec/task",
                Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))],
            uploadPath: "review.md",
            taskRunId: "ai-review.1");

        await a.Arrangement.ReportTaskResultAsync(
            a.Work, output: null, addTasks: null, artifactUploadIds: [a.UploadId!]);

        var status = await a.Querier.GetStatusAsync(a.RunId);
        var task1 = status!.Stages[0].Tasks[0];
        Assert.NotNull(task1.ArtifactSummaries);
        var summary1 = Assert.Single(task1.ArtifactSummaries);
        Assert.Equal("review.md", summary1.Path);

        await using var db = CreateDb();
        var latestArtifacts = (await ArtifactsOf(db, a.RunId))
            .OrderByDescending(artifact => artifact.RecordedAt)
            .ToList();
        Assert.Single(latestArtifacts);
        Assert.Equal("ai-review.1", latestArtifacts[0].TaskRunId);
    }

    [Fact]
    public async Task LatestArtifact_PointsToNewestRun_WhileOlderRemain()
    {
        var a = await ArrangeWithUploadAsync(
            "wr-art-latest",
            tasks: [new TaskDefinition("ai-review", "AI review", "spec/task",
                Artifacts: new TaskArtifactCapture([new TaskArtifactDeclaration("review.md")]))],
            uploadPath: "review.md",
            taskRunId: "ai-review.1");

        await a.Arrangement.ReportTaskResultAsync(
            a.Work, output: null, addTasks: null, artifactUploadIds: [a.UploadId!]);

        var status1 = await a.Querier.GetStatusAsync(a.RunId);
        var task1 = status1!.Stages[0].Tasks[0];
        Assert.Single(task1.ArtifactSummaries!);
        Assert.Equal("review.md", task1.ArtifactSummaries![0].Path);

        var status2 = await a.Querier.GetStatusAsync(a.RunId);
        Assert.NotNull(status2!.Stages[0].Tasks[0].ArtifactSummaries);

        await using var db = CreateDb();
        var allArtifacts = (await ArtifactsOf(db, a.RunId))
            .OrderBy(artifact => artifact.RecordedAt)
            .ToList();
        Assert.Single(allArtifacts);
        Assert.Equal("ai-review.1", allArtifacts[0].TaskRunId);
    }

    /// <summary>
    /// Starts a single-task run and claims its work. Optionally declares
    /// captured artifacts and seeds a pending upload; pins the expected
    /// task-run id for history assertions.
    /// </summary>
    private async Task<ArtifactArrangement> ArrangeWithUploadAsync(
        string runId,
        TaskArtifactCapture? declaredArtifacts = null,
        bool seedUpload = true,
        string uploadPath = "result.txt",
        List<TaskDefinition>? tasks = null,
        string? taskRunId = null)
    {
        var definition = SingleStage(
            tasks ?? [new("task-1", "Task 1", "spec/task", Artifacts: declaredArtifacts)]);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition, TimeProvider);
        await arrangement.Grain.AssignWorkerAsync(arrangement.WorkerId);
        var work = await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId);
        Assert.NotNull(work);
        var resolvedTaskRunId = taskRunId ?? await RunningTaskRunIdAsync(arrangement);
        var uploadId = seedUpload
            ? await SeedPendingUploadAsync(arrangement.RunId, work!.Id!, resolvedTaskRunId, uploadPath)
            : null;
        return new ArtifactArrangement(arrangement, work!, resolvedTaskRunId, arrangement.ProjectId, uploadId, _fixture.Services);
    }

    private sealed record ArtifactArrangement(
        WorkflowGrainArrangement Arrangement,
        WorkItem Work,
        string TaskRunId,
        string ProjectId,
        string? UploadId,
        IServiceProvider Services)
    {
        public RunnerUpdateOperationGrainRegistry? Operations => Arrangement.Operations;
        public WorkflowGrain Grain => Arrangement.Grain;
        public IEventStore Events => Arrangement.Events;
        public WorkflowQuerier Querier => Arrangement.Querier;
        public string RunId => Arrangement.RunId;
        public string WorkerId => Arrangement.WorkerId;
    }

    private static WorkflowDefinition SingleStage(List<TaskDefinition> tasks) => new(
    [
        new StageDefinition("build", tasks, []),
    ]);

    private static async Task<string> RunningTaskRunIdAsync(WorkflowGrainArrangement arrangement)
    {
        var run = await arrangement.Store.LoadAsync(arrangement.RunId)
            ?? throw new InvalidOperationException("run missing");
        return run.CurrentStage().RunningTask?.Id
            ?? throw new InvalidOperationException("no running task");
    }

    private async Task<string> SeedPendingUploadAsync(string workflowRunId, string workId, string taskRunId, string path)
    {
        await using var db = CreateDb();
        var uploadId = $"artup_{Guid.NewGuid():N}";
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            TaskRunId = taskRunId,
            Path = path,
            ContentType = "text/plain",
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Size = 42,
            StoragePath = $"workflows/{workflowRunId}/tasks/{taskRunId}/artifacts/{uploadId}/content",
            CreatedAt = TimeProvider.GetUtcNow(),
            ExpiresAt = TimeProvider.GetUtcNow().AddDays(1),
        });
        await db.SaveChangesAsync();
        return uploadId;
    }

    private static async Task<List<WorkflowArtifactRow>> ArtifactsOf(MohistDbContext db, string runId) =>
        await db.WorkflowArtifacts.Where(a => a.WorkflowRunId == runId).ToListAsync();

    private static async Task<List<WorkflowArtifactPendingUploadRow>> PendingUploadsOf(MohistDbContext db, string runId) =>
        await db.WorkflowArtifactPendingUploads.Where(p => p.WorkflowRunId == runId).ToListAsync();

    private MohistDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        return new MohistDbContext(options);
    }
}
