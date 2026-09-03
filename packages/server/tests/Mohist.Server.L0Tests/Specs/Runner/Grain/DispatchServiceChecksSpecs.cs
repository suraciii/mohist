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
    public async Task ActiveChecks_ConflictingPullRequestCarrier_FailsThroughCheckFailurePath()
    {
        var (workflow, runnerId, _) = await StartActiveChecksWithPullRequestIdentityAsync();
        await SetConflictingRunCarrierAsync();

        var response = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(response.Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(FailureReason.CheckFailed, run.Failure?.Reason);
        Assert.Contains("pull_request_identity_conflict", run.Failure?.Message, StringComparison.Ordinal);
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();
        Assert.DoesNotContain(_workflowId!, await querier.FindRunningAssignedToAsync(runnerId));
    }

    [Fact]
    public async Task ActiveChecks_ConflictingPullRequestCarrier_RedeliveryAfterGrainActivationFailsRun()
    {
        var (workflow, runnerId, _) = await StartActiveChecksWithPullRequestIdentityAsync();
        await SetConflictingRunCarrierAsync();
        await DeactivateWorkflowAsync(_workflowId!);

        var response = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(response.Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(FailureReason.CheckFailed, run.Failure?.Reason);
        Assert.Contains("pull_request_identity_conflict", run.Failure?.Message, StringComparison.Ordinal);
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();
        Assert.DoesNotContain(_workflowId!, await querier.FindRunningAssignedToAsync(runnerId));
    }

    [Fact]
    public async Task PostClaimConflict_SettlementFailurePreservesAssignmentAndNextPollSettles()
    {
        await ClearBacklogAsync();
        _workflowId = $"post-claim-identity-{Guid.NewGuid():N}";
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
        await SeedWorkflowTemplateAsync(
            _workflowId,
            SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task")],
                checks: [new("check-1", "Check 1", "spec/check")]),
            TestProjectId(_workflowId));
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(TestProjectId(_workflowId), 1, null),
            new WorkflowStartSnapshot(repository, null));
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: JsonSerializer.SerializeToElement(new
        {
            github = new { pr = new { number = 42 } },
        })));
        Assert.Equal(WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);

        var initial = await PollWorkAsync(_runnerId);
        await ReportAsync(_runnerId, initial.Work, "completed");

        var checksWorkId = WorkflowRunExtensions.ChecksWorkIdFor("build");
        _fixture.ReportPersistenceFailures.FailNextWorkflowReport(_workflowId, checksWorkId);
        _fixture.DispatchPollObserver.BeforeWorkflowClaim = async workflowRunId =>
        {
            Assert.Equal(_workflowId, workflowRunId);
            await SetConflictingRunCarrierAsync();
            _fixture.DispatchPollObserver.BeforeWorkflowClaim = null;
        };

        try
        {
            var first = await Dispatch.PollAsync(
                _runnerId,
                new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            Assert.Empty(first.Dispatches);

            var stillAssigned = await LoadRunAsync(_workflowId);
            Assert.Equal(WorkflowRunStatus.Running, stillAssigned.Status);
            Assert.Equal(_runnerId, stillAssigned.Assignment?.WorkerId);
            await DeactivateWorkflowAsync(_workflowId);

            var second = await Dispatch.PollAsync(
                _runnerId,
                new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            Assert.Empty(second.Dispatches);

            var settled = await LoadRunAsync(_workflowId);
            Assert.Equal(WorkflowRunStatus.Failed, settled.Status);
            Assert.Equal(FailureReason.CheckFailed, settled.Failure?.Reason);
            Assert.Contains("pull_request_identity_conflict", settled.Failure?.Message, StringComparison.Ordinal);
            using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
            var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();
            Assert.DoesNotContain(_workflowId, await querier.FindRunningAssignedToAsync(_runnerId));
        }
        finally
        {
            _fixture.DispatchPollObserver.BeforeWorkflowClaim = null;
        }
    }

    private async Task<(IWorkflowGrain Workflow, string RunnerId, WorkDispatch Checks)> StartActiveChecksWithPullRequestIdentityAsync()
    {
        await ClearBacklogAsync();
        _workflowId = $"active-check-identity-{Guid.NewGuid():N}";
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
        await SeedWorkflowTemplateAsync(_workflowId, SingleStage(checks: [new("check-1", "Check 1", "spec/check")]));
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(TestProjectId(_workflowId), 1, null),
            new WorkflowStartSnapshot(repository, null));
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: JsonSerializer.SerializeToElement(new
        {
            github = new { pr = new { number = 42 } },
        })));

        var (task, taskRunnerId) = await PollWorkAnyAsync();
        await ReportAsync(taskRunnerId, task.WorkId, "completed");
        var (checks, checksRunnerId) = await PollWorkAnyAsync();
        Assert.Equal(_runnerId, checksRunnerId);
        return (workflow, checksRunnerId, checks);
    }

    private async Task SetConflictingRunCarrierAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowRunProfiles
            .SingleAsync(value => value.WorkflowRunId == _workflowId);
        row.Variables = new VariableBundle(Vars: JsonSerializer.SerializeToElement(new
        {
            github = new { pr = new { number = 43 } },
        })).ToJson();
        await db.SaveChangesAsync();
    }

}
