using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Workflow.Definition;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Workflow.GrainContracts;

/// <summary>
/// Terminal-result arbitration on the real grain without a cluster: foreign
/// runner and identity-mismatched reports are stale before any artifact side
/// effect, identical terminal replays stay event-free, conflicting late
/// reports change nothing (#681).
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowGrainReportArbitrationSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainReportArbitrationSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ForeignRunner_ReportIsStaleAndDoesNotConsumeTheUpload()
    {
        var a = await ArrangeAsync("wr-arb-foreign-runner");
        var uploadId = await SeedPendingUploadAsync(a.RunId, a.WorkId!, "task-1.1", "foreign.txt");

        var report = await a.CreateReportService().ReportAsync(
            $"other-{a.WorkerId}",
            a.RunId,
            a.Work.Id!,
            a.ActionAttemptId,
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("refused", report.Ack);
        Assert.Equal("Running", await a.Grain.GetRunStatusAsync());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == a.RunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task DirectTaskReport_WithMismatchedEnvelopeWorkIdIsStaleBeforeSideEffects()
    {
        var a = await ArrangeAsync("wr-arb-envelope-workid");
        var uploadId = await SeedPendingUploadAsync(a.RunId, a.WorkId!, "task-1.1", "mismatched.txt");

        var ack = await a.Grain.ReceiveTaskReportAsync(
            a.WorkerId,
            a.Work.Id!,
            new TaskReport(
                "other-work",
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                ArtifactUploadIds: [uploadId],
                ActionAttemptId: a.ActionAttemptId));

        Assert.Equal(WorkReportVerdict.Refused, ack);
        Assert.Equal("Running", await a.Grain.GetRunStatusAsync());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == a.RunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task MismatchedActionAttemptId_IsStaleBeforeSideEffects()
    {
        var a = await ArrangeAsync("wr-arb-taskrunid");
        var uploadId = await SeedPendingUploadAsync(a.RunId, a.WorkId!, a.ActionAttemptId, "wrong-attempt.txt");

        var report = await a.CreateReportService().ReportAsync(
            a.WorkerId,
            a.RunId,
            a.Work.Id!,
            "other-task.1",
            new WorkResult("completed", ArtifactUploadIds: [uploadId]));

        Assert.Equal("refused", report.Ack);
        Assert.Equal("Running", await a.Grain.GetRunStatusAsync());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == a.RunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    [Fact]
    public async Task TerminalTask_IdenticalReplayIsAcceptedWithoutDuplicateEvents()
    {
        var a = await ArrangeAsync("wr-arb-replay");
        var result = new WorkResult(
            "completed",
            "same result",
            ExitCode: 0);

        var first = await a.CreateReportService().ReportAsync(a.WorkerId, a.RunId, a.Work.Id!, a.ActionAttemptId, result);
        Assert.Equal("accepted", first.Ack);
        var eventCount = (await a.Events.ListAsync(a.RunId)).Count;

        var replay = await a.CreateReportService().ReportAsync(a.WorkerId, a.RunId, a.Work.Id!, a.ActionAttemptId, result);
        Assert.Equal("accepted", replay.Ack);
        Assert.Equal(eventCount, (await a.Events.ListAsync(a.RunId)).Count);
        Assert.Equal("Completed", await a.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task TerminalChecks_ExactReplayIsAcceptedAndConflictingReplayIsRefused()
    {
        var definition = new WorkflowDefinition([
            new StageDefinition(
                "build",
                [new TaskDefinition("task-1", "Task 1", "spec/task")],
                [new CheckDefinition("check-1", "Check 1", "spec/check")]),
        ]);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            "wr-check-replay",
            definition,
            TimeProvider,
            workerId: "runner-check-replay");
        var task = Assert.IsType<WorkItem>(await arrangement.AssignAndClaimAsync());
        await arrangement.ReportCompletedAsync(task);
        var checks = Assert.IsType<WorkItem>(await arrangement.AssignAndClaimAsync());
        var report = new CheckReport(
            checks.Stage,
            [new CheckResult("check-1", CheckResultStatus.Passed)],
            "fingerprint-1");

        Assert.Equal(
            WorkReportVerdict.Accepted,
            await arrangement.Grain.ReceiveCheckReportAsync(arrangement.WorkerId, checks.Id!, report));
        var eventCount = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;

        Assert.Equal(
            WorkReportVerdict.Accepted,
            await arrangement.Grain.ReceiveCheckReportAsync(arrangement.WorkerId, checks.Id!, report));
        Assert.Equal(eventCount, (await arrangement.Events.ListAsync(arrangement.RunId)).Count);
        Assert.Equal(
            WorkReportVerdict.Refused,
            await arrangement.Grain.ReceiveCheckReportAsync(
                arrangement.WorkerId,
                checks.Id!,
                report with { TerminalResultFingerprint = "fingerprint-2" }));
        Assert.Equal(eventCount, (await arrangement.Events.ListAsync(arrangement.RunId)).Count);
    }

    [Fact]
    public async Task TerminalTask_ConflictingReportIsStaleWithoutOutputFollowUpOrArtifactSideEffects()
    {
        var a = await ArrangeAsync("wr-arb-conflict");
        var first = await a.CreateReportService().ReportAsync(
            a.WorkerId, a.RunId, a.Work.Id!, a.ActionAttemptId, new WorkResult("completed"));
        Assert.Equal("accepted", first.Ack);
        var eventCount = (await a.Events.ListAsync(a.RunId)).Count;

        var uploadId = await SeedPendingUploadAsync(a.RunId, a.WorkId!, a.ActionAttemptId, "late.txt");
        var late = await a.CreateReportService().ReportAsync(
            a.WorkerId,
            a.RunId,
            a.Work.Id!,
            a.ActionAttemptId,
            new WorkResult(
                "failed",
                "conflicting late result",
                Output: System.Text.Json.JsonSerializer.SerializeToElement(new { late = true }),
                ArtifactUploadIds: [uploadId],
                AddTasks: [new RuntimeTaskInput("late-follow-up", "Late follow-up", "spec/task")]));

        Assert.Equal("refused", late.Ack);
        var run = await a.LoadRunAsync();
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, task.Status);
        Assert.Null(task.Output);
        Assert.Equal(eventCount, (await a.Events.ListAsync(a.RunId)).Count);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        Assert.Empty(await db.WorkflowArtifacts
            .Where(row => row.WorkflowRunId == a.RunId)
            .ToListAsync());
        Assert.NotNull(await db.WorkflowArtifactPendingUploads.FindAsync(uploadId));
    }

    private async Task<ArbitrationArrangement> ArrangeAsync(string runId)
    {
        var definition = SingleStage([new TaskDefinition("task-1", "Task 1", "spec/task")]);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, definition, TimeProvider, workerId: $"runner-{runId}");
        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);
        var actionAttemptId = (await arrangement.Store.LoadAsync(runId))!.CurrentStage().RunningTask?.Id
            ?? throw new InvalidOperationException("no running task");
        return new ArbitrationArrangement(arrangement, work!, actionAttemptId);
    }

    private static WorkflowDefinition SingleStage(List<TaskDefinition> tasks) => new(
    [
        new StageDefinition("build", tasks, []),
    ]);

    private async Task<string> SeedPendingUploadAsync(string workflowRunId, string workId, string actionAttemptId, string path)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var uploadId = $"artup_{Guid.NewGuid():N}";
        db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = workflowRunId,
            WorkId = workId,
            ActionAttemptId = actionAttemptId,
            Path = path,
            ContentType = "text/plain",
            ContentHash = $"sha256:{Guid.NewGuid():N}",
            Size = 42,
            StoragePath = $"workflows/{workflowRunId}/tasks/{actionAttemptId}/artifacts/{uploadId}/content",
            CreatedAt = TimeProvider.GetUtcNow(),
            ExpiresAt = TimeProvider.GetUtcNow().AddDays(1),
        });
        await db.SaveChangesAsync();
        return uploadId;
    }

    private sealed record ArbitrationArrangement(
        WorkflowGrainArrangement Arrangement,
        WorkItem Work,
        string ActionAttemptId)
    {
        public WorkflowGrain Grain => Arrangement.Grain;
        public IEventStore Events => Arrangement.Events;
        public IWorkflowRunStore Store => Arrangement.Store;
        public string RunId => Arrangement.RunId;
        public string WorkerId => Arrangement.WorkerId;
        public string? WorkId => Work.Id;

        public WorkflowReportService CreateReportService() =>
            WorkflowGrainContractSupport.CreateReportService(Arrangement.Services, Grain);

        public async Task<WorkflowRun> LoadRunAsync() =>
            await Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
    }
}
