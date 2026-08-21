using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.OrleansTests.Support;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Orleans;
using Xunit;

namespace Mohist.Server.OrleansTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain")]
public class BacklogSpecs
{
    private readonly OrleansL0WorkflowGrainFixture _fixture;
    private string? _workflowId;

    public BacklogSpecs(OrleansL0WorkflowGrainFixture fixture)
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

    private async Task<string> RegisterRunnerAsync()
    {
        var runnerId = $"runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var projectId = _workflowId is null ? "test-project" : TestProjectId(_workflowId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
        return runnerId;
    }

    private async Task ResetRunnerRegistryAsync()
    {
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        foreach (var runnerId in await registry.ListRunnerIdsAsync())
            await registry.UnregisterAsync(runnerId);
    }

    private static WorkflowDefinition SingleStage() =>
        new(
        [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]);

    private static WorkflowStartInput TestInput(string projectId)
    {
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            ProjectId: projectId));
    }

    private static string TestProjectId(string workflowId) => $"test-project-{workflowId}";

    private async Task CreateAndStartAsync(WorkflowDefinition definition)
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var projectId = TestProjectId(workflowId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, definition, projectId);
        await workflow.StartAsync(TestInput(projectId));
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
        await ResetRunnerRegistryAsync();
        await CreateAndStartAsync(SingleStage());

        var runnerId = await RegisterRunnerAsync();
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
}
