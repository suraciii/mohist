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
using Mohist.Server.L0Tests.Support;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Runner.Grain;

public partial class DispatchServiceReconciliationSpecs
{
    [Fact]
    public async Task PollAsync_AssignedWorkflowCanClaimItsOwnNextWorkAtCapacity()
    {
        var (runnerId, workflowIds) = await StartReadyWorkflowsAsync(
            $"poll-assigned-capacity-{Guid.NewGuid():N}", count: 1, slots: 1);
        var workflow = Grains.GetGrain<IWorkflowGrain>(Assert.Single(workflowIds));

        var assignment = await workflow.AssignWorkerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, assignment.Status);

        var dispatch = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        Assert.Equal(workflowIds[0], dispatch.WorkflowRunId);
    }

    [Fact]
    public async Task FindRunningAssignedToAsync_ReturnsOnlyRunningForTheRunner()
    {
        var prefix = $"desired-{Guid.NewGuid():N}";
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        await InsertStatusRowAsync($"{prefix}-run-1", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-2", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-blocked", "Running", runnerA, activeWork: false);
        await InsertStatusRowAsync($"{prefix}-mismatched-active-worker", "Running", runnerA, activeWorkerId: runnerB);
        await InsertStatusRowAsync($"{prefix}-ready-A", "Ready", runnerA);
        await InsertStatusRowAsync($"{prefix}-completed-A", "Completed", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-B", "Running", runnerB);

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var forA = await querier.FindRunningAssignedToAsync(runnerA);
        Assert.Equal(new[] { $"{prefix}-run-1", $"{prefix}-run-2" }, forA.Order());

        var forB = await querier.FindRunningAssignedToAsync(runnerB);
        Assert.Equal(new[] { $"{prefix}-run-B" }, forB);

        Assert.Empty(await querier.FindRunningAssignedToAsync($"{prefix}-runner-unknown"));
    }

    [Fact]
    public async Task PollAsync_OfflineRunner_ReturnsEmptyRound()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UnregisterAsync();

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(resp.Dispatches);
    }

    [Fact]
    public async Task PollAsync_UnregisterAfterInfoRead_DoesNotAssignWorkflow()
    {
        var (runnerId, workflowIds) = await StartReadyWorkflowsAsync(
            $"poll-unregister-{Guid.NewGuid():N}", count: 1, slots: 1);
        _fixture.DispatchPollObserver.Reset();
        _fixture.DispatchPollObserver.BlockAfterRunnerInfo();

        try
        {
            var poll = Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            await _fixture.DispatchPollObserver.WaitForRunnerInfoAsync();

            await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();

            Assert.Empty((await poll).Dispatches);
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowIds[0]);
            Assert.Null(await workflow.GetAssignedWorkerIdAsync());
            Assert.Equal("Pending", await workflow.GetRunStatusAsync());
        }
        finally
        {
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();
        }
    }

    [Fact]
    public async Task PollAsync_CancelledAfterInfoRead_ReleasesAdmission()
    {
        var (runnerId, _) = await StartReadyWorkflowsAsync(
            $"poll-cancel-{Guid.NewGuid():N}", count: 1, slots: 1);
        _fixture.DispatchPollObserver.Reset();
        _fixture.DispatchPollObserver.BlockAfterRunnerInfo();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var poll = Dispatch.PollAsync(
                runnerId,
                new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration),
                cancellation.Token);
            await _fixture.DispatchPollObserver.WaitForRunnerInfoAsync();

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);

            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var next = await runner.TryBeginPollAsync();
            Assert.True(next.Admitted);
            await runner.EndPollAsync(next.AdmissionToken);
        }
        finally
        {
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();
        }
    }

    [Fact]
    public async Task PollAsync_CapacityReducedAfterInfoRead_ClaimsAtMostNewCapacity()
    {
        var (runnerId, workflowIds) = await StartReadyWorkflowsAsync(
            $"poll-capacity-{Guid.NewGuid():N}", count: 2, slots: 2);
        _fixture.DispatchPollObserver.Reset();
        _fixture.DispatchPollObserver.BlockAfterRunnerInfo();

        try
        {
            var poll = Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            await _fixture.DispatchPollObserver.WaitForRunnerInfoAsync();

            await Grains.GetGrain<IRunnerGrain>(runnerId).UpdateAsync(1);
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();

            var response = await poll;
            Assert.Single(response.Dispatches);
            var statuses = await Task.WhenAll(workflowIds.Select(async workflowId =>
                await Grains.GetGrain<IWorkflowGrain>(workflowId).GetRunStatusAsync()));
            Assert.Equal(1, statuses.Count(status => status == "Running"));
            Assert.Equal(1, statuses.Count(status => status == "Pending"));
        }
        finally
        {
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();
        }
    }


}
