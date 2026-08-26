using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class DispatchSnapshotPersistenceSpecs : WorkflowGrainSpecs
{
    public DispatchSnapshotPersistenceSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private IDispatchSnapshotStore Store(IServiceProvider services) =>
        services.GetRequiredService<IDispatchSnapshotStore>();

    private static async Task<WorkDispatch?> LoadAsync(IDispatchSnapshotStore store, string runId, string workId)
    {
        var json = await store.LoadJsonAsync(runId, workId);
        return json is null ? null : JSON.Deserialize<WorkDispatch>(json);
    }

    [Fact]
    public async Task Redelivery_AfterGrainDeactivation_ReturnsStoredSnapshotVerbatim()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (first, _) = await PollWorkAnyAsync();

        await DeactivateWorkflowAsync(_workflowId!);

        var redelivery = Assert.Single((await Dispatch().PollAsync(_runnerId!, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Equal(first, redelivery);
    }

    [Fact]
    public async Task HistoricalCoreScript_RedeliveryPreservesRawDeclarationAndTaskContract()
    {
        var rawWith = With("""
            {
              "resourceProfile": { "limits": { "cpu": "legacy" } },
              "run": "${{ vars.script }}",
              "shell": "${{ vars.shell }}",
              "timeout": "${{ vars.timeout }}"
            }
            """);
        var expect = With("""
            { "markers": [{ "path": "report.txt", "oneOf": ["PASS"] }] }
            """);
        var artifacts = new TaskArtifactCapture([new TaskArtifactDeclaration("report.txt")]);
        var setVars = new Dictionary<string, string> { ["result"] = "output.value" };
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition("error.code=script-failed", [], RetrySelf: true)]);
        await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "script",
                    "Historical script",
                    "core/script",
                    rawWith,
                    expect,
                    artifacts,
                    setVars,
                    recovery),
            ],
            checks: []));

        var (first, _) = await PollWorkAnyAsync();
        var persistedBefore = await LoadRunAsync(_workflowId!);
        var taskBefore = Assert.Single(persistedBefore.CurrentStage().Tasks);
        var taskStateBefore = JSON.Serialize(taskBefore);

        Assert.Equal(_workflowId, first.WorkflowRunId);
        Assert.Equal("script.1", first.WorkId);
        Assert.Equal(taskBefore.Id, first.TaskRunId);
        Assert.Equal("Historical script", first.Title);
        Assert.Equal("core/script", first.Uses);
        Assert.Equal(JSON.Serialize(rawWith), first.With);
        Assert.Equal(JSON.Serialize(artifacts), first.Artifacts);
        Assert.Equal(JSON.Serialize(setVars), first.SetVars);
        Assert.Equal(JSON.Serialize(expect), first.Expect);
        Assert.Equal(JSON.Serialize(recovery), first.Recovery);
        Assert.Null(first.RecoveryRemaining);
        using (var variables = JsonDocument.Parse(first.Variables!))
            Assert.True(variables.RootElement.TryGetProperty("vars", out _));

        await DeactivateWorkflowAsync(_workflowId!);

        var redelivery = Assert.Single((await Dispatch().PollAsync(
            _runnerId!, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        Assert.Equal(first, redelivery);
        Assert.Equal(taskStateBefore, JSON.Serialize((await LoadRunAsync(_workflowId!)).CurrentStage().Tasks.Single()));
        Assert.Equal(JSON.Serialize(rawWith), redelivery.With);
        Assert.Equal(JSON.Serialize(recovery), redelivery.Recovery);
    }

    [Fact]
    public async Task ChecksDispatch_DoesNotPersistSnapshotAndRedeliveryReconstructs()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, task.WorkId, "completed");

        var checksList = await PollWorkAnyAsync();

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var store = Store(scope.ServiceProvider);
        Assert.Null(await LoadAsync(store, _workflowId!, checksList.Work.WorkId));

        var checksWork = checksList.Work;
        await DeactivateWorkflowAsync(_workflowId!);

        var redelivery = Assert.Single((await Dispatch().PollAsync(_runnerId!, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Equal(checksWork.OwnerKind, redelivery.OwnerKind);
        Assert.Equal(checksWork.WorkId, redelivery.WorkId);
        Assert.Equal(checksWork.WorkflowRunId, redelivery.WorkflowRunId);
    }

    private DispatchService Dispatch() =>
        _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<DispatchService>();
}
