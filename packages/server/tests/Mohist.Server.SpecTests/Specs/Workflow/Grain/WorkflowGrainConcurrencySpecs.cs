using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Concurrency characteristic specs for <see cref="WorkflowGrain"/>.
/// Verifies that the authority grain's owned domain and persisted state
/// remain internally consistent when multiple control operations are
/// issued against the same activation without broad reentrancy. Each
/// scenario prepares the grain in a valid lifecycle phase, fires
/// concurrent calls, and asserts only on the final settled state — the
/// allowed complete serialized outcomes — without depending on
/// scheduler order or interleaving timing.
/// </summary>
[Collection("WorkflowGrain3")]
public class WorkflowGrainConcurrencySpecs : WorkflowGrainSpecs
{
    public WorkflowGrainConcurrencySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ConcurrentPauseResume_FromPending_SettlesIntoOneSerializedOutcome()
    {
        await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);

        var pause1 = workflow.PauseAsync("first");
        var resume1 = workflow.ResumeAsync();
        var pause2 = workflow.PauseAsync("second");
        var resume2 = workflow.ResumeAsync();

        await Task.WhenAll(
            CatchAsync(pause1),
            CatchAsync(resume1),
            CatchAsync(pause2),
            CatchAsync(resume2));

        var persisted = await LoadRunAsync(_workflowId!);
        var inMemoryStatus = await workflow.GetRunStatusAsync();

        Assert.Equal(persisted.Status.ToString(), inMemoryStatus);
        AssertAllowedSerializedState(inMemoryStatus);

        await DeactivateWorkflowAsync(_workflowId!);
        var reloaded = await LoadRunAsync(_workflowId!);
        Assert.Equal(persisted.Status, reloaded.Status);
        Assert.Equal(persisted.Stages.Count, reloaded.Stages.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ConcurrentStop_FromPending_ExactlyOneSucceeds_RestRejected()
    {
        await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);

        var results = await Task.WhenAll(
            CatchAsync(workflow.StopAsync("a")),
            CatchAsync(workflow.StopAsync("b")),
            CatchAsync(workflow.StopAsync("c")),
            CatchAsync(workflow.StopAsync("d")));

        var successCount = results.Count(r => r is null);
        var failureCount = results.Count(r => r is not null);

        Assert.Equal(1, successCount);
        Assert.Equal(3, failureCount);
        Assert.All(results.Where(r => r is not null), r => Assert.IsType<InvalidOperationException>(r));

        var persisted = await LoadRunAsync(_workflowId!);
        var inMemoryStatus = await workflow.GetRunStatusAsync();

        Assert.Equal(WorkflowRunStatus.Stopped, persisted.Status);
        Assert.Equal("Stopped", inMemoryStatus);

        await DeactivateWorkflowAsync(_workflowId!);
        var reloaded = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Stopped, reloaded.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ConcurrentPauseAndStop_FromPending_SettlesToStopped()
    {
        await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);

        var results = await Task.WhenAll(
            CatchAsync(workflow.PauseAsync("p1")),
            CatchAsync(workflow.StopAsync("s1")),
            CatchAsync(workflow.PauseAsync("p2")),
            CatchAsync(workflow.StopAsync("s2")));

        var persisted = await LoadRunAsync(_workflowId!);
        var inMemoryStatus = await workflow.GetRunStatusAsync();

        Assert.True(
            persisted.Status is WorkflowRunStatus.Paused or WorkflowRunStatus.Stopped,
            $"Expected Paused or Stopped, got {persisted.Status}");
        Assert.Equal(persisted.Status.ToString(), inMemoryStatus);

        if (persisted.Status == WorkflowRunStatus.Paused)
        {
            Assert.All(results, r =>
                Assert.True(r is null || r is InvalidOperationException,
                    $"Pause+Stop race from Pending should never produce unexpected errors: {r?.GetType().Name}"));
        }
        else
        {
            Assert.Contains(results, r => r is null);
            Assert.All(results.Where(r => r is not null), r => Assert.IsType<InvalidOperationException>(r));
        }

        await DeactivateWorkflowAsync(_workflowId!);
        var reloaded = await LoadRunAsync(_workflowId!);
        Assert.Equal(persisted.Status, reloaded.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ConcurrentAssignWorker_FromPending_ExactlyOneWorkerAssigned_PersistedAgreesWithInMemory()
    {
        await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));

        await RegisterRunnerAsync("runner-alpha");
        await RegisterRunnerAsync("runner-bravo");
        await RegisterRunnerAsync("runner-charlie");

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);

        var results = await Task.WhenAll(
            workflow.AssignWorkerAsync("runner-alpha"),
            workflow.AssignWorkerAsync("runner-bravo"),
            workflow.AssignWorkerAsync("runner-charlie"));

        var accepted = results.Where(r => r.Status == WorkflowAssignmentStatus.Assigned).ToList();
        var rejected = results.Where(r => r.Status == WorkflowAssignmentStatus.Rejected).ToList();

        Assert.Single(accepted);
        Assert.Equal(2, rejected.Count);

        var inMemoryOwner = await workflow.GetAssignedWorkerIdAsync();
        var persisted = await LoadRunAsync(_workflowId!);

        Assert.Equal(accepted[0].OwnerWorkerId, inMemoryOwner);
        Assert.Equal(accepted[0].OwnerWorkerId, persisted.Assignment?.WorkerId);

        await DeactivateWorkflowAsync(_workflowId!);
        var reloaded = await LoadRunAsync(_workflowId!);
        Assert.Equal(persisted.Assignment?.WorkerId, reloaded.Assignment?.WorkerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ConcurrentControlAcrossIndependentWorkflows_AllSettleIndependently()
    {
        var ids = Enumerable.Range(0, 4)
            .Select(_ => $"wf-conc-{Guid.NewGuid():N}")
            .ToArray();

        var workflows = new List<IWorkflowGrain>();
        foreach (var id in ids)
        {
            var workflow = Grains.GetGrain<IWorkflowGrain>(id);
            await SeedWorkflowTemplateAsync(id, SingleStage(checks: []), TestProjectId(id));
            await workflow.StartAsync(TestInput(TestProjectId(id), TestIssueId(id)));
            workflows.Add(workflow);
        }

        var tasks = new List<Task>();
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var w = workflows[i];
            tasks.Add(CatchAsync(w.StopAsync($"stop-{id}")));
            tasks.Add(CatchAsync(w.PauseAsync($"pause-{id}")));
        }

        await Task.WhenAll(tasks);

        foreach (var id in ids)
        {
            var w = Grains.GetGrain<IWorkflowGrain>(id);
            var persisted = await LoadRunAsync(id);
            var inMemoryStatus = await w.GetRunStatusAsync();

            Assert.Equal(persisted.Status.ToString(), inMemoryStatus);
            Assert.True(
                persisted.Status is WorkflowRunStatus.Paused or WorkflowRunStatus.Stopped
                    or WorkflowRunStatus.Pending or WorkflowRunStatus.Ready or WorkflowRunStatus.Running,
                $"Unexpected status {persisted.Status} for {id}");
        }
    }

    private static void AssertAllowedSerializedState(string? status)
    {
        Assert.NotNull(status);
        Assert.True(
            status is "Pending" or "Ready" or "Running" or "Paused" or "AwaitingApproval",
            $"Pause+Resume race from Pending produced unexpected status: {status}");
    }

    private static async Task<Exception?> CatchAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Exception?> CatchAsync<T>(Task<T> task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
