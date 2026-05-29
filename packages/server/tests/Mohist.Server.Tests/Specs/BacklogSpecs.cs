using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;
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
            siloBuilder.Services.AddScoped<IStateStore<WorkflowBacklogState>, InMemoryStateStore<WorkflowBacklogState>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowRunProfile>, InMemoryStateStore<WorkflowRunProfile>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkLease>, InMemoryStateStore<WorkLease>>();
            siloBuilder.Services.AddScoped<IWorkflowRunStore, InMemoryWorkflowRunStore>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowExecutionContext>, InMemoryStateStore<WorkflowExecutionContext>>();
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

    private async Task ResetClusterAsync()
    {
        var oldCluster = _fixture.Cluster;
        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddScoped<IStateStore<WorkflowBacklogState>, InMemoryStateStore<WorkflowBacklogState>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowRunProfile>, InMemoryStateStore<WorkflowRunProfile>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkLease>, InMemoryStateStore<WorkLease>>();
            siloBuilder.Services.AddScoped<IWorkflowRunStore, InMemoryWorkflowRunStore>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowExecutionContext>, InMemoryStateStore<WorkflowExecutionContext>>();
            siloBuilder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
            siloBuilder.Services.AddSingleton<IEventStore, NoopEventStore>();
        });
        var newCluster = builder.Build();
        await newCluster.DeployAsync();
        oldCluster?.Dispose();
        _fixture.GetType().GetProperty("Cluster")!.SetValue(_fixture, newCluster);
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

    private static WorkflowStartInput TestInput() => new(
        Variables: """{"project":{"id":"test-project"}}""");

    private async Task<IWorkflowGrain> CreateAndStartAsync(WorkflowDefinition definition)
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await workflow.StartAsync(definition, TestInput());
        return workflow;
    }

    [Fact]
    public async Task WorkflowInBacklog_RunnerClaimsOnFirstPoll()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.Equal(_workflowId, work.WorkflowRunId);
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
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());
        await workflow.PauseAsync("hold");

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task FailedWorkflow_ReleasedFromBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        await runner.ReportAsync(work.WorkId, new WorkDispatchResult("failed", "boom"));

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync());
    }

    [Fact]
    public async Task RetryAfterFailure_ReRegisteredToBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

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
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.Equal(_workflowId, work.WorkflowRunId);
    }

    [Fact]
    public async Task CompletedWorkflow_ReleasedFromBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

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

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync());
    }
}
