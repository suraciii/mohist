using System.Text.Json;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Variables.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class StatusSpecs : WorkflowGrainSpecs
{
    public StatusSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task WorkflowStatusShowsCurrentStage()
    {
        await StartWorkflowAsync(SingleStage());

        var (_, r1) = await PollWorkAnyAsync();

        var wf = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        var status = await wf.GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.Equal("build", status.CurrentStage);
        Assert.Single(status.Stages);
        Assert.Equal("build", status.Stages[0].Stage);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);
    }

    [Fact]
    public async Task WorkflowStatusShowsPendingWork()
    {
        var wf = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
            checks: []));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);

        var status = await wf.GetStatusAsync();
        Assert.NotNull(status);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);

        await ReportAsync(r1, task1.WorkId, "completed");

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task2.WorkId);

        var status2 = await wf.GetStatusAsync();
        Assert.NotNull(status2!.PendingWork);
        Assert.Equal("Task 2", status2.PendingWork.Title);

        await ReportAsync(r2, task2.WorkId, "completed");
    }

    [Fact]
    public async Task WorkflowStatusShowsTasksChecksAndApproval()
    {
        await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        await ReportAsync(r2, check.WorkId, "pass");

        var status = await Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("AwaitingApproval", status.Status);
        var planStage = status.Stages.Find(s => s.Stage == "plan");
        Assert.NotNull(planStage);
        Assert.Equal("AwaitingApproval", planStage.Status);
        Assert.Single(planStage.Tasks);
        Assert.Equal("Completed", planStage.Tasks[0].Status);
        Assert.Single(planStage.Checks);
        Assert.Equal("Passed", planStage.Checks[0].Status);
        Assert.NotNull(planStage.Approval);
        Assert.Equal("awaiting", planStage.Approval.Status);
    }

    [Fact]
    public async Task WorkflowDoesNotStoreIssueOrWorkspaceContext()
    {
        var wf = await CreateWorkflowAsync();
        await wf.StartAsync(SingleStage());

        var status = await wf.GetStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.DoesNotContain("Issue", typeof(WorkflowStatusSnapshot).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Worktree", typeof(WorkflowStatusSnapshot).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("ChangeDir", typeof(WorkflowStatusSnapshot).GetProperties().Select(p => p.Name));
    }
}
