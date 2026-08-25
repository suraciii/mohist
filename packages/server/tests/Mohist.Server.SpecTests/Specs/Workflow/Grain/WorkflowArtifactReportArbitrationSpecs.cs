using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public sealed class WorkflowArtifactReportArbitrationSpecs : WorkflowGrainSpecs
{
    public WorkflowArtifactReportArbitrationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ConcurrentSuccessAndFailure_SelectsExactlyOneTerminalResult()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var service = Services.GetRequiredService<WorkflowReportService>();

        var reports = await Task.WhenAll(
            service.ReportAsync(
                runnerId,
                work.WorkflowRunId,
                work.WorkId,
                work.TaskRunId,
                new WorkResult("completed")),
            service.ReportAsync(
                runnerId,
                work.WorkflowRunId,
                work.WorkId,
                work.TaskRunId,
                new WorkResult("failed", "runner failed")));

        Assert.Equal(["accepted", "stale"], reports.Select(report => report.Ack).Order().ToArray());
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Contains(await workflow.GetRunStatusAsync(), new[] { "Completed", "Failed" });
        var eventTypes = (await EventStore.ListAsync(work.WorkflowRunId))
            .Select(entry => entry.Envelope.Type)
            .ToArray();
        Assert.Equal(1, eventTypes.Count(type =>
            type is EventCatalog.ReverseDns.TaskCompleted or EventCatalog.ReverseDns.TaskFailed));
    }

    [Fact]
    public async Task ForeignRunner_ReportIsStaleAndDoesNotConsumeTheUpload()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, _) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(
            work.WorkflowRunId,
            work.WorkId,
            "task-1.1",
            "foreign.txt");
        var service = Services.GetRequiredService<WorkflowReportService>();

        var report = await service.ReportAsync(
            "runner-foreign",
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("stale", report.Ack);
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        await using var db = CreateDb();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task DirectTaskReport_WithMismatchedEnvelopeWorkIdIsStaleBeforeSideEffects()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(
            work.WorkflowRunId,
            work.WorkId,
            "task-1.1",
            "mismatched.txt");
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);

        var ack = await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new Mohist.Server.Workflow.Domain.Run.TaskReport(
                "other-work",
                Mohist.Server.Workflow.Domain.Run.TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                ArtifactUploadIds: new System.Collections.Generic.List<string> { uploadId },
                TaskRunId: work.TaskRunId));

        Assert.Equal(ReportAck.Stale, ack);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        await using var db = CreateDb();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task MismatchedTaskRunId_IsStaleBeforeSideEffects()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = await SeedPendingUploadAsync(
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId!,
            "wrong-attempt.txt");
        var service = Services.GetRequiredService<WorkflowReportService>();

        var report = await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            "other-task.1",
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("stale", report.Ack);
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        await using var db = CreateDb();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task TerminalTask_IdenticalReplayIsAcceptedWithoutDuplicateEvents()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var service = Services.GetRequiredService<WorkflowReportService>();
        var result = new WorkResult(
            "completed",
            "same result",
            ExitCode: 0);

        var first = await service.ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, result);
        Assert.Equal("accepted", first.Ack);
        var eventCount = (await EventStore.ListAsync(work.WorkflowRunId)).Count;

        var replay = await service.ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, result);
        Assert.Equal("accepted", replay.Ack);
        Assert.Equal(eventCount, (await EventStore.ListAsync(work.WorkflowRunId)).Count);
        Assert.Equal("Completed", await Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId).GetRunStatusAsync());
    }

    [Fact]
    public async Task TerminalTask_ConflictingReportIsStaleWithoutOutputFollowUpOrArtifactSideEffects()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var service = Services.GetRequiredService<WorkflowReportService>();

        var first = await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("completed"));
        Assert.Equal("accepted", first.Ack);
        var eventCount = (await EventStore.ListAsync(work.WorkflowRunId)).Count;

        var uploadId = await SeedPendingUploadAsync(
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId!,
            "late.txt");
        var late = await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult(
                "failed",
                "conflicting late result",
                Output: System.Text.Json.JsonSerializer.SerializeToElement(new { late = true }),
                ArtifactUploadIds: [uploadId],
                AddTasks: [new RuntimeTaskInput("late-follow-up", "Late follow-up", "spec/task")]));

        Assert.Equal("stale", late.Ack);
        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, task.Status);
        Assert.Null(task.Output);
        Assert.Equal(eventCount, (await EventStore.ListAsync(work.WorkflowRunId)).Count);
        await using var db = CreateDb();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == work.WorkflowRunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
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
            CreatedAt = TestTime.UtcNow,
            ExpiresAt = TestTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();
        return uploadId;
    }

    private MohistDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        return new MohistDbContext(options);
    }
}
