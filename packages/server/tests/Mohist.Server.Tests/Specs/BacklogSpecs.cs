using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class BacklogFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;

    public Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
            siloBuilder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
            siloBuilder.Services.AddSingleton<IEventStore, NoopEventStore>();
        });
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("Backlog", DisableParallelization = true)]
public class BacklogCollection;

[Collection("Backlog")]
public class BacklogSpecs : IClassFixture<BacklogFixture>
{
    private readonly BacklogFixture _fixture;
    private string? _workflowId;
    private string? _runnerId;

    public BacklogSpecs(BacklogFixture fixture)
    {
        _fixture = fixture;
    }

    private IGrainFactory Grains => _fixture.Grains;

    private async Task<string> RegisterRunnerAsync()
    {
        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", "test-project"));
        return runnerId;
    }

    private async Task ClearBacklogAsync()
    {
        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));
        await backlog.ClearAsync();
    }

    private static WorkflowDefinition SingleStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        return new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                tasks ?? [new("task-1", "Task 1", "spec/task")],
                checks ?? [new("check-1", "Check 1", "spec/check")])
        ]);
    }

    [Fact]
    public async Task WorkflowInBacklog_RunnerClaimsOnFirstPoll()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.Equal(workflowId, work.WorkflowRunId);
        Assert.StartsWith("task-1.", work.WorkId);

        await runner.ReportAsync(work.WorkId, new WorkDispatchResult("completed"));
        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
        await runner.ReportAsync(check.WorkId, new WorkDispatchResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));

        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task PausedWorkflowInBacklog_RunnerClaimsButNoWork()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(SingleStage());
        await workflow.PauseAsync("hold");

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task FailedWorkflow_ReleasedFromBacklog()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        await runner.ReportAsync(work.WorkId, new WorkDispatchResult("failed", "boom"));

        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));
        var running = await backlog.ListRunningAsync();
        Assert.All(running, r => Assert.NotEqual(workflowId, r.WorkflowId));
    }

    [Fact]
    public async Task RetryAfterFailure_ReRegisteredToBacklog()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        await runner.ReportAsync(work.WorkId, new WorkDispatchResult("failed", "boom"));

        await workflow.RetryAsync();

        var retryWork = await runner.PollAsync();
        Assert.NotNull(retryWork);
        Assert.StartsWith("task-1.", retryWork.WorkId);
        await runner.ReportAsync(retryWork.WorkId, new WorkDispatchResult("completed"));

        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await runner.ReportAsync(check.WorkId, new WorkDispatchResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));
    }

    [Fact]
    public async Task NoRunner_WorkflowWaitsInBacklog()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(SingleStage());

        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));
        var waiting = await backlog.ListWaitingAsync();
        Assert.Contains(workflowId, waiting);

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.Equal(workflowId, work.WorkflowRunId);
    }

    [Fact]
    public async Task CompletedWorkflow_ReleasedFromBacklog()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var task = await runner.PollAsync();
        Assert.NotNull(task);
        await runner.ReportAsync(task.WorkId, new WorkDispatchResult("completed"));

        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await runner.ReportAsync(check.WorkId, new WorkDispatchResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));

        var backlog = Grains.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.ForProject("test-project"));
        var running = await backlog.ListRunningAsync();
        Assert.All(running, r => Assert.NotEqual(workflowId, r.WorkflowId));
    }
}
