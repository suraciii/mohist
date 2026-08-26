using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Claim, assignment, and stop-durability decisions of the workflow run,
/// driven through the real grain without a cluster. These migrate the
/// state-decision scenarios from the SpecTests WorkflowStateSpecs; the
/// poll-routing scenarios stay on the cluster as representative dispatch
/// proofs (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainClaimAssignmentSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainClaimAssignmentSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextAsync_ClaimsThePendingTaskAndTransitionsItToRunning()
    {
        var (grain, store, events, runId, workerId, projectId) = await ArrangeAsync(
            "wr-claim-transitions", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");

        Assert.NotNull(claimed);
        Assert.Equal(WorkItemTypes.Task, claimed!.WorkType);
        var run = await RequireRunAsync(store, runId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(workerId, task.WorkerId);
        Assert.Equal(claimed.Id, task.WorkId);
    }

    [Fact]
    public async Task ClaimNextAsync_ReturnsNull_WhenTaskAlreadyRunning()
    {
        var (grain, _, _, runId, workerId, _) = await ArrangeAsync("wr-claim-reentry", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");
        Assert.NotNull(claimed);

        Assert.Null(await grain.ClaimNextAsync(workerId, "test-generation"));
    }

    [Fact]
    public async Task ActiveTask_PreservesOwnership_BlocksDuplicateDispatch()
    {
        var (grain, _, _, runId, workerId, _) = await ArrangeAsync("wr-claim-ownership", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");
        Assert.NotNull(claimed);

        var duplicate = await grain.AssignWorkerAsync("different-runner");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, duplicate.Status);
        Assert.Equal("already-assigned", duplicate.Reason);
        Assert.Equal(workerId, await grain.GetAssignedWorkerIdAsync());
    }

    [Fact]
    public async Task ActiveTask_DifferentRunnerAssign_DoesNotOverwriteExistingWork()
    {
        var (grain, _, _, runId, workerId, _) = await ArrangeAsync("wr-claim-overwrite", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");
        Assert.NotNull(claimed);

        var firstAttempt = await grain.AssignWorkerAsync("other-runner");
        var secondAttempt = await grain.AssignWorkerAsync("other-runner");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, firstAttempt.Status);
        Assert.Equal("already-assigned", firstAttempt.Reason);
        Assert.Equal(WorkflowAssignmentStatus.Rejected, secondAttempt.Status);
        Assert.Equal("already-assigned", secondAttempt.Reason);
        Assert.Equal(workerId, await grain.GetAssignedWorkerIdAsync());
        Assert.Equal(claimed!.Id, await grain.GetCurrentWorkIdAsync());
    }

    [Fact]
    public async Task ActiveTask_SameOwnerReassign_DoesNotCreateDuplicateAssignment()
    {
        var (grain, _, _, runId, workerId, _) = await ArrangeAsync("wr-claim-same-owner", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");
        Assert.NotNull(claimed);

        var firstAttempt = await grain.AssignWorkerAsync(workerId);
        var secondAttempt = await grain.AssignWorkerAsync(workerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, firstAttempt.Status);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, secondAttempt.Status);
        Assert.Equal(workerId, await grain.GetAssignedWorkerIdAsync());
        Assert.Equal(claimed!.Id, await grain.GetCurrentWorkIdAsync());
        // The re-offered poll observes the same running task, not a second
        // dispatchable item.
        Assert.Null(await grain.ClaimNextAsync(workerId, "test-generation"));
    }

    [Fact]
    public async Task TaskDelivery_IsCompletedWhenRunnerReports()
    {
        var (grain, store, _, runId, workerId, projectId) = await ArrangeAsync(
            "wr-claim-report", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");

        var running = await RequireRunAsync(store, runId);
        var runningTask = running.CurrentStage().RunningTask;
        Assert.NotNull(runningTask);
        Assert.Equal(TaskRunStatus.Running, runningTask!.Status);
        Assert.Equal(claimed!.Id, runningTask.WorkId);

        var acknowledgement = await grain.ReceiveTaskReportAsync(
            workerId,
            claimed.Id!,
            new TaskReport(claimed.Id!, TaskReportStatus.Succeeded, Output: null, Artifacts: null, TaskRunId: runningTask.Id));
        Assert.Equal(WorkReportVerdict.Accepted, acknowledgement);

        var completed = await RequireRunAsync(store, runId);
        Assert.Null(completed.CurrentStage().RunningTask);
        Assert.Equal(TaskRunStatus.Completed, completed.CurrentStage().Tasks.Single().Status);
    }

    [Fact]
    public async Task WorkflowTaskStarted_IsRecordedAfterRunningTaskIsPersisted()
    {
        var (grain, store, _, runId, workerId, _) = await ArrangeAsync("wr-claim-started-record", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        var claimed = await grain.ClaimNextAsync(workerId, "test-generation");

        var persisted = await RequireRunAsync(store, runId);
        Assert.Equal(TaskRunStatus.Running, persisted.CurrentStage().RunningTask!.Status);
        Assert.Equal(claimed!.Id, await grain.GetCurrentWorkIdAsync());
        Assert.Equal(workerId, await grain.GetAssignedWorkerIdAsync());
    }

    [Fact]
    public async Task StoppedAssignedWorkflow_RequestWorkRejectsAsNotRunnable()
    {
        var (grain, _, _, runId, workerId, projectId) = await ArrangeAsync("wr-claim-stopped", SingleStage());

        await grain.AssignWorkerAsync(workerId);
        await grain.StopAsync("test-stop");

        var request = await grain.AssignWorkerAsync(workerId);
        Assert.Equal(WorkflowAssignmentStatus.Rejected, request.Status);
        Assert.Equal("not-runnable", request.Reason);
        Assert.Null(await grain.ClaimNextAsync(workerId, "test-generation"));
    }

    [Fact]
    public async Task StopAsync_StopEventSaveFailure_DoesNotPersistStoppedStateWithoutEvent()
    {
        const string runId = "wr-stop-event-failure";
        const string projectId = "proj-stop-event-failure";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var innerStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var failing = new WorkflowGrainContractSupport.SelectiveFailingStore(
            innerStore,
            e => e is WorkflowRunStopped);
        var grain = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, failing, runId, TimeProvider);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        var before = await RequireRunAsync(innerStore, runId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StopAsync("event-store-down"));

        // A fresh activation reads only durable facts: the stopped transition
        // never happened because its event batch failed to commit.
        var reactivated = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, failing, runId, TimeProvider);
        await reactivated.OnActivateAsync(CancellationToken.None);
        var after = await RequireRunAsync(innerStore, runId);
        Assert.Equal(before.Status, after.Status);
        Assert.NotEqual(WorkflowRunStatus.Stopped, after.Status);
        Assert.DoesNotContain(
            await events.ListAsync(runId),
            e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStopped);
    }

    [Fact]
    public async Task StartAsync_StartEventSaveFailure_KeepsCreatedBindingWithoutStartedState()
    {
        const string runId = "wr-start-event-failure";
        const string projectId = "proj-start-event-failure";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var innerStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var failing = new WorkflowGrainContractSupport.SelectiveFailingStore(
            innerStore,
            e => e is WorkflowRunStarted);
        var grain = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, failing, runId, TimeProvider);
        await grain.OnActivateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.StartAsync(TestInput(projectId)));

        var reactivated = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, failing, runId, TimeProvider);
        await reactivated.OnActivateAsync(CancellationToken.None);
        var persisted = await innerStore.LoadAsync(runId);
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowRunStatus.Created, persisted!.Status);
        Assert.DoesNotContain(
            await events.ListAsync(runId),
            e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStarted);
    }

    [Fact]
    public async Task StopAsync_AfterCommit_ReadbackKeepsStoppedEventAndState()
    {
        const string runId = "wr-stop-commit";
        const string projectId = "proj-stop-commit";
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var grain = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, store, runId, TimeProvider);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));

        await grain.StopAsync("user-stop");

        var reactivated = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, store, runId, TimeProvider);
        await reactivated.OnActivateAsync(CancellationToken.None);
        Assert.Equal(WorkflowRunStatus.Stopped, (await RequireRunAsync(store, runId)).Status);
        Assert.Contains(
            await events.ListAsync(runId),
            e => e.Envelope.Type == EventCatalog.ReverseDns.WorkflowRunStopped);
    }

    /// <summary>
    /// Seeds a single-stage template, activates a fresh grain, and starts the
    /// run into Pending. Every scenario owns unique run/project ids because
    /// the MohistDb fixture database persists across the whole collection.
    /// </summary>
    private async Task<(WorkflowGrain Grain, IWorkflowRunStore Store, IEventStore Events, string RunId, string WorkerId, string ProjectId)>
        ArrangeAsync(string runId, WorkflowDefinition? definition = null, string? projectId = null)
    {
        var resolvedProject = projectId ?? $"prof-{Math.Abs(WorkflowYamlSerializer.ToYaml(definition ?? SingleStage()).GetHashCode()):x8}";
        await WorkflowGrainContractSupport.SeedTemplateAsync(
            _fixture,
            resolvedProject,
            definition ?? SingleStage(),
            FixedTime);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var grain = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, store, runId, TimeProvider);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(resolvedProject, 1, null));
        return (grain, store, scope.ServiceProvider.GetRequiredService<IEventStore>(), runId, "worker-1", resolvedProject);
    }

    private static WorkflowStartInput TestInput(string projectId) =>
        new(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: FixedTime,
            ProjectId: projectId,
            IssueNumber: 1));

    private static WorkflowDefinition SingleStage() => new(
    [
        new StageDefinition("build", [new("task-1", "Task 1", "spec/task")], []),
    ]);

    private static async Task<WorkflowRun> RequireRunAsync(IWorkflowRunStore store, string runId) =>
        await store.LoadAsync(runId) ?? throw new InvalidOperationException($"run '{runId}' missing");
}
