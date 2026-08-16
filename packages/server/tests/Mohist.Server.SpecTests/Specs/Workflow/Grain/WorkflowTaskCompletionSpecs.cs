using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
public sealed class WorkflowTaskCompletionSpecs : WorkflowGrainSpecs
{
    public WorkflowTaskCompletionSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task MissingBoundary_IsRejectedWithoutUsingLegacySettlement()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);

        var result = await Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>()
            .ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, new WorkResult("completed"));

        Assert.Equal(("stale", "Running"), result);
        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Null(task.CompletionBoundary);
        Assert.Null(task.WorkflowTaskRecovery);
        Assert.Null(task.AgentResultSettlement);
        Assert.NotNull(run.CurrentPendingWork());
        Assert.Null(await workflow.ClaimNextAsync(runnerId));
    }

    [Fact]
    public async Task CleanBoundary_IsPersistedAndReplayedExactlyOnce()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var output = JsonSerializer.SerializeToElement(new { answer = "ok" });
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.CommittedClean, output: output);
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        var result = new WorkResult(
            "completed",
            Output: output,
            CompletionBoundary: boundary);

        Assert.Equal(("accepted", "Completed"), await service.ReportAsync(
            runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, result));
        var eventCount = (await EventStore.ListAsync(work.WorkflowRunId)).Count;

        var replay = await service.ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, result);
        Assert.Equal(("accepted", "Completed"), replay);
        Assert.Equal(eventCount, (await EventStore.ListAsync(work.WorkflowRunId)).Count);

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, task.Status);
        Assert.True(task.CompletionProjectionApplied);
        Assert.Equal("ok", task.Output!.Value.GetProperty("answer").GetString());
        Assert.Null(task.WorkflowTaskRecovery);
    }

    [Fact]
    public async Task CleanBoundary_BindsArtifactsOnceAcrossAnExactReplay()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var uploadId = $"upload-{Guid.NewGuid():N}";
        await using (var db = await Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync())
        {
            db.WorkflowArtifactPendingUploads.Add(new WorkflowArtifactPendingUploadRow
            {
                UploadId = uploadId,
                WorkflowRunId = work.WorkflowRunId,
                WorkId = work.WorkId,
                TaskRunId = work.TaskRunId!,
                Path = "result.txt",
                ContentType = "text/plain",
                ContentHash = "sha256:test",
                Size = 4,
                StoragePath = $"workflows/{work.WorkflowRunId}/{uploadId}",
                CreatedAt = TestTime.UtcNow,
                ExpiresAt = TestTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var boundary = Boundary(
            work,
            runnerId,
            WorkflowTaskWorkspaceOutcomes.CommittedClean,
            artifactUploadIds: [uploadId]);
        var report = new WorkResult(
            "completed",
            ArtifactUploadIds: [uploadId],
            CompletionBoundary: boundary);
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        Assert.Equal(("accepted", "Completed"), await service.ReportAsync(
            runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, report));
        Assert.Equal(("accepted", "Completed"), await service.ReportAsync(
            runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, report));

        await using var resultDb = await Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        Assert.Single(await resultDb.WorkflowArtifacts.Where(a => a.WorkflowRunId == work.WorkflowRunId).ToListAsync());
        Assert.Empty(await resultDb.WorkflowArtifactPendingUploads.Where(a => a.WorkflowRunId == work.WorkflowRunId).ToListAsync());
        Assert.Single(
            await EventStore.ListAsync(work.WorkflowRunId),
            e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowArtifactRecorded);
    }

    [Fact]
    public async Task DirtyBoundary_RemainsRunningAndExposesRecoveryWithoutPendingWork()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var output = JsonSerializer.SerializeToElement(new { preserved = true });
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.Dirty, ["source.cs"], reason: "unscoped-task-change", output: output);
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();

        Assert.Equal(("accepted", "Running"), await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult(
                "completed",
                Output: output,
                WorkspaceOutcome: WorkflowTaskWorkspaceOutcomes.Dirty,
                WorkspaceReason: "unscoped-task-change",
                CompletionBoundary: boundary)));

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(StageRunStatus.Running, run.CurrentStage().Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(WorkflowTaskRecoveryState.Dirty, task.WorkflowTaskRecovery!.State);
        Assert.Equal("unscoped-task-change", task.WorkflowTaskRecovery.Reason);
        Assert.Null(run.CurrentPendingWork());
        Assert.Null(await Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId).ClaimNextAsync(runnerId));

        var status = await GetQuerier().GetStatusAsync(work.WorkflowRunId);
        Assert.NotNull(status);
        Assert.Equal("recoverable-dirty", status!.Status);
        var statusTask = Assert.Single(Assert.Single(status.Stages).Tasks);
        Assert.Equal("recoverable-dirty", statusTask.Status);
        Assert.True(statusTask.Output!.Value.GetProperty("preserved").GetBoolean());
        Assert.Contains("workspace-verification", statusTask.WorkflowTaskRecovery!.RecoveryActions!);
        Assert.Null(status.PendingWork);
        Assert.Null(status.Failure);
    }

    [Fact]
    public async Task UnconfirmedBoundary_UsesRecoverableUnconfirmedAndDoesNotFailTheRun()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var output = JsonSerializer.SerializeToElement(new { retained = "yes" });
        var boundary = Boundary(
            work,
            runnerId,
            WorkflowTaskWorkspaceOutcomes.Unconfirmed,
            reason: "workspace-probe-timeout",
            authoritative: false,
            output: output);
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();

        Assert.Equal(("accepted", "Running"), await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult(
                "completed",
                Output: output,
                WorkspaceOutcome: WorkflowTaskWorkspaceOutcomes.Unconfirmed,
                WorkspaceReason: "workspace-probe-timeout",
                CompletionBoundary: boundary)));

        var status = await GetQuerier().GetStatusAsync(work.WorkflowRunId);
        Assert.Equal("recoverable-unconfirmed", status!.Status);
        Assert.Equal("recoverable-unconfirmed", Assert.Single(Assert.Single(status.Stages).Tasks).Status);
        Assert.Null(status.Failure);
        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(run.CurrentStage().Tasks).Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Fact]
    public async Task CleanVerification_CompletesTheOriginalAttemptWithoutDuplicatingEvents()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.Dirty, ["generated.tmp"], reason: "cleanup-deferred", output: JsonSerializer.SerializeToElement(new { answer = "preserved" }));
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        var admission = await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult(
                "completed",
                Output: JsonSerializer.SerializeToElement(new { answer = "preserved" }),
                WorkspaceOutcome: WorkflowTaskWorkspaceOutcomes.Dirty,
                WorkspaceReason: "cleanup-deferred",
                CompletionBoundary: boundary));
        Assert.Equal(("accepted", "Running"), admission);

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var lease = await workflow.AcquireWorkflowTaskCleanupLeaseAsync(
            new WorkflowTaskCleanupLeaseRequest(
                "cleanup-for-verification",
                boundary.Identity,
                boundary.Fingerprint,
                CleanupScope: [],
                WorkBudget: 1));
        Assert.True(lease.Accepted);
        var verification = CleanVerification(boundary, "verification-1") with { Fence = lease.Lease!.Fence };
        var first = await workflow.ReceiveWorkspaceVerificationAsync(verification);
        Assert.Equal(ReportAck.Accepted, first);
        var eventCount = (await EventStore.ListAsync(work.WorkflowRunId)).Count;

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveWorkspaceVerificationAsync(verification));
        Assert.Equal(eventCount, (await EventStore.ListAsync(work.WorkflowRunId)).Count);

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Completed, task.Status);
        Assert.True(WorkflowTaskCompletionBoundaryRules.SameBoundary(boundary, task.CompletionBoundary!));
        Assert.True(task.WorkflowTaskRecovery!.Projection.Applied);
        Assert.Single(task.WorkflowTaskRecovery.Verifications);
        Assert.Equal("Completed", await workflow.GetRunStatusAsync());
    }

    [Fact]
    public async Task ActionFailure_RemainsBusinessFailureEvenWhenWorkspaceIsDirty()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var boundary = Boundary(
            work,
            runnerId,
            WorkflowTaskWorkspaceOutcomes.Dirty,
            ["source.cs"],
            actionOutcome: "failed",
            reason: "workspace-status-non-empty");
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();

        Assert.Equal(("accepted", "Failed"), await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult(
                "failed",
                "Action failed",
                Error: new ExecutionError("action-failed", "Action failed"),
                WorkspaceOutcome: WorkflowTaskWorkspaceOutcomes.Dirty,
                WorkspaceReason: "workspace-status-non-empty",
                CompletionBoundary: boundary)));

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Null(task.WorkflowTaskRecovery);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var types = (await EventStore.ListAsync(work.WorkflowRunId)).Select(e => e.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskFailed, types);
        Assert.Contains(EventCatalog.ReverseDns.StageFailed, types);
        Assert.Contains(EventCatalog.ReverseDns.WorkflowRunFailed, types);
    }

    [Fact]
    public async Task StopRecovery_CancelsWithoutBusinessFailureEvents()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.Dirty, ["source.cs"], reason: "operator-review-required");
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult(
                "completed",
                WorkspaceOutcome: WorkflowTaskWorkspaceOutcomes.Dirty,
                WorkspaceReason: "operator-review-required",
                CompletionBoundary: boundary));

        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        await workflow.StopAsync("operator-stop");

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.Equal(TaskRunStatus.Cancelled, Assert.Single(run.CurrentStage().Tasks).Status);
        var types = (await EventStore.ListAsync(work.WorkflowRunId)).Select(e => e.Envelope.Type).ToArray();
        Assert.Contains(EventCatalog.ReverseDns.TaskCancelled, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, types);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, types);
    }

    [Fact]
    public async Task ConflictingBoundaryReplay_IsStaleAndLeavesOriginalStateUnchanged()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.CommittedClean);
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        var original = new WorkResult("completed", CompletionBoundary: boundary);
        Assert.Equal("accepted", (await service.ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId, original)).Ack);

        var conflicting = boundary with
        {
            CommitReceipt = boundary.CommitReceipt with { ExpectedHead = "different-head" },
        };
        Assert.Equal("stale", (await service.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("completed", CompletionBoundary: conflicting))).Ack);

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.True(WorkflowTaskCompletionBoundaryRules.SameBoundary(boundary, task.CompletionBoundary!));
        Assert.Equal(TaskRunStatus.Completed, task.Status);
    }

    [Fact]
    public async Task CleanupLease_IsExclusiveReplaySafeAndExpiresByFence()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.Dirty, ["generated.tmp"], reason: "dirty");
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        await service.ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId,
            new WorkResult("completed", CompletionBoundary: boundary, WorkspaceOutcome: "dirty", WorkspaceReason: "dirty"));
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var request = new WorkflowTaskCleanupLeaseRequest("cleanup-1", boundary.Identity, boundary.Fingerprint, ["generated.tmp"], 2, TimeSpan.FromSeconds(1));
        var first = await workflow.AcquireWorkflowTaskCleanupLeaseAsync(request);
        var replay = await workflow.AcquireWorkflowTaskCleanupLeaseAsync(request);
        Assert.True(first.Accepted);
        Assert.True(replay.Replay);
        Assert.Equal(first.Lease!.Fence, replay.Lease!.Fence);

        var competing = await workflow.AcquireWorkflowTaskCleanupLeaseAsync(request with { OperationId = "cleanup-2" });
        Assert.False(competing.Accepted);
        Assert.Equal("cleanup-lease-active", competing.Reason);

        var operation = new WorkflowTaskCleanupOperation(
            "mutation-1", first.Lease.Fence, boundary.Identity, true, false, 1, ["generated.tmp"], "verification-required", _fixture.TimeProvider.GetUtcNow());
        Assert.True((await workflow.RecordWorkflowTaskCleanupAsync(operation)).Accepted);
        Assert.True((await workflow.RecordWorkflowTaskCleanupAsync(operation)).Replay);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        var renewed = await workflow.AcquireWorkflowTaskCleanupLeaseAsync(request with { OperationId = "cleanup-2" });
        Assert.True(renewed.Accepted);
        Assert.NotEqual(first.Lease.Fence, renewed.Lease!.Fence);
        var stale = await workflow.RecordWorkflowTaskCleanupAsync(operation with { OperationId = "mutation-stale" });
        Assert.False(stale.Accepted);
        Assert.Equal("cleanup-fence-stale", stale.Reason);
    }

    [Fact]
    public async Task AuthorizedSourceAdoption_IsAllowlistedAndChangedHeadVerificationSettlesOnce()
    {
        await StartWorkflowAsync(SingleStage(tasks: [new("task-1", "Task 1", "spec/task")], checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var boundary = Boundary(work, runnerId, WorkflowTaskWorkspaceOutcomes.Dirty, ["generated.tmp"], reason: "source-dirty", cleanupScope: ["generated.tmp"]);
        var service = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        await service.ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, work.TaskRunId,
            new WorkResult("completed", CompletionBoundary: boundary, WorkspaceOutcome: "dirty", WorkspaceReason: "source-dirty"));
        var workflow = Grains.GetGrain<IWorkflowGrain>(work.WorkflowRunId);
        var lease = await workflow.AcquireWorkflowTaskCleanupLeaseAsync(
            new WorkflowTaskCleanupLeaseRequest("cleanup-1", boundary.Identity, boundary.Fingerprint, ["generated.tmp"], 2));
        Assert.True(lease.Accepted);

        var rejected = await workflow.AuthorizeTaskSourceAdoptionAsync(new WorkflowTaskSourceAdoptionRequest(
            "adopt-rejected", boundary.Identity, boundary.Fingerprint, lease.Lease!.Fence,
            "operator", Authenticated: false, HasWorkflowPermission: true, SourcePaths: ["source.cs"]));
        Assert.False(rejected.Accepted);
        Assert.Equal("recovery-operator-unauthorized", rejected.Reason);

        var authorized = await workflow.AuthorizeTaskSourceAdoptionAsync(new WorkflowTaskSourceAdoptionRequest(
            "adopt-1", boundary.Identity, boundary.Fingerprint, lease.Lease.Fence,
            "operator", Authenticated: true, HasWorkflowPermission: true, SourcePaths: ["source.cs"]));
        Assert.True(authorized.Accepted);
        var completed = await workflow.RecordTaskSourceAdoptionAsync(
            authorized.Operation! with { Completed = true, ResultingHead = "adopted-head" });
        Assert.True(completed.Accepted);

        var verification = CleanVerification(boundary, "verification-adopted") with
        {
            Fence = lease.Lease.Fence,
            ObservedHead = "adopted-head",
            ObservedTree = "adopted-tree",
            SourceAdoptionOperationId = "adopt-1",
        };
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveWorkspaceVerificationAsync(verification));
        Assert.Equal("Completed", await workflow.GetRunStatusAsync());
        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveWorkspaceVerificationAsync(verification));
    }

    private static WorkflowTaskCompletionBoundary Boundary(
        WorkDispatch work,
        string runnerId,
        string outcome,
        IReadOnlyList<string>? dirtyPaths = null,
        string actionOutcome = "succeeded",
        string? reason = null,
        bool authoritative = true,
        JsonElement? output = null,
        IReadOnlyList<string>? artifactUploadIds = null,
        IReadOnlyList<string>? cleanupScope = null)
    {
        var identity = new WorkflowTaskExecutionIdentity(
            work.WorkflowRunId,
            work.Stage,
            work.TaskRunId!,
            work.WorkId,
            WorkDispatchOwnerKinds.Workflow,
            work.WorkflowRunId,
            runnerId,
            "workspace-1",
            JsonSerializer.SerializeToElement(1));
        var paths = dirtyPaths?.ToList() ?? new List<string>();
        var receipt = new CommitReceipt(
            1,
            identity,
            "test-branch",
            "test-head",
            "test-tree",
            "test-branch",
            "test-head",
            "test-tree",
            paths,
            new List<string>(),
            new List<string>(),
            authoritative,
            authoritative ? null : reason,
            DateTimeOffset.UnixEpoch);
        return new WorkflowTaskCompletionBoundary(
            1,
            identity,
            new ActionCompletion(
                1,
                ActionStarted: true,
                actionOutcome,
                "action",
                actionOutcome == "succeeded" ? output : null,
                actionOutcome == "failed" ? new ExecutionError("action-failed", "Action failed") : null,
                artifactUploadIds?.ToList() ?? new List<string>(),
                null,
                DateTimeOffset.UnixEpoch),
            receipt,
            outcome,
            reason,
            $"boundary:{work.WorkflowRunId}:{work.TaskRunId}:{outcome}:{actionOutcome}",
            cleanupScope?.ToList());
    }

    private static WorkspaceVerification CleanVerification(
        WorkflowTaskCompletionBoundary boundary,
        string key) => new(
            key,
            boundary.Identity,
            boundary.Fingerprint,
            boundary.CommitReceipt.ExpectedBranch,
            boundary.CommitReceipt.ExpectedHead,
            boundary.CommitReceipt.ExpectedTree,
            new List<string>(),
            new List<string>(),
            new List<string>(),
            true,
            null,
            "operator",
            "workspace-probe");
}
