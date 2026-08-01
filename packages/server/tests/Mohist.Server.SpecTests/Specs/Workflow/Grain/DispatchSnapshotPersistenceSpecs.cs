using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class DispatchSnapshotPersistenceSpecs : WorkflowGrainSpecs
{
    public DispatchSnapshotPersistenceSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private IDispatchSnapshotStore Store(IServiceProvider services) =>
        services.GetRequiredService<IDispatchSnapshotStore>();

    private static async Task<WorkDispatch?> LoadAsync(IDispatchSnapshotStore store, string runId, string workId)
    {
        var json = await store.LoadJsonAsync(runId, workId);
        return json is null ? null : JSON.Deserialize<WorkDispatch>(json);
    }

    private static async Task<WorkDispatch> SaveFirstAsync(
        IDispatchSnapshotStore store, string runId, string workId, WorkDispatch dispatch)
    {
        var json = await store.SaveFirstJsonAsync(runId, workId, JSON.Serialize(dispatch));
        return JSON.Deserialize<WorkDispatch>(json)!;
    }

    [Fact]
    public async Task PersistedTaskRun_StateHasNoDispatchSnapshot_SnapshotInSeparateStore()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (dispatch, _) = await PollWorkAnyAsync();

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var stored = await LoadAsync(Store(scope.ServiceProvider), _workflowId!, dispatch.WorkId);
        Assert.Equal(dispatch, stored);

        var run = await LoadRunAsync(_workflowId!);
        var task = Assert.Single(run.CurrentStage().Tasks);
        using var doc = JsonDocument.Parse(JSON.Serialize(task));
        Assert.False(doc.RootElement.TryGetProperty("dispatchSnapshot", out _),
            "TaskRun must not embed a dispatchSnapshot after dispatch");
        Assert.False(doc.RootElement.TryGetProperty("workDispatch", out _),
            "TaskRun must not embed a WorkDispatch-like payload after dispatch");
    }

    [Fact]
    public async Task Redelivery_AfterGrainDeactivation_ReturnsStoredSnapshotVerbatim()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (first, _) = await PollWorkAnyAsync();

        await DeactivateWorkflowAsync(_workflowId!);

        var redelivery = Assert.Single((await Dispatch().PollAsync(_runnerId!, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(first, redelivery);
    }

    [Fact]
    public async Task SaveFirstAsync_SecondCallForSameAttempt_ReturnsFirstStoredUnchanged()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (first, _) = await PollWorkAnyAsync();

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);

        var second = await SaveFirstAsync(store, _workflowId!, first.WorkId,
            first with { Uses = "spec/second" });

        Assert.Equal(first, second);
        var loaded = await LoadAsync(store, _workflowId!, first.WorkId);
        Assert.Equal(first, loaded);
    }

    [Fact]
    public async Task CompletedTask_LosesSnapshot()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, task.WorkId, "completed");

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);
        Assert.Null(await LoadAsync(store, _workflowId!, task.WorkId));
    }

    [Fact]
    public async Task FailedTask_LosesSnapshot()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, task.WorkId, "failed", "expected");

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);
        Assert.Null(await LoadAsync(store, _workflowId!, task.WorkId));
    }

    [Fact]
    public async Task Retry_AfterFailure_OnlyNewAttemptSnapshotExists()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (first, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, first.WorkId, "failed", "expected");

        await workflow.RetryAsync();

        var (second, _) = await PollWorkAnyAsync();
        Assert.NotEqual(first.WorkId, second.WorkId);

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);
        Assert.Null(await LoadAsync(store, _workflowId!, first.WorkId));
        Assert.NotNull(await LoadAsync(store, _workflowId!, second.WorkId));
    }

    [Fact]
    public async Task StoppedRun_LosesSnapshot()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: []));

        var (task, _) = await PollWorkAnyAsync();
        await workflow.StopAsync("test stop");

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);
        Assert.Null(await LoadAsync(store, _workflowId!, task.WorkId));
    }

    [Fact]
    public async Task ChecksDispatch_DoesNotPersistSnapshotAndRedeliveryReconstructs()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, task.WorkId, "completed");

        var checksList = await PollWorkAnyAsync();

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);
        Assert.Null(await LoadAsync(store, _workflowId!, checksList.Work.WorkId));

        var checksWork = checksList.Work;
        await DeactivateWorkflowAsync(_workflowId!);

        var redelivery = Assert.Single((await Dispatch().PollAsync(_runnerId!, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(checksWork.OwnerKind, redelivery.OwnerKind);
        Assert.Equal(checksWork.WorkId, redelivery.WorkId);
        Assert.Equal(checksWork.WorkflowRunId, redelivery.WorkflowRunId);
    }

    [Fact]
    public async Task WorkflowRunStore_DeleteAsync_CascadeDeletesSnapshots()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task, _) = await PollWorkAnyAsync();

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            await store.DeleteAsync(_workflowId!);
        }

        await using (var verifyScope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var store = Store(verifyScope.ServiceProvider);
            Assert.Null(await LoadAsync(store, _workflowId!, task.WorkId));
        }
    }

    private DispatchService Dispatch() =>
        _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<DispatchService>();
}
