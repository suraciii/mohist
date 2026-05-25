using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class DispatchAndLoadingSpecs : WorkflowGrainSpecs
{
    public DispatchAndLoadingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NoRunnerAtStart_RegisterLater_AssignAndRun()
    {
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.AssignWorkflowAsync(_workflowId!);

        var (task, rId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);

        await ReportAsync(rId, task.WorkId, "completed");
        var (check, checkRunnerId) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(checkRunnerId, check, "check-1");
    }

    [Fact]
    public async Task PausedBeforeRunner_StillPaused()
    {
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage());

        await workflow.PauseAsync("paused before capacity");
        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.AssignWorkflowAsync(_workflowId!);

        Assert.Null(await runner.PollAsync());
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task StageWithDynamicTasks_LoadCompletes_DynamicTasksMaterializedBeforeChecks()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build", [], [new("check-1", "Check 1", "spec/check")], TasksFromUses: "spec/load")
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("load-build:", load.WorkId);
        Assert.Equal("load", load.WorkType);
        Assert.Equal("build", load.Stage);
        Assert.Equal("spec/load", load.Uses);

        await ReportAsync(r1, load.WorkId, new WorkDispatchResult("loaded", Output: """
        {
          "tasks": [
            { "id": "dynamic-1", "title": "Dynamic 1", "uses": "spec/task", "with": { "value": "one" } },
            { "taskId": "dynamic-2", "title": "Dynamic 2", "uses": "spec/task" }
          ]
        }
        """));

        var (dynamic1, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-1.", dynamic1.WorkId);
        Assert.Equal("spec/task", dynamic1.Uses);
        Assert.Contains("one", dynamic1.With);
        await ReportAsync(r2, dynamic1.WorkId, "completed");

        var (dynamic2, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-2.", dynamic2.WorkId);
        await ReportAsync(r3, dynamic2.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r4, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.True(await runner.IsAvailableAsync());
    }

    [Fact]
    public async Task StageWithDynamicTasks_LoadedTaskWithContract_DispatchPreservesWithContract()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build", [], [], TasksFromUses: "spec/load")
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, load.WorkId, new WorkDispatchResult("loaded", Output: """
        {
          "tasks": [
            {
              "id": "T-001",
              "title": "Implement feature",
              "uses": "mohist/agent",
              "with": {
                "stage": "build",
                "task": "T-001",
                "description": "Add the feature flag service.",
                "acceptanceCriteria": ["service is registered"],
                "requireFiles": [{ "path": "src/FeatureFlags.cs" }],
                "requireMarkers": [{ "path": "openspec/changes/issue-1/tasks.json", "marker": "\"passes\": true" }]
              }
            }
          ]
        }
        """));

        var (dynamicTask, _) = await PollWorkAnyAsync();

        Assert.StartsWith("T-001.", dynamicTask.WorkId);
        Assert.Equal("mohist/agent", dynamicTask.Uses);
        Assert.NotNull(dynamicTask.With);
        Assert.Contains("Add the feature flag service.", dynamicTask.With);
        Assert.Contains("service is registered", dynamicTask.With);
        Assert.Contains("requireFiles", dynamicTask.With);
        Assert.Contains("requireMarkers", dynamicTask.With);
        Assert.Contains("src/FeatureFlags.cs", dynamicTask.With);
    }

    [Fact]
    public async Task StageWithStaticAndDynamicTasks_LoadCompletes_StaticTasksRunBeforeDynamicTasks()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput(
                "build",
                [new("static-1", "Static 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")],
                TasksFromUses: "spec/load")
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, load.WorkId, new WorkDispatchResult("loaded", Output: """
        [{ "id": "dynamic-1", "title": "Dynamic 1", "uses": "spec/task" }]
        """));

        var (staticTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("static-1.", staticTask.WorkId);
        await ReportAsync(r2, staticTask.WorkId, "completed");

        var (dynamicTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-1.", dynamicTask.WorkId);
        await ReportAsync(r3, dynamicTask.WorkId, "completed");

        var (check, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, check, "check-1");
    }

    [Fact]
    public async Task RunningWorkflow_CanAcceptRuntimeTaskBeforeChecks()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, task.WorkId, "completed");

        await workflow.AddTaskAsync(new RuntimeTaskInput("runtime-1", "Runtime 1", "spec/runtime", """{"value":"one"}"""));

        var (runtimeTask, runtimeRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("runtime-1.", runtimeTask.WorkId);
        Assert.Equal("spec/runtime", runtimeTask.Uses);
        Assert.Contains("one", runtimeTask.With);

        await ReportAsync(runtimeRunnerId, runtimeTask.WorkId, "completed");
        var (check, checkRunnerId) = await PollWorkAnyAsync();
        Assert.Equal("checks", check.WorkType);
        await ReportChecksPassAsync(checkRunnerId, check, "check-1");
    }

    [Fact]
    public async Task RuntimeTaskWithInvalidateChecks_ReopensStageChecks()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")],
            requiresApproval: true));

        var (task, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, task.WorkId, "completed");
        var (check, checkRunnerId) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(checkRunnerId, check, "check-1");
        var awaiting = await workflow.GetStatusAsync();
        Assert.Equal("AwaitingApproval", awaiting!.Status);
        Assert.Equal("Passed", awaiting.Stages[0].Checks[0].Status);

        await workflow.AddTaskAsync(new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));

        var afterAdd = await workflow.GetStatusAsync();
        Assert.Equal("Running", afterAdd!.Status);
        Assert.Equal("Pending", afterAdd.Stages[0].Checks[0].Status);

        var (rebase, rebaseRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("rebase.", rebase.WorkId);
        await ReportAsync(rebaseRunnerId, rebase.WorkId, "completed");

        var (rerunCheck, rerunCheckRunnerId) = await PollWorkAnyAsync();
        Assert.Equal("checks", rerunCheck.WorkType);
        await ReportChecksPassAsync(rerunCheckRunnerId, rerunCheck, "check-1");
    }

    [Fact]
    public async Task RuntimeTaskAddedBeforeStageMaterializes_DoesNotReplaceDefinedTasks()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        await workflow.AddTaskAsync(new RuntimeTaskInput("runtime-1", "Runtime 1", "spec/runtime"));

        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.AssignWorkflowAsync(_workflowId!);

        var first = await runner.PollAsync();
        Assert.NotNull(first);
        Assert.StartsWith("task-1.", first.WorkId);
        await runner.ReportAsync(first.WorkId, new WorkDispatchResult("completed"));

        var second = await runner.PollAsync();
        Assert.NotNull(second);
        Assert.StartsWith("runtime-1.", second.WorkId);
    }

    [Fact]
    public async Task FailedTaskWithRequestedTask_QueuesRequestedTaskInsteadOfFailingWorkflow()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("rebase", "Rebase", "mohist/rebase")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (rebase, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, rebase.WorkId, new WorkDispatchResult("failed", "conflict", Output: """
        {
          "kind": "rebase",
          "status": "conflict",
          "requestedTask": {
            "id": "resolve-rebase-conflicts",
            "title": "Resolve rebase conflicts",
            "uses": "mohist/agent",
            "with": {
              "stage": "maintenance",
              "task": "resolve-rebase-conflicts"
            },
            "then": {
              "id": "verify-rebase",
              "title": "Verify rebase completed",
              "uses": "mohist/rebase-status",
              "with": {
                "baseBranch": "main"
              }
            }
          }
        }
        """));

        var status = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetStatusAsync();
        Assert.Equal("Running", status!.Status);

        var (resolver, resolverRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("resolve-rebase-conflicts.", resolver.WorkId);
        Assert.Equal("mohist/agent", resolver.Uses);
        Assert.Contains("resolve-rebase-conflicts", resolver.With);

        await ReportAsync(resolverRunnerId, resolver.WorkId, "completed");
        var (verify, verifyRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("verify-rebase.", verify.WorkId);
        Assert.Equal("mohist/rebase-status", verify.Uses);
        await ReportAsync(verifyRunnerId, verify.WorkId, "completed");

        var (check, checkRunnerId) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(checkRunnerId, check, "check-1");
    }

    [Fact]
    public async Task StageWithDynamicTasks_LoadFails_WorkflowFails()
    {
        await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("build", [], [new("check-1", "Check 1", "spec/check")], TasksFromUses: "spec/load")
        ]));

        var (load, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("load-build:", load.WorkId);

        await ReportAsync(runnerId, load.WorkId, "failed", "loader failed");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await runner.IsAvailableAsync());
    }
}
