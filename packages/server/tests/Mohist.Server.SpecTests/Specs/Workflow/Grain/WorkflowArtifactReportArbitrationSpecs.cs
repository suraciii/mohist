using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public partial class WorkflowArtifactBindingSpecs
{
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
                ArtifactUploadIds: new[] { uploadId },
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
}
