using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class StatusSpecs : WorkflowGrainSpecs
{
    public StatusSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusShowsCurrentStage()
    {
        await StartWorkflowAsync(SingleStage());

        var (_, r1) = await PollWorkAnyAsync();

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.Equal("build", status.CurrentStage);
        Assert.Single(status.Stages);
        Assert.Equal("build", status.Stages[0].Stage);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusShowsPendingWork()
    {
        var wf = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
            checks: []));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);

        await ReportAsync(r1, task1.WorkId, "completed");

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task2.WorkId);

        var status2 = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status2!.PendingWork);
        Assert.Equal("Task 2", status2.PendingWork.Title);

        await ReportAsync(r2, task2.WorkId, "completed");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusShowsTasksChecksAndApproval()
    {
        await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

        Assert.NotNull(status);
        Assert.Equal("AwaitingApproval", status.Status);
        var planStage = status.Stages.Find(s => s.Stage == "plan");
        Assert.NotNull(planStage);
        Assert.Equal("AwaitingApproval", planStage.Status);
        Assert.Single(planStage.Tasks);
        Assert.Equal("Completed", planStage.Tasks[0].Status);
        Assert.Single(planStage.Checks);
        Assert.Equal("Passed", planStage.Checks[0].Status);
        Assert.NotNull(planStage.ApprovalStatus);
        Assert.Null(planStage.ApprovalStatus.Result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowDoesNotStoreIssueOrWorkspaceContext()
    {
        var wf = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await wf.StartAsync(TestInput());

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.DoesNotContain("Issue", typeof(WorkflowStatusView).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Worktree", typeof(WorkflowStatusView).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("ChangeDir", typeof(WorkflowStatusView).GetProperties().Select(p => p.Name));
    }
}
