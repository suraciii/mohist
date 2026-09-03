using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("RunnerGrain")]
[Trait("level", "L1")]
public sealed class RunnerStatusProjectionSpecs : WorkflowGrainSpecs
{
    public RunnerStatusProjectionSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private async Task<WorkDispatch> StartIssueWorkflowWorkAsync(
        string runnerId,
        string workflowId,
        string projectId,
        int issueNumber,
        string title = "Issue Task")
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(
            tasks: [new("task-1", title, "spec/task")],
            checks: []), projectId);
        await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: _fixture.TimeProvider.GetUtcNow(),
            ProjectId: projectId,
            IssueNumber: issueNumber),
            VerificationCommand: "true"));
        await workflow.AssignWorkerAsync(runnerId);

        var work = await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(Services);
        Assert.NotNull(work);
        return work;
    }

    [Fact]
    public async Task GetRuntimeStateAsync_OnlineIdleRunner_ExposesEmptyActiveWorksList()
    {
        var runnerId = await RegisterRunnerAsync("idle-state-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var runtime = await runner.GetRuntimeStateAsync();

        Assert.NotNull(runtime.ActiveWorks);
        Assert.Empty(runtime.ActiveWorks);
    }

    [Fact]
    public async Task GetRuntimeStateAsync_BusyRunner_ExposesDispatchContextForActiveWork()
    {
        var runnerId = $"runner-active-ctx-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "active-ctx-host", "test-project"));

        var workflowId = $"wf-ctx-{Guid.NewGuid():N}";
        var issue = new WorkIssueRef("test-project", 42);
        var dispatch = await StartIssueWorkflowWorkAsync(
            runnerId,
            workflowId,
            issue.ProjectId,
            issue.IssueNumber,
            "Task 1");

        var runtime = await runner.GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.Equal(dispatch.WorkId, active.WorkId);
        Assert.Equal(WorkDispatchOwnerKinds.Workflow, active.OwnerKind);
        Assert.Equal(workflowId, active.OwnerId);
        Assert.Equal("task", active.WorkType);
        Assert.Equal("build", active.Stage);
        Assert.Equal("Task 1", active.Title);
        Assert.NotNull(active.Issue);
        Assert.Equal(issue.ProjectId, active.Issue!.ProjectId);
        Assert.Equal(issue.IssueNumber, active.Issue.IssueNumber);
    }

    [Fact]
    public async Task GetRuntimeStateAsync_BusyRunner_ProjectsWorkflowIssue()
    {
        var runnerId = $"runner-no-issue-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "no-issue-host", "test-project"));

        var workflowId = $"wf-no-issue-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowId, "task-1.1", "task", "build", "Task 1");

        var runtime = await runner.GetRuntimeStateAsync();
        var active = Assert.Single(runtime.ActiveWorks);
        Assert.NotNull(active.Issue);
        Assert.Equal(1, active.Issue!.IssueNumber);
    }

    [Fact]
    public async Task GetRuntimeStateAsync_MultiSlotRunner_ExposesAllConcurrentWorks()
    {
        var projectId = "test-project-multi";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"runner-multi-{Guid.NewGuid():N}", maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var workflowA = $"wf-multi-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-multi-b-{Guid.NewGuid():N}";
        await AssignActiveWorkForTestAsync(runnerId, workflowA);
        await AssignActiveWorkForTestAsync(runnerId, workflowB);

        var runtime = await runner.GetRuntimeStateAsync();

        Assert.Equal(2, runtime.ActiveWorks.Count);
        Assert.Contains(runtime.ActiveWorks, w => w.OwnerId == workflowA);
        Assert.Contains(runtime.ActiveWorks, w => w.OwnerId == workflowB);
        Assert.All(runtime.ActiveWorks, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.WorkId));
            Assert.Equal(WorkDispatchOwnerKinds.Workflow, w.OwnerKind);
        });
    }
}
