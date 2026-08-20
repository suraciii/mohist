using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public sealed partial class WorkflowGrainStateSaveFailureSpecs
{
    [Theory]
    [InlineData("snapshot")]
    [InlineData("reminder")]
    [InlineData("stage-lock")]
    public async Task ExplicitStop_PostCommitCleanupFailureIsRepairedByReplayAndActivation(string boundary)
    {
        var workflowRunId = $"wr-settlement-cleanup-{boundary}";
        var projectId = $"proj-settlement-cleanup-{boundary}";
        var workerId = $"worker-settlement-cleanup-{boundary}";
        var calls = new ReminderCalls();

        await SeedWorkflowTemplateAsync(projectId, AgentWorkflowDefinition());
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var failing = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await failing.OnActivateAsync(CancellationToken.None);
        var binding = await StartAgentWorkAsync(failing, store, workflowRunId, projectId, workerId);
        await failing.ObserveAgentExecutionAsync(new AgentExecutionObservation(
            binding, AgentExecutionObservationKind.StopUnconfirmed, "stop-unconfirmed"));

        await snapshots.SaveFirstJsonAsync(workflowRunId, binding.WorkId, "{}");
        FailNextCleanupBoundary(calls, boundary);
        var beforeInitialStop = CleanupAttempts(calls, boundary);
        await StopWithExpectedCleanupFailureAsync(failing, boundary);

        Assert.Equal(beforeInitialStop + 1, CleanupAttempts(calls, boundary));
        await AssertStoppedWithoutFailureAsync(store, events, workflowRunId);
        if (boundary == "snapshot")
            Assert.NotNull(await snapshots.LoadJsonAsync(workflowRunId, binding.WorkId));

        await failing.StopAsync("cleanup replay");
        await AssertStoppedWithoutFailureAsync(store, events, workflowRunId);
        if (boundary == "snapshot")
            Assert.Null(await snapshots.LoadJsonAsync(workflowRunId, binding.WorkId));

        if (boundary == "snapshot")
            await snapshots.SaveFirstJsonAsync(workflowRunId, binding.WorkId, "{}");
        FailNextCleanupBoundary(calls, boundary);
        var beforeActivation = CleanupAttempts(calls, boundary);
        await StopWithExpectedCleanupFailureAsync(failing, boundary);

        Assert.Equal(beforeActivation + 1, CleanupAttempts(calls, boundary));
        await AssertStoppedWithoutFailureAsync(store, events, workflowRunId);

        var recovered = CreateReminderGrain(scope.ServiceProvider, store, workflowRunId, calls);
        await recovered.OnActivateAsync(CancellationToken.None);

        Assert.Equal(beforeActivation + 2, CleanupAttempts(calls, boundary));
        await AssertStoppedWithoutFailureAsync(store, events, workflowRunId);
        if (boundary == "snapshot")
            Assert.Null(await snapshots.LoadJsonAsync(workflowRunId, binding.WorkId));
    }

    private static Task StopWithExpectedCleanupFailureAsync(WorkflowGrain grain, string boundary) =>
        Assert.ThrowsAsync<InvalidOperationException>(() => grain.StopAsync("operator stop"));

    private static void FailNextCleanupBoundary(ReminderCalls calls, string boundary)
    {
        switch (boundary)
        {
            case "snapshot":
                calls.FailNextSnapshotDelete = true;
                break;
            case "reminder":
                calls.FailNextRemove = true;
                break;
            case "stage-lock":
                calls.FailNextLockRelease = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary));
        }
    }

    private static int CleanupAttempts(ReminderCalls calls, string boundary) => boundary switch
    {
        "snapshot" => calls.SnapshotDeleteAttempts,
        "reminder" => calls.RemoveAttempts,
        "stage-lock" => calls.LockReleaseAttempts,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary))
    };

    private static async Task AssertStoppedWithoutFailureAsync(
        IWorkflowRunStore store,
        IEventStore events,
        string workflowRunId)
    {
        var stopped = Assert.IsType<WorkflowRun>(await store.LoadAsync(workflowRunId));
        Assert.Equal(WorkflowRunStatus.Stopped, stopped.Status);
        Assert.Equal(TaskRunStatus.Cancelled, Assert.Single(stopped.CurrentStage().Tasks).Status);
        Assert.Null(stopped.Failure);
        Assert.Null(stopped.CurrentStage().Failure);
        var eventTypes = (await events.ListAsync(workflowRunId)).Select(entry => entry.Envelope.Type).ToArray();
        Assert.DoesNotContain(EventCatalog.ReverseDns.TaskFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.StageFailed, eventTypes);
        Assert.DoesNotContain(EventCatalog.ReverseDns.WorkflowRunFailed, eventTypes);
    }
}
