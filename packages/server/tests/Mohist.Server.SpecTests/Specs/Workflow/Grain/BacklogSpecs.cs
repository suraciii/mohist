using Mohist.Server.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Orleans;
using Orleans.TestingHost;
using Xunit;
using Mohist.Server.SpecTests.Specs.Issue.Profile;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

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

    // The runner grain no longer relays workflow reports; report direct to the
    // owning grain via the shared translator path (mirrors the API /report).
    private async Task ReportAsync(string runnerId, string workflowRunId, string workId, WorkResult result)
        => await DispatchTestExtensions.ReportWorkflowDirectAsync(
            Grains, _fixture.Cluster.GetSiloServiceProvider(null),
            runnerId, workflowRunId, workId, result);

    private async Task<string> RegisterRunnerAsync(int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var projectId = _workflowId is null ? "test-project" : TestProjectId(_workflowId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*", RunnerCapabilities.WorkflowTaskCompletionBoundaryV1],
            "test-host",
            projectId));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }
        return runnerId;
    }

    private async Task ResetClusterAsync()
    {
        var oldCluster = _fixture.Cluster;
        var builder = new InProcessTestClusterBuilder().UseLogicalPorts();
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
        return new WorkflowDefinition(
        [
            new StageDefinition("build",
                tasks ?? [new("task-1", "Task 1", "spec/task")],
                checks ?? [new("check-1", "Check 1", "spec/check")])
        ]);
    }

    private static WorkflowStartInput TestInput(string projectId)
    {
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            ProjectId: projectId));
    }

    private static string TestProjectId(string workflowId) => $"test-project-{workflowId}";

    private async Task<IWorkflowGrain> CreateAndStartAsync(WorkflowDefinition definition)
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var projectId = TestProjectId(workflowId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        await workflow.StartAsync(TestInput(projectId));
        return workflow;
    }

    private async Task SeedWorkflowTemplateAsync(string workflowId, WorkflowDefinition definition, string projectId)
    {
        await WorkflowGrainTestHelpers.SeedWorkflowTemplateAsync(
            _fixture.ConnectionString,
            workflowId,
            definition,
            projectId);
    }


    [Fact]
    public async Task WorkflowInBacklog_RunnerAssignsOnFirstPoll()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(work);
        Assert.Equal(_workflowId, work.WorkflowRunId);
        Assert.StartsWith("task-1.", work.WorkId);

        await ReportAsync(runnerId, _workflowId!, work.WorkId, new WorkResult("completed"));
        var check = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
        await ReportAsync(runnerId, _workflowId!, check.WorkId, new WorkResult("pass", Output: JSON.DeserializeElement("[{\"name\":\"check-1\",\"status\":\"pass\"}]")));

        Assert.Null(await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null)));
    }

    [Fact]
    public async Task PausedWorkflowInBacklog_RunnerAssignsButNoWork()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());
        await workflow.PauseAsync("hold");

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        Assert.Null(await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null)));
    }

    [Fact]
    public async Task FailedWorkflow_ReleasedFromBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(work);
        await ReportAsync(runnerId, _workflowId!, work.WorkId, new WorkResult("failed", "boom"));

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Failed", status);

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null)));
    }

    [Fact]
    public async Task RetryAfterFailure_ReRegisteredToBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(work);
        await ReportAsync(runnerId, _workflowId!, work.WorkId, new WorkResult("failed", "boom"));

        await workflow.RetryAsync();

        var retryWork = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(retryWork);
        Assert.StartsWith("task-1.", retryWork.WorkId);
        await ReportAsync(runnerId, _workflowId!, retryWork.WorkId, new WorkResult("completed"));

        var check = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await ReportAsync(runnerId, _workflowId!, check.WorkId, new WorkResult("pass", Output: JSON.DeserializeElement("[{\"name\":\"check-1\",\"status\":\"pass\"}]")));
    }

    [Fact]
    public async Task NoRunner_WorkflowWaitsInBacklog()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());

        var status = await workflow.GetRunStatusAsync();
        // After Start without a runner assignment, the workflow sits in
        // Pending — the assignment pool will pick it up.
        Assert.Equal("Pending", status);

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
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

        var task = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(task);
        await ReportAsync(runnerId, _workflowId!, task.WorkId, new WorkResult("completed"));

        var check = await runner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null));
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await ReportAsync(runnerId, _workflowId!, check.WorkId, new WorkResult("pass", Output: JSON.DeserializeElement("[{\"name\":\"check-1\",\"status\":\"pass\"}]")));

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync(_fixture.Cluster.GetSiloServiceProvider(null)));
    }

}
