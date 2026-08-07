using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class ApprovalGateSpecs : WorkflowGrainSpecs
{
    public ApprovalGateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ApprovalStage_TasksAndChecksPass_WorkflowAwaitsApproval()
    {
        await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        var poll = await runner.PollAsync(Services);
        Assert.Null(poll);
    }

    [Fact]
    public async Task AwaitingApproval_UserApprovesWithoutOperator_WorkflowContinuesToNextStage()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.ApproveAsync();

        var run = await LoadRunAsync();
        Assert.Null(run.Stages.Single(stage => stage.Id == "plan").ApprovalStatus?.DecidedBy);

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", task2.WorkId);
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check2.WorkId);
        await ReportChecksPassAsync(r4, check2, "build-ok");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task AwaitingApproval_UserApproves_AssignedRunnerContinuesWorkflow()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.ApproveAsync("operator-1");

        var nextRunnerId = await RegisterRunnerAsync();
        var nextRunner = Grains.GetGrain<IRunnerGrain>(nextRunnerId);
        Assert.Null(await nextRunner.PollAsync(Services));

        var assignedRunner = Grains.GetGrain<IRunnerGrain>(r2);
        var buildWork = await assignedRunner.PollAsync(Services);
        Assert.NotNull(buildWork);
        Assert.StartsWith("compile.", buildWork.WorkId);
    }

    [Fact]
    public async Task AwaitingApproval_LegacyReject_RoutesToFeedbackLoop_AndDoesNotFail()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        // RequestChanges must NOT mark the workflow as failed; it
        // routes through the feedback loop.
        await workflow.RequestChangesAsync("not good enough");

        var run = await LoadRunAsync();
        Assert.NotEqual(WorkflowRunStatus.Failed, run.Status);
        var current = run.Stages.First(s => s.Id == run.CurrentStageId);
        Assert.Equal(StageRunStatus.Running, current.Status);
        Assert.NotNull(current.ApprovalStatus);
        Assert.Null(current.ApprovalStatus!.Result);
        Assert.Null(current.ApprovalStatus.DecidedBy);
        Assert.Single(run.Feedback);
        Assert.Equal("not good enough", run.Feedback[0].Body);
        Assert.Equal(ApprovalFeedbackStatus.Open, run.Feedback[0].Status);

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task RejectedApproval_LegacyReject_NewRunResumesFromFeedbackTask()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());
        var initialAttempt = await ReadCurrentStageAttemptAsync();
        Assert.Equal(1, initialAttempt);

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        // RequestChanges routes through the feedback loop and
        // schedules an apply-feedback task. The stage does not fail.
        await workflow.RequestChangesAsync("plan is too short", "operator-1");

        var run = await LoadRunAsync();
        var current = run.Stages.First(s => s.Id == run.CurrentStageId);
        Assert.Equal(1, current.Attempt);
        Assert.Equal(StageRunStatus.Running, current.Status);
        Assert.Single(run.Feedback);
        Assert.Equal("plan is too short", run.Feedback[0].Body);
        Assert.Equal(ApprovalFeedbackStatus.Open, run.Feedback[0].Status);
        Assert.Contains(current.Tasks, t => t.DefinitionId == "apply-feedback");
    }

    private async Task<WorkflowRun> LoadRunAsync()
    {
        await using var db = new MohistDbContext(
            new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_fixture.ConnectionString)
                .Options);
        var row = await db.WorkflowRuns.FindAsync(_workflowId!);
        Assert.NotNull(row);
        var jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        return JsonSerializer.Deserialize<WorkflowRun>(row!.State, jsonOptions)!;
    }

    private async Task<int> ReadCurrentStageAttemptAsync()
    {
        var run = await LoadRunAsync();
        return run.Stages.First(s => s.Id == run.CurrentStageId).Attempt;
    }
}
