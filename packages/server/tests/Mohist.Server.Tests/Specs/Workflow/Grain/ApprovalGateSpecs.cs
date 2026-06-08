using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class ApprovalGateSpecs : WorkflowGrainSpecs
{
    public ApprovalGateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
        var poll = await runner.PollAsync();
        Assert.Null(poll);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task AwaitingApproval_UserApproves_WorkflowContinuesToNextStage()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.ApproveAsync();

        var (task2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", task2.WorkId);
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check2.WorkId);
        await ReportChecksPassAsync(r4, check2, "build-ok");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task AwaitingApproval_UserApproves_AssignedRunnerContinuesWorkflow()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.ApproveAsync();

        var nextRunnerId = await RegisterRunnerAsync();
        var nextRunner = Grains.GetGrain<IRunnerGrain>(nextRunnerId);
        Assert.Null(await nextRunner.PollAsync());

        var assignedRunner = Grains.GetGrain<IRunnerGrain>(r2);
        var buildWork = await assignedRunner.PollAsync();
        Assert.NotNull(buildWork);
        Assert.StartsWith("compile.", buildWork.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task AwaitingApproval_UserRejects_WorkflowFails()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RejectAsync("not good enough");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RejectedApproval_Rerun_RestartsStageFromScratch()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());
        var initialAttempt = await ReadCurrentStageAttemptAsync();
        Assert.Equal(1, initialAttempt);

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        // Reject: the reason must persist on the current StageRun.
        await workflow.RejectAsync("plan is too short");
        var (afterRejectAttempt, afterRejectReason, afterRejectStatus) =
            await ReadCurrentStageRerunFieldsAsync();
        Assert.Equal(1, afterRejectAttempt);
        Assert.Equal("plan is too short", afterRejectReason);
        Assert.Equal(StageRunStatus.Failed, afterRejectStatus);

        // Rerun: new stage takes over with Attempt=2; the old rejection
        // reason is carried over so the operator can see why the
        // previous attempt was rejected.
        await workflow.RerunAsync();
        var (afterRerunAttempt, afterRerunReason, _) =
            await ReadCurrentStageRerunFieldsAsync();
        Assert.Equal(2, afterRerunAttempt);
        Assert.Equal("plan is too short", afterRerunReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RejectedApproval_RerunTwice_ReasonStillOnTheLatestAttempt()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RejectAsync("first reason");
        await workflow.RerunAsync();

        // Drive a second cycle: tasks + checks + reject again, with
        // a different reason.
        var (task2, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, task2.WorkId, "completed");

        var (check2, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check2, "plan-ok");

        await workflow.RejectAsync("second reason");

        var (attempt, reason, _) = await ReadCurrentStageRerunFieldsAsync();
        Assert.Equal(2, attempt);
        Assert.Equal("second reason", reason);
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

    private async Task<(int Attempt, string? LastRejectionReason, StageRunStatus Status)>
        ReadCurrentStageRerunFieldsAsync()
    {
        var run = await LoadRunAsync();
        var current = run.Stages.First(s => s.Id == run.CurrentStageId);
        return (current.Attempt, current.LastRejectionReason, current.Status);
    }
}
