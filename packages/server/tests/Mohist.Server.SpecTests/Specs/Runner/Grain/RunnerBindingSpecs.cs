using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerBindingSpecs : WorkflowGrainSpecs
{
    public RunnerBindingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task CapacityOneRunner_WithInFlightWork_DoesNotGetSecondWorkflow()
    {
        var runnerId = await RegisterRunnerAsync("shared-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-1";
        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-1");
        await SeedWorkflowTemplateAsync("wf-1", SingleStage(checks: []));
        await wf1.StartAsync(TestInput());
        await AssignWorkflowToRunnerAsync("wf-1", runnerId);

        _workflowId = "wf-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-2");
        await SeedWorkflowTemplateAsync("wf-2", SingleStage(checks: []));
        await wf2.StartAsync(TestInput());

        var work1 = await runner.PollAsync(Services);
        Assert.NotNull(work1);
        Assert.Equal("wf-1", work1.WorkflowRunId);

        var work2 = await runner.PollAsync(Services);
        if (work2 is not null)
        {
            Assert.Equal("wf-1", work2.WorkflowRunId);
        }
    }

    [Fact]
    public async Task CapacityTwoRunner_TwoWorkflows_BothGetInFlightWork()
    {
        var projectId = "test-project-capacity";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, "shared-runner-capacity-2", maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-capacity-1";
        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-capacity-1");
        await SeedWorkflowTemplateAsync("wf-capacity-1", SingleStage(checks: []), projectId);
        await wf1.StartAsync(TestInput(projectId));
        await AssignWorkflowToRunnerAsync("wf-capacity-1", runnerId);

        _workflowId = "wf-capacity-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-capacity-2");
        await SeedWorkflowTemplateAsync("wf-capacity-2", SingleStage(checks: []), projectId);
        await wf2.StartAsync(TestInput(projectId));

        var dispatched = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3 && dispatched.Count < 2; i++)
        {
            foreach (var work in await runner.PollAllAsync(Services))
                dispatched.Add(work.WorkflowRunId);
        }

        Assert.Contains("wf-capacity-1", dispatched);
        Assert.Contains("wf-capacity-2", dispatched);
    }

    [Fact]
    public async Task TaskCompletes_NextTaskOnSameRunner()
    {
        await ClearBacklogAsync();
        var runnerId = await RegisterRunnerAsync("sticky-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-sticky";
        _runnerId = runnerId;

        var workflow = Grains.GetGrain<IWorkflowGrain>("wf-sticky");
        await SeedWorkflowTemplateAsync("wf-sticky", SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks: []));
        await workflow.StartAsync(TestInput());
        await AssignWorkflowToRunnerAsync("wf-sticky", runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        Assert.StartsWith("task-1.", first.WorkId);
        await ReportAsync(runnerId, first.WorkflowRunId, first.WorkId, new WorkResult("completed"));

        var second = await runner.PollAsync(Services);
        Assert.NotNull(second);
        Assert.StartsWith("task-2.", second.WorkId);
    }

    [Fact]
    public async Task TwoWorkflows_CompletingOneDoesNotAffectOther()
    {
        var runnerId = await RegisterRunnerAsync("report-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-report-1";
        _runnerId = runnerId;

        var wf1 = Grains.GetGrain<IWorkflowGrain>("wf-report-1");
        await SeedWorkflowTemplateAsync("wf-report-1", SingleStage(checks: []));
        await wf1.StartAsync(TestInput());
        await AssignWorkflowToRunnerAsync("wf-report-1", runnerId);

        _workflowId = "wf-report-2";
        var wf2 = Grains.GetGrain<IWorkflowGrain>("wf-report-2");
        await SeedWorkflowTemplateAsync("wf-report-2", SingleStage(checks: []));
        await wf2.StartAsync(TestInput());
        await AssignWorkflowToRunnerAsync("wf-report-2", runnerId);

        var work1 = await runner.PollAsync(Services);
        Assert.NotNull(work1);
        Assert.Equal("wf-report-1", work1.WorkflowRunId);
        await ReportAsync(runnerId, work1.WorkflowRunId, work1.WorkId, new WorkResult("completed"));

        var nextPoll = await runner.PollAsync(Services);
        Assert.NotNull(nextPoll);
        Assert.Equal("wf-report-2", nextPoll.WorkflowRunId);
        Assert.StartsWith("task-1.", nextPoll.WorkId);
    }

    [Fact]
    public async Task RuntimeState_DoesNotConsumeAssignedWork()
    {
        var runnerId = await RegisterRunnerAsync("runtime-read-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = "wf-runtime-read";
        var workflow = Grains.GetGrain<IWorkflowGrain>("wf-runtime-read");
        await SeedWorkflowTemplateAsync("wf-runtime-read", SingleStage(checks: []));
        await workflow.StartAsync(TestInput());
        await AssignWorkflowToRunnerAsync("wf-runtime-read", runnerId);

        var runtime = await runner.GetRuntimeStateAsync();
        Assert.DoesNotContain("wf-runtime-read", runtime.ActiveWorks.Select(w => w.OwnerId));

        var work = await runner.PollAsync(Services);
        Assert.NotNull(work);
        Assert.Equal("wf-runtime-read", work.WorkflowRunId);
    }

    [Fact]
    public async Task HeartbeatRepair_WhenRegistryEntryMissing_ReRegistersRunner()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"heartbeat-repair-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"heartbeat-repair-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var registeredAt = (await runner.GetInfoAsync())!.RegisteredAt;

        await registry.UnregisterAsync(runnerId);
        Assert.DoesNotContain(runnerId, await registry.ListRunnerIdsAsync());

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await runner.HeartbeatRepairAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            projectId,
            CoderModels: ["openai/gpt-4"],
            BuildGitHash: "heartbeat-hash"));

        var info = Assert.Single(await registry.ListRunnersAsync(), r => r.RunnerId == runnerId);
        Assert.Equal(projectId, info.ProjectId);
        Assert.Equal("heartbeat-hash", info.BuildGitHash);
        Assert.NotNull(info.CoderModels);
        Assert.Equal(["openai/gpt-4"], info.CoderModels!);
        Assert.Equal(registeredAt, info.RegisteredAt);
    }

    [Fact]
    public async Task Poll_WhenRegistryEntryMissing_DoesNotWriteRegistryEveryPoll()
    {
        await ClearGlobalRunnerRegistryAsync();
        await ClearBacklogAsync();
        var projectId = $"poll-redelivery-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"poll-redelivery-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

        await registry.UnregisterAsync(runnerId);
        Assert.DoesNotContain(runnerId, await registry.ListRunnerIdsAsync());

        var work = await runner.PollAsync(Services);

        Assert.Null(work);
        Assert.DoesNotContain(runnerId, await registry.ListRunnerIdsAsync());
    }

    [Fact]
    public async Task Register_ReRegisteringWithDifferentProjectId_KeepsRunnerInGlobalRegistry()
    {
        var projectId = $"scope-change-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"scope-change-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);

        Assert.Contains(runnerId, await registry.ListRunnerIdsAsync());
        var before = await runner.GetInfoAsync();
        Assert.Equal(projectId, before!.ProjectId);

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", null));

        Assert.Contains(runnerId, await registry.ListRunnerIdsAsync());
        var after = await runner.GetInfoAsync();
        Assert.Null(after!.ProjectId);
    }
}
