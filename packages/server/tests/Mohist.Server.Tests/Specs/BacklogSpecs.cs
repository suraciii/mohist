using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Infrastructure.Workflow;
using Mohist.Server.Project.Queries;
using Orleans;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class BacklogFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;
    public string ConnectionString => _keeper.ConnectionString;

    private SqliteConnection _keeper = null!;

    public Task InitializeAsync()
    {
        var dbName = $"mohist-backlog-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();

        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        ConfigureCluster(builder, connectionString);
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    public static void ConfigureCluster(InProcessTestClusterBuilder builder, string connectionString)
    {
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.Services.AddDbContextFactory<MohistDbContext>(options => options.UseSqlite(connectionString));
            siloBuilder.Services.AddScoped<IStateStore<WorkflowBacklogState>, InMemoryStateStore<WorkflowBacklogState>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowStageLockState>, InMemoryStateStore<WorkflowStageLockState>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowRunProfile>, InMemoryStateStore<WorkflowRunProfile>>();
            siloBuilder.Services.AddScoped<IStateStore<WorkLease>, InMemoryStateStore<WorkLease>>();
            siloBuilder.Services.AddScoped<IWorkflowRunStore, WorkflowRunStore>();
            siloBuilder.Services.AddScoped<IStateStore<WorkflowExecutionContext>, InMemoryStateStore<WorkflowExecutionContext>>();
            siloBuilder.Services.AddSingleton<ProjectQueryService>();
            siloBuilder.Services.AddScoped<WorkflowVariableResolver>();
            siloBuilder.Services.AddSingleton<IWorkflowBacklogDirectory, InMemoryWorkflowBacklogDirectory>();
            siloBuilder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
            siloBuilder.Services.AddSingleton<IEventStore, NoopEventStore>();
            siloBuilder.Services.AddHostedService<DbSchemaInitializer>();
        });
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

    private async Task<string> RegisterRunnerAsync(int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", "test-project", MaxWorkflowSlots: maxWorkflowSlots));
        return runnerId;
    }

    private async Task ResetClusterAsync()
    {
        var oldCluster = _fixture.Cluster;
        var builder = new InProcessTestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        BacklogFixture.ConfigureCluster(builder, _fixture.ConnectionString);
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

        await workflow.ReportResultAsync(runnerId, work.WorkId, new WorkResult("completed"));
        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
        await workflow.ReportResultAsync(runnerId, check.WorkId, new WorkResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));

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
        await workflow.ReportResultAsync(runnerId, work.WorkId, new WorkResult("failed", "boom"));

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Failed", status);

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
        await workflow.ReportResultAsync(runnerId, work.WorkId, new WorkResult("failed", "boom"));

        await workflow.RetryAsync();

        var retryWork = await runner.PollAsync();
        Assert.NotNull(retryWork);
        Assert.StartsWith("task-1.", retryWork.WorkId);
        await workflow.ReportResultAsync(runnerId, retryWork.WorkId, new WorkResult("completed"));

        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await workflow.ReportResultAsync(runnerId, check.WorkId, new WorkResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));
    }

    [Fact]
    public async Task NoRunner_WorkflowWaitsInBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);

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
        await workflow.ReportResultAsync(runnerId, task.WorkId, new WorkResult("completed"));

        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await workflow.ReportResultAsync(runnerId, check.WorkId, new WorkResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync());
    }

}
