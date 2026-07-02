using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
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
using Mohist.Server.Tests.Specs.Issue.Profile;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

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
        var projectId = _workflowId is null ? "test-project" : TestProjectId(_workflowId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }
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

    private static WorkflowStartInput TestInput(string projectId)
    {
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
            }));
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
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var templateJson = System.Text.Json.JsonSerializer.Serialize(definition, WorkflowYamlSerializer.JsonOptions);
        var template = await db.ProjectWorkflowTemplates.FindAsync(projectId, definition.Id);
        if (template is null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = definition.Id,
                Template = templateJson,
            });
        }
        else
        {
            template.Template = templateJson;
            template.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var profile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (profile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultTemplateId = definition.Id,
            });
        }
        else
        {
            profile.DefaultTemplateId = definition.Id;
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }


    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowInBacklog_RunnerAssignsOnFirstPoll()
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

        await runner.ReportWorkflowResultAsync(_workflowId!, work.WorkId, new WorkResult("completed"));
        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        Assert.StartsWith("checks-", check.WorkId);
        await runner.ReportWorkflowResultAsync(_workflowId!, check.WorkId, new WorkResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));

        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task PausedWorkflowInBacklog_RunnerAssignsButNoWork()
    {
        await ResetClusterAsync();
        var workflow = await CreateAndStartAsync(SingleStage());
        await workflow.PauseAsync("hold");

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
        await runner.ReportWorkflowResultAsync(_workflowId!, work.WorkId, new WorkResult("failed", "boom"));

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Failed", status);

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
        await runner.ReportWorkflowResultAsync(_workflowId!, work.WorkId, new WorkResult("failed", "boom"));

        await workflow.RetryAsync();

        var retryWork = await runner.PollAsync();
        Assert.NotNull(retryWork);
        Assert.StartsWith("task-1.", retryWork.WorkId);
        await runner.ReportWorkflowResultAsync(_workflowId!, retryWork.WorkId, new WorkResult("completed"));

        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await runner.ReportWorkflowResultAsync(_workflowId!, check.WorkId, new WorkResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.Equal(_workflowId, work.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
        await runner.ReportWorkflowResultAsync(_workflowId!, task.WorkId, new WorkResult("completed"));

        var check = await runner.PollAsync();
        Assert.NotNull(check);
        Assert.Equal("checks", check.WorkType);
        await runner.ReportWorkflowResultAsync(_workflowId!, check.WorkId, new WorkResult("pass", Output: """[{"name":"check-1","status":"pass"}]"""));

        var anotherRunnerId = await RegisterRunnerAsync();
        var anotherRunner = Grains.GetGrain<IRunnerGrain>(anotherRunnerId);
        Assert.Null(await anotherRunner.PollAsync());
    }

}
