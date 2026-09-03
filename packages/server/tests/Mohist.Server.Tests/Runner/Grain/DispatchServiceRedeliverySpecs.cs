using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

public partial class DispatchServiceReconciliationSpecs
{
    [Fact]
    public async Task Redelivery_UsesPersistedDispatchSnapshotAfterGrainActivation()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();
        var storedJson = await snapshotStore.LoadJsonAsync(_workflowId!, first.WorkId);
        Assert.Equal(first, JSON.Deserialize<WorkDispatch>(storedJson!));

        await TestLifecycle.Deactivate(workflow);
        var redelivery = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Equal(first, redelivery);
    }

    [Fact]
    public async Task Redelivery_RedeliversRunningWork_WhenProcessDoesNotReportIt()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var workId = first!.WorkId;

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        var redelivery = Assert.Single(resp.Dispatches);
        Assert.Equal(_workflowId, redelivery.WorkflowRunId);
        Assert.Equal(workId, redelivery.WorkId);
    }

    [Fact]
    public async Task Reconnect_DoesNotRedeliverWorkClosedWithTheLostGeneration()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var first = Assert.Single(
            (await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        await runner.UnregisterAsync();
        await runner.RegisterAsync(
            new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(_workflowId!)),
            "replacement-generation");

        Assert.Empty((await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: "replacement-generation"))).Dispatches);
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());

        Assert.Equal(WorkReportVerdict.Refused, await workflow.ReceiveTaskReportAsync(
            runnerId,
            first.WorkId,
            new TaskReport(
                first.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                ActionAttemptId: first.ActionAttemptId)));
        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: "replacement-generation"))).Dispatches);
    }

    [Fact]
    public async Task Reconnect_DoesNotTakeInterruptedWorkflowOverFromRecordedRunner()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var originalRunnerId = _runnerId!;
        var originalRunner = Grains.GetGrain<IRunnerGrain>(originalRunnerId);
        var first = Assert.Single(
            (await Dispatch.PollAsync(originalRunnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        await originalRunner.UnregisterAsync();

        var otherRunnerId = $"other-recovery-runner-{Guid.NewGuid():N}";
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(
            otherRunnerId,
            ["spec/*"],
            "other-host",
            TestProjectId(_workflowId!)));

        Assert.Empty((await Dispatch.PollAsync(otherRunnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(originalRunnerId, run.Assignment?.WorkerId);
        Assert.Equal(first.WorkId, run.CurrentStage().Tasks.Single().WorkId);

        await otherRunner.UnregisterAsync();
    }


    [Fact]
    public async Task Dispatch_MissingAgent_PersistsAgentNotFoundOnWorkflowActionAttemptAndFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "reviewer",
                    "Use Agent reviewer",
                    "mohist/agent",
                    With("""{"name":"reviewer","prompt":"Review the change."}"""),
                    Recovery: new RecoveryDefinition(
                        1,
                        [new RecoveryHandlerDefinition("failure.error.code=agent_not_found", [], RetrySelf: true)])),
            ],
            checks: [],
            stage: "build"));
        var runnerId = _runnerId!;

        var assignment = await workflow.AssignWorkerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, assignment.Status);
        var claimed = await workflow.ClaimNextAsync(runnerId, "test-generation");
        Assert.Null(claimed);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, task.Status);
        Assert.Equal("agent_not_found", task.Error?.Code);
        Assert.Equal("agent_not_found", run.Failure?.Error?.Code);
        Assert.Contains("reviewer", run.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_ArchivedAgent_PersistsAgentNotFoundOnWorkflowActionAttemptAndFailure()
    {
        var projectId = TestProjectId(_workflowId ?? $"wf-{Guid.NewGuid():N}");
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "reviewer",
                    "Use Agent reviewer",
                    "mohist/agent",
                    With("""{"name":"reviewer","prompt":"Review the change."}""")),
            ],
            checks: [],
            stage: "build"));
        var runnerId = _runnerId!;

        await SeedArchivedAgentAsync(projectId, "reviewer");

        var assignment = await workflow.AssignWorkerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, assignment.Status);
        var claimed = await workflow.ClaimNextAsync(runnerId, "test-generation");
        Assert.Null(claimed);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, task.Status);
        Assert.Equal("agent_not_found", task.Error?.Code);
        Assert.Equal("agent_not_found", run.Failure?.Error?.Code);
        Assert.Contains("reviewer", run.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redelivery_DoesNotRedeliver_WhenProcessReportsTheWorkInFlight()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var key = WorkKey(_workflowId!, first!.WorkId);

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([key], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(resp.Dispatches);
    }

    [Fact]
    public async Task Redelivery_DoesNotRedeliver_WhenWorkIsAwaitingAck()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var key = WorkKey(_workflowId!, first!.WorkId);

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [key], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(resp.Dispatches);
    }

}
