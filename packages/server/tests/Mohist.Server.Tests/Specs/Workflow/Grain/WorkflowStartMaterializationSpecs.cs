using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

[CollectionDefinition("WorkflowStartMaterialization")]
public sealed class WorkflowStartMaterializationCollection : ICollectionFixture<WorkflowGrainFixture>;

[Collection("WorkflowStartMaterialization")]
public sealed class WorkflowStartMaterializationSpecs(WorkflowGrainFixture fixture) : WorkflowGrainSpecs(fixture)
{
    private static readonly SemaphoreSlim MaterializationTestLock = new(1, 1);

    [Fact]
    public async Task RunnerPoll_MaterializesWorkspaceBeforeReturningFirstDispatch()
    {
        await WithMaterializationTestLockAsync(async () =>
        {
            await ClearBacklogAsync();
            _fixture.RunnerWorkspace.Reset();
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var projectId = TestProjectId(workflowId);
            var runnerId = await StartWorkflowForBacklogPollAsync(workflowId, projectId, SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task")],
                checks: []));
            var work = await PollAssignedWorkAsync(runnerId, workflowId);
            var calls = await MaterializationCallsForAsync(workflowId, 1);

            var run = await LoadRunAsync(workflowId);
            Assert.NotNull(run.WorkspaceMaterializedAt);
            Assert.Equal("task-1.1", work.WorkId);
            Assert.Single(calls);
            Assert.Equal(projectId, calls[0].ProjectId);
            Assert.Equal(runnerId, calls[0].RunnerId);
            Assert.Equal(workflowId, calls[0].WorkflowRunId);
            Assert.Equal("task-1.1", calls[0].WorkId);
        });
    }

    [Fact]
    public async Task PrepareStartMaterialization_BuildsExpectedFirstDispatchPayload()
    {
        await WithMaterializationTestLockAsync(async () =>
        {
            await ClearBacklogAsync();
            _fixture.RunnerWorkspace.Reset();
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var projectId = TestProjectId(workflowId);
            var runnerId = await StartWorkflowForDirectAssignmentAsync(workflowId, projectId, SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task")],
                checks: []));
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await workflow.AssignRunnerAsync(runnerId);

            var materialization = await workflow.PrepareStartMaterializationAsync(runnerId);

            Assert.NotNull(materialization);
            Assert.Equal(workflowId, materialization!.Dispatch.WorkflowRunId);
            Assert.Equal("task-1.1", materialization.Dispatch.WorkId);
            Assert.Equal(WorkDispatchOwnerKinds.Workflow, materialization.Dispatch.OwnerKind);
        });
    }

    [Fact]
    public async Task AssignRunner_WhenStartMaterializationFails_RecordsWorkflowInfrastructureFailure()
    {
        await WithMaterializationTestLockAsync(async () =>
        {
            await ClearBacklogAsync();
            _fixture.RunnerWorkspace.Reset();
            _fixture.RunnerWorkspace.MaterializationResult = new(false, null, null, null, "clone failed");
            var workflowId = $"wf-{Guid.NewGuid():N}";
            _workflowId = workflowId;
            var projectId = TestProjectId(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task")],
                checks: []), projectId);

            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await workflow.StartAsync(TestInput(projectId));
            var runnerId = await RegisterRunnerForProjectAsync(projectId);

            await workflow.PrepareStartMaterializationAsync(runnerId);
            await workflow.RecordStartMaterializationFailureAsync(runnerId, "clone failed");
            var run = await LoadRunAsync(workflowId);
            Assert.Equal(WorkflowRunStatus.Failed, run.Status);
            Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
            Assert.Null(run.Failure?.TaskId);
            Assert.Contains("workflow workspace materialization failure", run.Failure?.Message ?? string.Empty);
            Assert.Contains("workspace-corrupt", run.Failure?.Message ?? string.Empty);
            Assert.Contains("clone failed", run.Failure?.Message ?? string.Empty);
        });
    }

    [Fact]
    public async Task RunnerPoll_MaterializesOnlyOnceAcrossMultipleDispatches()
    {
        await WithMaterializationTestLockAsync(async () =>
        {
            await ClearBacklogAsync();
            _fixture.RunnerWorkspace.Reset();
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var projectId = TestProjectId(workflowId);
            var runnerId = await StartWorkflowForBacklogPollAsync(workflowId, projectId, SingleStage(
                tasks:
                [
                    new("task-1", "Task 1", "spec/task"),
                    new("task-2", "Task 2", "spec/task")
                ],
                checks: []));

            var first = await PollAssignedWorkAsync(runnerId, workflowId);
            var calls = await MaterializationCallsForAsync(workflowId, 1);
            var firstRun = await LoadRunAsync(workflowId);
            Assert.NotNull(firstRun.WorkspaceMaterializedAt);
            var materializedAt = firstRun.WorkspaceMaterializedAt;
            Assert.Single(calls);
            Assert.Equal(projectId, calls[0].ProjectId);
            Assert.Equal(runnerId, calls[0].RunnerId);
            Assert.Equal(workflowId, calls[0].WorkflowRunId);
            Assert.Equal("task-1.1", calls[0].WorkId);

            await ReportAsync(runnerId, first, new WorkResult("completed", "ok"));
            var second = await PollAssignedWorkAsync(runnerId, workflowId);

            Assert.Equal("task-2.1", second.WorkId);
            var secondRun = await LoadRunAsync(workflowId);
            Assert.Equal(materializedAt, secondRun.WorkspaceMaterializedAt);
            var secondDispatchCalls = _fixture.RunnerWorkspace.MaterializeWorkspaceCalls
                .Where(c => c.WorkflowRunId == workflowId)
                .ToList();
            Assert.Single(secondDispatchCalls);
        });
    }

    [Fact]
    public async Task TaskRetry_ReMaterializesWorkspaceBeforeRetryDispatch()
    {
        await WithMaterializationTestLockAsync(async () =>
        {
            await ClearBacklogAsync();
            _fixture.RunnerWorkspace.Reset();
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var projectId = TestProjectId(workflowId);
            var runnerId = await StartWorkflowForDirectAssignmentAsync(workflowId, projectId, SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task")],
                checks: []));
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

            await workflow.AssignRunnerAsync(runnerId);
            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var first = await runner.PollAsync();
            Assert.NotNull(first);
            await ReportAsync(runnerId, first!, new WorkResult("failed", "missing workspace"));

            await workflow.RetryAsync();
            await workflow.AssignRunnerAsync(runnerId);
            var retry = await runner.PollAsync();

            Assert.NotNull(retry);
            Assert.Equal("task-1.2", retry!.WorkId);
            var run = await LoadRunAsync(workflowId);
            Assert.NotNull(run.WorkspaceMaterializedAt);
        });
    }

    [Fact]
    public async Task StageRerun_ReMaterializesWorkspaceBeforeRerunDispatch()
    {
        await WithMaterializationTestLockAsync(async () =>
        {
            await ClearBacklogAsync();
            _fixture.RunnerWorkspace.Reset();
            var workflowId = $"wf-{Guid.NewGuid():N}";
            var projectId = TestProjectId(workflowId);
            var runnerId = await StartWorkflowForDirectAssignmentAsync(workflowId, projectId, SingleStage(
                tasks:
                [
                    new("task-1", "Task 1", "spec/task"),
                    new("task-2", "Task 2", "spec/task")
                ],
                checks: []));
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

            await workflow.AssignRunnerAsync(runnerId);
            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var first = await PollAssignedWorkAsync(runnerId, workflowId);
            await ReportAsync(runnerId, first, new WorkResult("failed", "missing workspace"));

            await workflow.RerunAsync();
            await workflow.AssignRunnerAsync(runnerId);
            var rerun = await PollAssignedWorkAsync(runnerId, workflowId);

            Assert.Equal("task-1.1", rerun.WorkId);
            var run = await LoadRunAsync(workflowId);
            Assert.NotNull(run.WorkspaceMaterializedAt);
        });
    }

    private Task<IReadOnlyList<MaterializeWorkspaceCall>> MaterializationCallsForAsync(string workflowId, int count) =>
        _fixture.RunnerWorkspace.WaitForMaterializeWorkspaceCallsAsync(workflowId, count);

    private async Task<WorkDispatch> PollAssignedWorkAsync(string runnerId, string workflowId)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var work = await runner.PollAsync();
            if (work is not null && work.WorkflowRunId == workflowId)
                return work;
            if (work is not null)
                Assert.Fail($"Runner '{runnerId}' returned work for workflow '{work.WorkflowRunId}' while waiting for workflow '{workflowId}'");

            await Task.Delay(20);
        }

        Assert.Fail($"Runner '{runnerId}' has no work for workflow '{workflowId}'");
        return null!;
    }

    private async Task<string> StartWorkflowForBacklogPollAsync(string workflowId, string projectId, WorkflowDefinition definition)
    {
        await ClearBacklogAsync();
        _workflowId = workflowId;
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"runner-{workflowId}-{Guid.NewGuid():N}");
        _runnerId = runnerId;
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(TestInput(projectId));
        await EnqueueWorkflowForTestAsync(workflowId, projectId);
        return runnerId;
    }

    private async Task<string> StartWorkflowForDirectAssignmentAsync(string workflowId, string projectId, WorkflowDefinition definition)
    {
        await ClearBacklogAsync();
        _workflowId = workflowId;
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"runner-{workflowId}-{Guid.NewGuid():N}");
        _runnerId = runnerId;
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(TestInput(projectId));
        return runnerId;
    }

    private static async Task WithMaterializationTestLockAsync(Func<Task> action)
    {
        await MaterializationTestLock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            MaterializationTestLock.Release();
        }
    }
}
