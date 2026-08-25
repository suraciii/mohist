using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Dispatch snapshot persistence semantics on the real grain without a
/// cluster: snapshots live outside the run aggregate, SaveFirst keeps the
/// first attempt immutable, terminal transitions delete them, and run
/// deletion cascades (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainDispatchSnapshotSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainDispatchSnapshotSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PersistedTaskRun_StateHasNoDispatchSnapshot_SnapshotInSeparateStore()
    {
        var a = await ArrangeAsync("wr-snap-separate");
        var dispatch = await a.StoreSnapshotAsync();

        var stored = await LoadAsync(a.Snapshots, a.RunId, a.WorkId!);
        Assert.Equal(dispatch, stored);

        var run = await a.LoadRunAsync();
        var task = Assert.Single(run.CurrentStage().Tasks);
        using var doc = JsonDocument.Parse(JSON.Serialize(task));
        Assert.False(doc.RootElement.TryGetProperty("dispatchSnapshot", out _),
            "TaskRun must not embed a dispatchSnapshot after dispatch");
        Assert.False(doc.RootElement.TryGetProperty("workDispatch", out _),
            "TaskRun must not embed a WorkDispatch-like payload after dispatch");
    }

    [Fact]
    public async Task SaveFirstAsync_SecondCallForSameAttempt_ReturnsFirstStoredUnchanged()
    {
        var a = await ArrangeAsync("wr-snap-savefirst");
        await a.StoreSnapshotAsync();

        var rewritten = JSON.Serialize(
            a.Dispatch with { Uses = "spec/second" });
        var second = JSON.Deserialize<WorkDispatch>(
            await a.Snapshots.SaveFirstJsonAsync(a.RunId, a.WorkId!, rewritten));

        Assert.Equal(a.Dispatch, second);
        var loaded = await LoadAsync(a.Snapshots, a.RunId, a.WorkId!);
        Assert.Equal(a.Dispatch, loaded);
    }

    [Fact]
    public async Task CompletedTask_LosesSnapshot()
    {
        var a = await ArrangeAsync("wr-snap-completed");
        await a.StoreSnapshotAsync();

        await a.ReportCompletedAsync();

        Assert.Null(await LoadAsync(a.Snapshots, a.RunId, a.WorkId!));
    }

    [Fact]
    public async Task FailedTask_LosesSnapshot()
    {
        var a = await ArrangeAsync("wr-snap-failed");
        await a.StoreSnapshotAsync();

        await a.ReportFailedAsync("expected");

        Assert.Null(await LoadAsync(a.Snapshots, a.RunId, a.WorkId!));
    }

    [Fact]
    public async Task Retry_AfterFailure_OnlyNewAttemptSnapshotExists()
    {
        var a = await ArrangeAsync("wr-snap-retry");
        await a.StoreSnapshotAsync();
        await a.ReportFailedAsync("expected");

        await a.Arrangement.Grain.RetryAsync();
        var second = await a.Arrangement.AssignAndClaimAsync();
        Assert.NotNull(second);
        Assert.NotEqual(a.WorkId, second!.Id);

        // The replacement attempt owns its own snapshot once dispatched.
        var secondDispatch = a.Dispatch with
        {
            WorkId = second.Id!,
            TaskRunId = await a.RunningTaskRunIdAsync(),
        };
        await a.Arrangement.Grain.StoreActiveWorkDispatchAsync(
            a.WorkerId, second.Id!, secondDispatch);

        Assert.Null(await LoadAsync(a.Snapshots, a.RunId, a.WorkId!));
        Assert.NotNull(await LoadAsync(a.Snapshots, a.RunId, second.Id!));
    }

    [Fact]
    public async Task StoppedRun_LosesSnapshot()
    {
        var a = await ArrangeAsync("wr-snap-stopped", checks: []);
        await a.StoreSnapshotAsync();

        await a.Arrangement.Grain.StopAsync("test stop");

        Assert.Null(await LoadAsync(a.Snapshots, a.RunId, a.WorkId!));
    }

    [Fact]
    public async Task WorkflowRunStore_DeleteAsync_CascadeDeletesSnapshots()
    {
        var a = await ArrangeAsync("wr-snap-cascade");
        await a.StoreSnapshotAsync();

        await a.Arrangement.Store.DeleteAsync(a.RunId);

        Assert.Null(await LoadAsync(a.Snapshots, a.RunId, a.WorkId!));
    }

    private static async Task<WorkDispatch?> LoadAsync(IDispatchSnapshotStore store, string runId, string workId)
    {
        var json = await store.LoadJsonAsync(runId, workId);
        return json is null ? null : JSON.Deserialize<WorkDispatch>(json);
    }

    private async Task<SnapshotArrangement> ArrangeAsync(string runId, params CheckDefinition[] checks)
    {
        var definition = SingleStage([new TaskDefinition("task-1", "Task 1", "spec/task")], checks);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, definition, TimeProvider, workerId: $"runner-{runId}");
        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);
        return new SnapshotArrangement(arrangement, work!);
    }

    private static WorkflowDefinition SingleStage(List<TaskDefinition> tasks, CheckDefinition[] checks) => new(
    [
        new StageDefinition("build", tasks, checks),
    ]);

    private sealed record SnapshotArrangement(
        WorkflowGrainArrangement Arrangement,
        WorkItem Work)
    {
        public WorkflowGrain Grain => Arrangement.Grain;
        public IDispatchSnapshotStore Snapshots => Arrangement.Snapshots;
        public string RunId => Arrangement.RunId;
        public string WorkerId => Arrangement.WorkerId;
        public string? WorkId => Work.Id;

        public WorkDispatch Dispatch => new(RunId, WorkId!, Uses: Work.Uses);

        /// <summary>Persists this attempt's snapshot through the grain.</summary>
        public async Task<WorkDispatch> StoreSnapshotAsync() =>
            (await Grain.StoreActiveWorkDispatchAsync(WorkerId, WorkId!, Dispatch))!;

        public async Task ReportCompletedAsync()
        {
            var taskRunId = await RunningTaskRunIdAsync();
            await Grain.ReceiveTaskReportAsync(
                WorkerId,
                WorkId!,
                new TaskReport(WorkId!, TaskReportStatus.Succeeded, Output: null, Artifacts: null, TaskRunId: taskRunId));
        }

        public async Task ReportFailedAsync(string detail)
        {
            var taskRunId = await RunningTaskRunIdAsync();
            await Grain.ReceiveTaskReportAsync(
                WorkerId,
                WorkId!,
                new TaskReport(WorkId!, TaskReportStatus.Failed, Output: null, Artifacts: null, Detail: detail, TaskRunId: taskRunId));
        }

        public async Task<string> RunningTaskRunIdAsync()
        {
            var run = await LoadRunAsync();
            return run.CurrentStage().RunningTask?.Id
                ?? throw new InvalidOperationException("no running task");
        }

        public async Task<WorkflowRun> LoadRunAsync() =>
            await Arrangement.Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
    }
}
