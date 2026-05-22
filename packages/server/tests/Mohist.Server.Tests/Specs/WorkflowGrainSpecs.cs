using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Orleans.TestingHost;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowGrainFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;
    public IGrainFactory Grains => Cluster.Client;

    public Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder();
        Cluster = builder.Build();
        return Cluster.DeployAsync();
    }

    public Task DisposeAsync()
    {
        Cluster?.Dispose();
        return Task.CompletedTask;
    }

}

public abstract class WorkflowGrainSpecs : IClassFixture<WorkflowGrainFixture>
{
    private readonly WorkflowGrainFixture _fixture;
    private string? _workflowId;

    protected WorkflowGrainSpecs(WorkflowGrainFixture fixture)
    {
        _fixture = fixture;
    }

    protected IGrainFactory Grains => _fixture.Grains;

    protected async Task<string> RegisterRunnerAsync(string? runnerId = null)
    {
        runnerId ??= $"runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host"));
        return runnerId;
    }

    protected async Task<IWorkflowGrain> CreateWorkflowAsync(string? id = null)
    {
        id ??= $"wf-{Guid.NewGuid():N}";
        _workflowId = id;
        return Grains.GetGrain<IWorkflowGrain>(id);
    }

    protected async Task<IWorkflowGrain> StartWorkflowAsync(WorkflowDefinitionInput definition, string? id = null)
    {
        await RegisterRunnerAsync();
        var workflow = await CreateWorkflowAsync(id);
        await workflow.StartAsync(definition);
        return workflow;
    }

    protected async Task<(WorkDispatch Work, string RunnerId)> PollWorkAnyAsync()
    {
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        foreach (var runnerId in await registry.ListRunnerIdsAsync())
        {
            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var assigned = await runner.PeekAsync();
            if (assigned?.RunId != _workflowId) continue;

            var work = await runner.PollAsync();
            Assert.NotNull(work);
            return (work, runnerId);
        }

        Assert.Fail($"No runner has work available for workflow '{_workflowId}'");
        return default;
    }

    protected async Task ReportAsync(string runnerId, string workId, string status, string? message = null)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var result = new WorkDispatchResult(status, message);
        var runId = await runner.ReportAsync(workId, result);

        if (runId is not null)
        {
            var workflow = Grains.GetGrain<IWorkflowGrain>(runId);
            await workflow.ReportResultAsync(workId, result);
        }
    }

    protected static WorkflowDefinitionInput SingleStage(
        List<TaskDefinitionInput>? tasks = null,
        List<CheckDefinitionInput>? checks = null,
        bool requiresApproval = false,
        string stage = "build")
    {
        return new WorkflowDefinitionInput(
        [
            new StageDefinitionInput(stage,
                tasks ?? [new("task-1", "Task 1", "spec/task")],
                checks ?? [new("check-1", "Check 1", "spec/check")],
                RequiresApproval: requiresApproval)
        ]);
    }

    protected static WorkflowDefinitionInput TwoStages()
    {
        return new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]);
    }

    protected static WorkflowDefinitionInput ApprovalStage()
    {
        return new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")],
                RequiresApproval: true),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]);
    }
}
