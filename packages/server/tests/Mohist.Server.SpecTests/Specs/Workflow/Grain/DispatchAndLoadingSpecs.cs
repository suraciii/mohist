using Mohist.Server.Infrastructure;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class DispatchAndLoadingSpecs : WorkflowGrainSpecs
{
    public DispatchAndLoadingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task NoRunnerAtStart_RegisterLater_AssignAndRun()
    {
        var workflow = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        _runnerId = await RegisterRunnerAsync();

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
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        await workflow.PauseAsync("paused before capacity");
        _runnerId = await RegisterRunnerAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId);

        Assert.Null(await runner.PollAsync(Services));
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task StageWithDynamicTasks_LoadTaskCompletes_DynamicTasksRunBeforeChecks()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/load")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (load, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("load-tasks.", load.WorkId);
        Assert.Equal("task", load.WorkType);
        Assert.Equal("build", load.Stage);
        Assert.Equal("spec/load", load.Uses);

        var addResult = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("dynamic-1", "Dynamic 1", "spec/task", JsonSerializer.Deserialize<JsonElement>("""{"value":"one"}""")),
                new AddTasksBatchItem("dynamic-2", "Dynamic 2", "spec/task")
            ]));
        Assert.Equal(2, addResult.AddedCount);

        await ReportAsync(r1, load.WorkId, "completed");

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
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task DynamicTaskRegistration_DoesNotAbandonInFlightLoadTaskOnConcurrentPoll()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/load")],
                [new("check-1", "Check 1", "spec/check")])
        ]), maxWorkflowSlots: 2);

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        var load = await runner.PollAsync(Services);
        Assert.NotNull(load);
        Assert.StartsWith("load-tasks.", load.WorkId);

        var addResult = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("dynamic-1", "Dynamic 1", "spec/task")
            ]));
        Assert.Equal(1, addResult.AddedCount);

        var concurrentPoll = await runner.PollAsync(Services);
        if (concurrentPoll is not null)
        {
            Assert.Equal(load.WorkflowRunId, concurrentPoll.WorkflowRunId);
            Assert.Equal(load.WorkId, concurrentPoll.WorkId);
        }

        await ReportAsync(_runnerId!, _workflowId!, load.WorkId, new WorkResult("completed"));

        var dynamicTask = await runner.PollAsync(Services);
        Assert.NotNull(dynamicTask);
        Assert.Equal(_workflowId, dynamicTask.WorkflowRunId);
        Assert.StartsWith("dynamic-1.", dynamicTask.WorkId);
    }

    [Fact]
    public async Task TaskDispatch_DoesNotInjectDisplayTitleIntoWith()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("integrate",
                [new("integrate:open-pr", "Open or update GitHub PR", "mohist/create-pull-request", With("""
                {
                  "source": "mohist/run-1",
                  "target": "master",
                  "remote": "origin",
                  "titleFrom": "issue.title",
                  "bodyFrom": "issue.body"
                }
                """))],
                [])
        ]));

        var (task, _) = await PollWorkAnyAsync();

        Assert.Equal("Open or update GitHub PR", task.Title);
        Assert.NotNull(task.With);
        using var with = JsonDocument.Parse(task.With);
        Assert.False(with.RootElement.TryGetProperty("title", out _));
        Assert.Equal("issue.title", with.RootElement.GetProperty("titleFrom").GetString());
        Assert.Equal("issue.body", with.RootElement.GetProperty("bodyFrom").GetString());
    }

    [Fact]
    public async Task StageWithDynamicTasks_TaskWithContract_DispatchPreservesWithContract()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/load")],
                [])
        ]));

        var (load, r1) = await PollWorkAnyAsync();

        var withJson = JsonDocument.Parse("""
            { "prompt": "Add the feature flag service.\n- service is registered" }
            """).RootElement;
        var expectJson = JsonDocument.Parse("""
            {
              "files": [{ "path": "src/FeatureFlags.cs" }],
              "markers": [{ "path": "openspec/changes/issue-1/tasks.json", "contains": "\"passes\": true" }]
            }
            """).RootElement;

        await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", withJson, expectJson)
            ]));

        await ReportAsync(r1, load.WorkId, "completed");

        var (dynamicTask, _) = await PollWorkAnyAsync();

        Assert.StartsWith("T-001.", dynamicTask.WorkId);
        Assert.Equal("mohist/opencode", dynamicTask.Uses);
        Assert.NotNull(dynamicTask.With);
        Assert.Contains("Add the feature flag service.", dynamicTask.With);
        Assert.Contains("service is registered", dynamicTask.With);
        Assert.NotNull(dynamicTask.Expect);
        Assert.Contains("contains", dynamicTask.Expect!);
        Assert.Contains("src/FeatureFlags.cs", dynamicTask.Expect!);
        Assert.DoesNotContain("expect", dynamicTask.With!);
    }

    [Fact]
    public async Task StageWithDynamicAgentVariables_LoadedDynamicTasksInheritStageAgent()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new("load-tasks", "Load tasks", "spec/load")],
                [])
        ]));
        await PatchIssueVariablesAsync(TestIssueNumber(_workflowId!), new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new { agent = new { model = "kimi-for-coding/k2p6" } }),
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { agent = new { model = "openai/gpt-5.4" } }))
            }));

        var (load, r1) = await PollWorkAnyAsync();

        await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", JsonSerializer.Deserialize<JsonElement>("""
                    {"prompt":"Implement feature","options":"${{ vars.agent }}"}
                    """))
            ]));

        await ReportAsync(r1, load.WorkId, "completed");

        var (dynamicTask, _) = await PollWorkAnyAsync();

        Assert.StartsWith("T-001.", dynamicTask.WorkId);
        Assert.Contains("${{ vars.agent }}", dynamicTask.With);
        Assert.DoesNotContain("openai/gpt-5.4", dynamicTask.With);
        Assert.DoesNotContain("kimi-for-coding/k2p6", dynamicTask.With);
        Assert.NotNull(dynamicTask.Variables);
        using var varsDoc = JsonDocument.Parse(dynamicTask.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("openai/gpt-5.4", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageWithAgentVariables_TaskWithoutAgentInheritsStageAgentAtDispatch()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new("T-001", "Implement feature", "mohist/opencode", new Dictionary<string, JsonElement?>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("Implement feature"),
                    ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}")
                })],
                [])
        ]));
        await PatchIssueVariablesAsync(TestIssueNumber(_workflowId!), new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new { agent = new { model = "kimi-for-coding/k2p6" } }),
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { agent = new { model = "openai/gpt-5.4" } }))
            }));

        var (dynamicTask, _) = await PollWorkAnyAsync();

        Assert.StartsWith("T-001.", dynamicTask.WorkId);
        Assert.Contains("Implement feature", dynamicTask.With);
        Assert.Contains("${{ vars.agent }}", dynamicTask.With);
        Assert.DoesNotContain("openai/gpt-5.4", dynamicTask.With);
        Assert.DoesNotContain("kimi-for-coding/k2p6", dynamicTask.With);
        Assert.NotNull(dynamicTask.Variables);
        using var varsDoc = JsonDocument.Parse(dynamicTask.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("openai/gpt-5.4", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageWithAgentVariables_TaskAgentTemplatePreservedAndSnapshotCarriesStageAgent()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "check",
                [new("ai-review", "AI review", "mohist/opencode", new Dictionary<string, JsonElement?>
                {
                    ["session"] = JsonSerializer.SerializeToElement("check"),
                    ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.review }}"),
                    ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}")
                })],
                [])
        ]));
        await PatchIssueVariablesAsync(TestIssueNumber(_workflowId!), new VariableBundle(
            Stages: new Dictionary<string, StageVariables>
            {
                ["check"] = new(JsonSerializer.SerializeToElement(new { agent = new { type = "opencode", model = "openai/gpt-5.5" } }))
            }));

        var (task, _) = await PollWorkAnyAsync();

        Assert.StartsWith("ai-review.", task.WorkId);
        using var with = JsonDocument.Parse(task.With!);
        Assert.Equal("${{ vars.agent }}", with.RootElement.GetProperty("options").GetString());
        Assert.Equal("${{ prompts.review }}", with.RootElement.GetProperty("prompt").GetString());
        Assert.DoesNotContain("kimi-for-coding/k2p6", task.With);
        Assert.DoesNotContain("openai/gpt-5.5", task.With);
        Assert.NotNull(task.Variables);
        using var varsDoc = JsonDocument.Parse(task.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageAgentWithoutModel_InheritsIssueAgentModelAtDispatch()
    {
        // After T-003 the runtime reads the issue layer (T1 snapshot) directly
        // and does not re-merge the project layer. A stage override that omits
        // `agent.model` inherits the issue's top-level `agent`, not the
        // project or the embedded variable.
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new("T-001", "Implement feature", "mohist/opencode", new Dictionary<string, JsonElement?>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("Implement feature"),
                    ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}")
                })],
                [])
        ]));

        await PatchIssueVariablesAsync(TestIssueNumber(_workflowId!), new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new
            {
                agent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3" }
            }),
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new
                {
                    agent = new { }
                }))
            }));

        var (task, _) = await PollWorkAnyAsync();

        Assert.StartsWith("T-001.", task.WorkId);
        Assert.Contains("${{ vars.agent }}", task.With);
        Assert.DoesNotContain("minimax-coding-plan/MiniMax-M3", task.With);
        Assert.DoesNotContain("old-coding/legacy", task.With);
        Assert.DoesNotContain("kimi-for-coding/k2p6", task.With);
        Assert.NotNull(task.Variables);
        using var varsDoc = JsonDocument.Parse(task.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageAgentVariableUpdate_DispatchedTaskInheritsLatestIssueAgentModel()
    {
        // After T-003 the runtime reads the issue layer directly. Patching
        // the issue layer (the T1 snapshot) is what surfaces in dispatch.
        var initialAgent = new { type = "opencode", model = "old-coding/legacy" };
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new("T-001", "Implement feature", "mohist/opencode", new Dictionary<string, JsonElement?>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("Implement feature"),
                    ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}")
                })],
                [])
        ]));

        var updatedAgent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3" };
        await PatchIssueVariablesAsync(TestIssueNumber(_workflowId!), new VariableBundle(Stages: new Dictionary<string, StageVariables>
        {
            ["build"] = new(JsonSerializer.SerializeToElement(new { agent = updatedAgent }))
        }));

        var (task, _) = await PollWorkAnyAsync();

        Assert.StartsWith("T-001.", task.WorkId);
        Assert.Contains("${{ vars.agent }}", task.With);
        Assert.DoesNotContain("minimax-coding-plan/MiniMax-M3", task.With);
        Assert.DoesNotContain("old-coding/legacy", task.With);
        Assert.NotNull(task.Variables);
        using var varsDoc = JsonDocument.Parse(task.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageWithStaticAndDynamicTasks_LoadTaskThenDynamicThenStaticBeforeChecks()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition(
                "build",
                [new("load-tasks", "Load tasks", "spec/load"), new("static-1", "Static 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (load, r1) = await PollWorkAnyAsync();

        await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("dynamic-1", "Dynamic 1", "spec/task")
            ]));

        await ReportAsync(r1, load.WorkId, "completed");

        var (dynamicTask, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("dynamic-1.", dynamicTask.WorkId);
        await ReportAsync(r2, dynamicTask.WorkId, "completed");

        var (staticTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("static-1.", staticTask.WorkId);
        await ReportAsync(r3, staticTask.WorkId, "completed");

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

        await workflow.AddTaskAsync(new RuntimeTaskInput("runtime-1", "Runtime 1", "spec/runtime", JsonSerializer.Deserialize<JsonElement>("""{"value":"one"}""")));

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
    public async Task AddTaskAsync_InsertsAsNextTaskBeforeExistingPendingTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task1, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);
        await ReportAsync(runnerId, task1.WorkId, "completed");

        await workflow.AddTaskAsync(new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));

        var (rebase, rebaseRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("rebase.", rebase.WorkId);
        await ReportAsync(rebaseRunnerId, rebase.WorkId, "completed");

        var (task2, task2RunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task2.WorkId);
        await ReportAsync(task2RunnerId, task2.WorkId, "completed");
    }

    [Fact]
    public async Task AddTaskAsync_WhenTaskIsRunning_InsertsAfterRunningTaskBeforeOtherPendingTasks()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (task1, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);

        await workflow.AddTaskAsync(new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));

        await ReportAsync(runnerId, task1.WorkId, "completed");

        var (rebase, rebaseRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("rebase.", rebase.WorkId);
        await ReportAsync(rebaseRunnerId, rebase.WorkId, "completed");

        var (task2, task2RunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task2.WorkId);
        await ReportAsync(task2RunnerId, task2.WorkId, "completed");
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
        var awaiting = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.Equal("awaiting-approval", awaiting!.Status);
        Assert.Equal("passed", awaiting.Stages[0].Checks[0].Status);

        await workflow.AddTaskAsync(new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));

        var afterAdd = await GetQuerier().GetStatusAsync(_workflowId!);
        // The AwaitingApproval is cleared and the new rebase task is
        // dispatchable. The runner is still assigned, so the run lands
        // on Ready (no in-flight work yet).
        Assert.Equal("ready", afterAdd!.Status);
        Assert.Equal("pending", afterAdd.Stages[0].Checks[0].Status);

        var (rebase, rebaseRunnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("rebase.", rebase.WorkId);
        await ReportAsync(rebaseRunnerId, rebase.WorkId, "completed");

        var (rerunCheck, rerunCheckRunnerId) = await PollWorkAnyAsync();
        Assert.Equal("checks", rerunCheck.WorkType);
        await ReportChecksPassAsync(rerunCheckRunnerId, rerunCheck, "check-1");
    }

    [Fact]
    public async Task FailedTask_FailsWorkflow()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("rebase", "Rebase", "mohist/rebase")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (rebase, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, rebase.WorkId, new WorkResult("failed", "rebase failed", Output: JSON.DeserializeElement("""
        {
          "kind": "rebase",
          "status": "failed",
          "baseBranch": "main",
          "rebased": false,
          "conflicts": [],
          "resolveAttempts": 0
        }
        """)));

        var status = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetRunStatusAsync();
        Assert.Equal("Failed", status);
    }

    [Fact]
    public async Task LoadTaskFails_WorkflowFails()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("load-tasks", "Load tasks", "spec/load")],
                [new("check-1", "Check 1", "spec/check")])
        ]));

        var (load, runnerId) = await PollWorkAnyAsync();
        Assert.StartsWith("load-tasks.", load.WorkId);

        await ReportAsync(runnerId, load.WorkId, "failed", "loader failed");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);

        var status = await _fixture.Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetRunStatusAsync();
        Assert.Equal("Failed", status);
    }

}
