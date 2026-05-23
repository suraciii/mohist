using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;
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
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Services.AddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
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

public abstract class WorkflowGrainSpecs : IClassFixture<WorkflowGrainFixture>
{
    protected readonly WorkflowGrainFixture _fixture;
    protected string? _workflowId;
    protected string? _runnerId;

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
        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;
        var workflowId = id ?? $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await runner.AssignWorkflowAsync(workflowId);
        await workflow.StartAsync(definition);
        return workflow;
    }

    protected async Task<IWorkflowGrain> StartWorkflowWithoutRunnerAsync(WorkflowDefinitionInput definition, string? id = null)
    {
        var workflow = await CreateWorkflowAsync(id);
        await workflow.StartAsync(definition);
        return workflow;
    }

    protected async Task<(WorkDispatch Work, string RunnerId)> PollWorkAnyAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
            var work = await runner.PollAsync();
            if (work is not null)
                return (work, _runnerId!);

            await Task.Delay(20);
        }

        Assert.Fail($"Runner '{_runnerId}' has no work for workflow '{_workflowId}'");
        return default;
    }

    protected async Task ReportAsync(string runnerId, string workId, string status, string? message = null)
    {
        await ReportAsync(runnerId, workId, new WorkDispatchResult(status, message));
    }

    protected async Task ReportAsync(string runnerId, string workId, WorkDispatchResult result)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportAsync(workId, result);
    }

    protected async Task ReportAsync(string runnerId, WorkDispatch work, string status, string? message = null)
    {
        await ReportAsync(runnerId, work, new WorkDispatchResult(status, message));
    }

    protected async Task ReportAsync(string runnerId, WorkDispatch work, WorkDispatchResult result)
    {
        await ReportAsync(runnerId, work.WorkId, result);
    }

    protected async Task ReportChecksAsync(string runnerId, WorkDispatch checksWork, params (string Name, string Status, string? Message)[] checkResults)
    {
        var output = JsonSerializer.Serialize(checkResults.Select(cr => new Dictionary<string, string?>
        {
            ["name"] = cr.Name,
            ["status"] = cr.Status,
            ["message"] = cr.Message,
        }));
        await ReportAsync(runnerId, checksWork.WorkId, new WorkDispatchResult(
            checkResults.All(cr => cr.Status == "pass") ? "pass" : "fail",
            Output: output));
    }

    protected async Task ReportChecksPassAsync(string runnerId, WorkDispatch checksWork, params string[] checkNames)
    {
        await ReportChecksAsync(runnerId, checksWork, checkNames.Select(n => (n, "pass", (string?)null)).ToArray());
    }

    protected async Task ReportChecksFailAsync(string runnerId, WorkDispatch checksWork, string failedCheckName, string message, params string[] passingCheckNames)
    {
        var results = passingCheckNames.Select(n => (n, "pass", (string?)null)).ToList();
        results.Add((failedCheckName, "fail", message));
        await ReportChecksAsync(runnerId, checksWork, results.ToArray());
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
