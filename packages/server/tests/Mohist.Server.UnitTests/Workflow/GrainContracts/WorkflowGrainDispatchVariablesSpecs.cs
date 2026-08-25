using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Dispatch rendering (title preservation, variable templates vs rendered
/// agent variables), runtime-task insertion ordering, check invalidation, and
/// load/task failure terminality. Dispatches are produced through the
/// production translation seam so every assertion reads the same Variables /
/// With payload a cluster poll would deliver (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainDispatchVariablesSpecs
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainDispatchVariablesSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TaskDispatch_DoesNotInjectDisplayTitleIntoWith()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-title",
            SingleStage([new(
                "integrate:open-pr",
                "Open or update GitHub PR",
                "mohist/create-pull-request",
                new Dictionary<string, JsonElement?>
                {
                    ["source"] = JsonSerializer.SerializeToElement("mohist/run-1"),
                    ["target"] = JsonSerializer.SerializeToElement("master"),
                    ["remote"] = JsonSerializer.SerializeToElement("origin"),
                    ["titleFrom"] = JsonSerializer.SerializeToElement("issue.title"),
                    ["bodyFrom"] = JsonSerializer.SerializeToElement("issue.body"),
                })]));

        var dispatch = await ClaimDispatchAsync(arrangement);

        Assert.Equal("Open or update GitHub PR", dispatch.Title);
        Assert.NotNull(dispatch.With);
        using var with = JsonDocument.Parse(dispatch.With!);
        Assert.False(with.RootElement.TryGetProperty("title", out _));
        Assert.Equal("issue.title", with.RootElement.GetProperty("titleFrom").GetString());
        Assert.Equal("issue.body", with.RootElement.GetProperty("bodyFrom").GetString());
    }

    [Fact]
    public async Task StageWithDynamicTasks_TaskWithContract_DispatchPreservesWithContract()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-contract",
            SingleStage([new("load-tasks", "Load tasks", "spec/load")], []));

        var load = (await arrangement.AssignAndClaimAsync())!;

        var withJson = JsonSerializer.SerializeToElement(new
        {
            prompt = "Add the feature flag service.\n- service is registered",
        });
        var expectJson = JsonSerializer.SerializeToElement(new
        {
            files = new[] { new { path = "src/FeatureFlags.cs" } },
            markers = new[]
            {
                new { path = "openspec/changes/issue-1/tasks.json", contains = "\"passes\": true" },
            },
        });

        await arrangement.Grain.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", withJson, expectJson),
        ]));

        await arrangement.ReportCompletedAsync(load);

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, claimed);

        Assert.StartsWith("T-001.", dispatch.WorkId);
        Assert.Equal("mohist/opencode", dispatch.Uses);
        Assert.NotNull(dispatch.With);
        Assert.Contains("Add the feature flag service.", dispatch.With);
        Assert.Contains("service is registered", dispatch.With);
        Assert.NotNull(dispatch.Expect);
        Assert.Contains("contains", dispatch.Expect!);
        Assert.Contains("src/FeatureFlags.cs", dispatch.Expect!);
        Assert.DoesNotContain("expect", dispatch.With!);
    }

    [Fact]
    public async Task StageWithDynamicAgentVariables_LoadedDynamicTasksInheritStageAgent()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-dynamic-stage-agent",
            SingleStage([new("load-tasks", "Load tasks", "spec/load")], []));
        await PatchIssueVariablesAsync(arrangement, new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new { agent = new { model = "kimi-for-coding/k2p6" } }),
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { agent = new { model = "openai/gpt-5.4" } })),
            }));

        var load = (await arrangement.AssignAndClaimAsync())!;

        await arrangement.Grain.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem(
                "T-001",
                "Implement feature",
                "mohist/opencode",
                JsonSerializer.Deserialize<JsonElement>("""{"prompt":"Implement feature","options":"${{ vars.agent }}"}""")),
        ]));

        await arrangement.ReportCompletedAsync(load);

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, claimed);

        Assert.StartsWith("T-001.", dispatch.WorkId);
        Assert.Contains("${{ vars.agent }}", dispatch.With);
        Assert.DoesNotContain("openai/gpt-5.4", dispatch.With);
        Assert.DoesNotContain("kimi-for-coding/k2p6", dispatch.With);
        Assert.NotNull(dispatch.Variables);
        using var varsDoc = JsonDocument.Parse(dispatch.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("openai/gpt-5.4", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageWithAgentVariables_TaskWithoutAgentInheritsStageAgentAtDispatch()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-inherit-stage-agent",
            SingleStage([new(
                "T-001",
                "Implement feature",
                "mohist/opencode",
                new Dictionary<string, JsonElement?>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("Implement feature"),
                    ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}"),
                })],
                []));
        await PatchIssueVariablesAsync(arrangement, new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new { agent = new { model = "kimi-for-coding/k2p6" } }),
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { agent = new { model = "openai/gpt-5.4" } })),
            }));

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, claimed);

        Assert.StartsWith("T-001.", dispatch.WorkId);
        Assert.Contains("Implement feature", dispatch.With);
        Assert.Contains("${{ vars.agent }}", dispatch.With);
        Assert.DoesNotContain("openai/gpt-5.4", dispatch.With);
        Assert.DoesNotContain("kimi-for-coding/k2p6", dispatch.With);
        Assert.NotNull(dispatch.Variables);
        using var varsDoc = JsonDocument.Parse(dispatch.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("openai/gpt-5.4", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageWithAgentVariables_TaskAgentTemplatePreservedAndSnapshotCarriesStageAgent()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-template-preserved",
            new WorkflowDefinition(
            [
                new StageDefinition(
                    "check",
                    [new TaskDefinition(
                        "ai-review",
                        "AI review",
                        "mohist/opencode",
                        new Dictionary<string, JsonElement?>
                        {
                            ["session"] = JsonSerializer.SerializeToElement("check"),
                            ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.review }}"),
                            ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}"),
                        })],
                    []),
            ]));
        await PatchIssueVariablesAsync(arrangement, new VariableBundle(
            Stages: new Dictionary<string, StageVariables>
            {
                ["check"] = new(JsonSerializer.SerializeToElement(new { agent = new { type = "opencode", model = "openai/gpt-5.5" } })),
            }));

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, claimed);

        Assert.StartsWith("ai-review.", dispatch.WorkId);
        using var with = JsonDocument.Parse(dispatch.With!);
        Assert.Equal("${{ vars.agent }}", with.RootElement.GetProperty("options").GetString());
        Assert.Equal("${{ prompts.review }}", with.RootElement.GetProperty("prompt").GetString());
        Assert.DoesNotContain("kimi-for-coding/k2p6", dispatch.With);
        Assert.DoesNotContain("openai/gpt-5.5", dispatch.With);
        Assert.NotNull(dispatch.Variables);
        using var varsDoc = JsonDocument.Parse(dispatch.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StageAgentVariableUpdate_DispatchedTaskInheritsLatestIssueAgentModel()
    {
        // After T-003 the runtime reads the issue layer directly. Patching
        // the issue layer (the T1 snapshot) is what surfaces in dispatch.
        var arrangement = await ArrangeAsync(
            "wr-dl-latest-agent",
            SingleStage([new(
                "T-001",
                "Implement feature",
                "mohist/opencode",
                new Dictionary<string, JsonElement?>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("Implement feature"),
                    ["options"] = JsonSerializer.SerializeToElement("${{ vars.agent }}"),
                })],
                []));

        await PatchIssueVariablesAsync(arrangement, new VariableBundle(
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new
                {
                    agent = new { type = "opencode", model = "minimax-coding-plan/MiniMax-M3" },
                })),
            }));

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, claimed);

        Assert.StartsWith("T-001.", dispatch.WorkId);
        Assert.Contains("${{ vars.agent }}", dispatch.With);
        Assert.DoesNotContain("minimax-coding-plan/MiniMax-M3", dispatch.With);
        Assert.DoesNotContain("old-coding/legacy", dispatch.With);
        Assert.NotNull(dispatch.Variables);
        using var varsDoc = JsonDocument.Parse(dispatch.Variables!);
        var agent = varsDoc.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("minimax-coding-plan/MiniMax-M3", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task RunningWorkflow_CanAcceptRuntimeTaskBeforeChecks()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-runtime-before-checks",
            SingleStage(checks: [new("check-1", "Check 1", "spec/check")]));

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);

        await arrangement.Grain.AddTaskAsync(
            new RuntimeTaskInput("runtime-1", "Runtime 1", "spec/runtime", JsonSerializer.SerializeToElement(new { value = "one" })));

        var runtimeItem = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("runtime-1.", runtimeItem.Id);
        Assert.Equal("spec/runtime", runtimeItem.Uses);
        var runtimeDispatch = await ToDispatchAsync(arrangement, runtimeItem);
        Assert.Contains("one", runtimeDispatch.With);

        await arrangement.ReportCompletedAsync(runtimeItem);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.Equal("checks", check!.WorkType);
        await arrangement.ReportChecksPassAsync(check, "check-1");
    }

    [Fact]
    public async Task AddTaskAsync_InsertsAsNextTaskBeforeExistingPendingTask()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-insert-next",
            SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
                checks: [new("check-1", "Check 1", "spec/check")]));

        var task1 = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task1);

        await arrangement.Grain.AddTaskAsync(
            new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));

        var rebase = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("rebase.", rebase.Id);
        await arrangement.ReportCompletedAsync(rebase);

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-2.", task2!.Id);
    }

    [Fact]
    public async Task AddTaskAsync_WhenTaskIsRunning_InsertsAfterRunningTaskBeforeOtherPendingTasks()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-insert-after-running",
            SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
                checks: [new("check-1", "Check 1", "spec/check")]));

        var task1 = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.Grain.AddTaskAsync(
            new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));
        await arrangement.ReportCompletedAsync(task1);

        var rebase = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("rebase.", rebase.Id);
        await arrangement.ReportCompletedAsync(rebase);

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-2.", task2!.Id);
    }

    [Fact]
    public async Task RuntimeTaskWithInvalidateChecks_ReopensStageChecks()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-invalidates-checks",
            SingleStage(
                checks: [new("check-1", "Check 1", "spec/check")],
                requiresApproval: true));

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);
        var check = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(check, "check-1");
        var awaiting = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.Equal("awaiting-approval", awaiting!.Status);
        Assert.Equal("passed", awaiting.Stages[0].Checks[0].Status);

        await arrangement.Grain.AddTaskAsync(
            new RuntimeTaskInput("rebase", "Rebase", "mohist/rebase", InvalidateChecks: true));

        var afterAdd = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        // The AwaitingApproval is cleared and the new rebase task is
        // dispatchable. The runner is still assigned, so the run lands
        // on Ready (no in-flight work yet).
        Assert.Equal("ready", afterAdd!.Status);
        Assert.Equal("pending", afterAdd.Stages[0].Checks[0].Status);

        var rebase = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("rebase.", rebase.Id);
        await arrangement.ReportCompletedAsync(rebase);

        var rerunCheck = await arrangement.AssignAndClaimAsync();
        Assert.Equal("checks", rerunCheck!.WorkType);
        await arrangement.ReportChecksPassAsync(rerunCheck, "check-1");
    }

    [Fact]
    public async Task FailedTask_FailsWorkflow()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-failed-task",
            SingleStage(
                tasks: [new("rebase", "Rebase", "mohist/rebase")],
                checks: [new("check-1", "Check 1", "spec/check")]));

        var rebase = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            rebase,
            JsonSerializer.SerializeToElement(new
            {
                kind = "rebase",
                status = "failed",
                baseBranch = "main",
                rebased = false,
                conflicts = Array.Empty<object>(),
                resolveAttempts = 0,
            }),
            addTasks: null,
            status: TaskReportStatus.Failed);

        Assert.Equal("Failed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task LoadTaskFails_WorkflowFails()
    {
        var arrangement = await ArrangeAsync(
            "wr-dl-load-fails",
            SingleStage(
                tasks: [new("load-tasks", "Load tasks", "spec/load")],
                checks: [new("check-1", "Check 1", "spec/check")]));

        var load = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("load-tasks.", load.Id);
        await arrangement.ReportFailedAsync(load, "loader failed");

        Assert.Equal("Failed", await arrangement.Grain.GetRunStatusAsync());
    }

    private Task<WorkflowGrainArrangement> ArrangeAsync(
        string runId,
        WorkflowDefinition? definition = null,
        string? stageId = null)
    {
        definition ??= SingleStage();
        return WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition, TimeProvider);
    }

    /// <summary>Claims the next item and renders it through the production translation seam.</summary>
    private static async Task<WorkDispatch> ToDispatchAsync(
        WorkflowGrainArrangement arrangement,
        WorkItem item)
    {
        var run = await arrangement.Store.LoadAsync(arrangement.RunId)
            ?? throw new InvalidOperationException("run missing");
        return await arrangement.Translator.TranslateToDispatchAsync(
            item, arrangement.RunId, run, arrangement.WorkerId);
    }

    /// <summary>Claims the next item and returns its rendered dispatch.</summary>
    private static async Task<WorkDispatch> ClaimDispatchAsync(WorkflowGrainArrangement arrangement)
    {
        var item = (await arrangement.AssignAndClaimAsync())!;
        return await ToDispatchAsync(arrangement, item);
    }

    private async Task PatchIssueVariablesAsync(WorkflowGrainArrangement arrangement, VariableBundle patch)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        const int issueNumber = 1;
        await using (var db = new MohistDbContext(options))
        {
            if (!await db.Issues.AnyAsync(row => row.ProjectId == arrangement.ProjectId && row.Number == issueNumber))
            {
                db.Issues.Add(new IssueRow
                {
                    State = IssueStore.Serialize(new DomainIssue
                    {
                        ProjectId = arrangement.ProjectId,
                        Number = issueNumber,
                        Title = $"Issue {issueNumber}",
                        Priority = "p2",
                    }),
                });
                await db.SaveChangesAsync();
            }
        }
        var factory = new PooledDbContextFactory<MohistDbContext>(options);
        var store = new IssueVariableStore(factory);
        await store.PatchVariablesAsync(arrangement.ProjectId, issueNumber, patch);
    }

    [Fact]
    public async Task RuntimeTaskWithPlaceholder_RetryAfterVariableChange_UsesNewValue()
    {
        var arrangement = await ArrangeAsync(
            "wr-dvs-placeholder-retry",
            SingleStage(
                tasks: [new("load-tasks", "Load tasks", "spec/load")],
                checks: [new("check-1", "Check 1", "spec/check")]));
        await PatchIssueVariablesAsync(arrangement, new VariableBundle(
            Vars: JsonSerializer.SerializeToElement(new { agent = new { type = "opencode", model = "model-a" } })));

        var loadItem = (await arrangement.AssignAndClaimAsync())!;
        var loadDispatch = await ToDispatchAsync(arrangement, loadItem);
        await arrangement.Grain.AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", JsonSerializer.Deserialize<JsonElement>("""
                    {"options":"${{ vars.agent }}"}
                    """))
            ]));
        await arrangement.ReportCompletedAsync(loadItem);

        var dynamicItem = (await arrangement.AssignAndClaimAsync())!;
        var dynamicTask = await ToDispatchAsync(arrangement, dynamicItem);
        Assert.StartsWith("T-001.1", dynamicTask.WorkId);
        Assert.Contains("${{ vars.agent }}", dynamicTask.With);
        Assert.DoesNotContain("model-a", dynamicTask.With);
        Assert.NotNull(dynamicTask.Variables);
        using (var firstVars = JsonDocument.Parse(dynamicTask.Variables!))
            Assert.Equal("model-a", firstVars.RootElement.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());

        await arrangement.ReportFailedAsync(dynamicItem, "expected flaky");
        await PatchIssueVariablesAsync(arrangement, new VariableBundle(
            Stages: new Dictionary<string, StageVariables>
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { agent = new { type = "opencode", model = "model-b" } }))
            }));
        await arrangement.Grain.RetryAsync();

        var retriedItem = (await arrangement.AssignAndClaimAsync())!;
        var retriedTask = await ToDispatchAsync(arrangement, retriedItem);
        Assert.StartsWith("T-001.2", retriedTask.WorkId);
        Assert.Contains("${{ vars.agent }}", retriedTask.With);
        Assert.DoesNotContain("model-a", retriedTask.With);
        Assert.DoesNotContain("model-b", retriedTask.With);
        Assert.NotNull(retriedTask.Variables);
        using (var retryVars = JsonDocument.Parse(retriedTask.Variables!))
            Assert.Equal("model-b", retryVars.RootElement.GetProperty("vars").GetProperty("agent").GetProperty("model").GetString());

        await arrangement.ReportCompletedAsync(retriedItem);
        var checkItem = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(checkItem, "check-1");
    }

    [Fact]
    public async Task RuntimeTaskWithBakedLiteral_Retry_UsesBakedValue()
    {
        var arrangement = await ArrangeAsync(
            "wr-dvs-baked-retry",
            SingleStage(
                tasks: [new("load-tasks", "Load tasks", "spec/load")],
                checks: [new("check-1", "Check 1", "spec/check")]));

        var loadItem = (await arrangement.AssignAndClaimAsync())!;
        var loadDispatch = await ToDispatchAsync(arrangement, loadItem);
        await arrangement.Grain.AddTasksAsync(
            new AddTasksBatchRequest([
                new AddTasksBatchItem("T-001", "Implement feature", "mohist/opencode", JsonSerializer.Deserialize<JsonElement>("""
                    {"options":{"type":"opencode","model":"model-a"}}
                    """))
            ]));
        await arrangement.ReportCompletedAsync(loadItem);

        var dynamicItem = (await arrangement.AssignAndClaimAsync())!;
        var dynamicTask = await ToDispatchAsync(arrangement, dynamicItem);
        Assert.StartsWith("T-001.1", dynamicTask.WorkId);
        Assert.Contains("model-a", dynamicTask.With);
        await arrangement.ReportFailedAsync(dynamicItem, "expected flaky");
        await arrangement.Grain.RetryAsync();

        var retriedItem = (await arrangement.AssignAndClaimAsync())!;
        var retriedTask = await ToDispatchAsync(arrangement, retriedItem);
        Assert.StartsWith("T-001.2", retriedTask.WorkId);
        Assert.Contains("model-a", retriedTask.With);
        Assert.DoesNotContain("model-b", retriedTask.With);
        await arrangement.ReportCompletedAsync(retriedItem);

        var checkItem = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(checkItem, "check-1");
    }

    private static WorkflowDefinition SingleStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null,
        bool requiresApproval = false,
        string stageId = "build") => new(
    [
        new StageDefinition(
            stageId,
            tasks ?? [new("task-1", "Task 1", "spec/task")],
            checks ?? [new("check-1", "Check 1", "spec/check")],
            RequiresApproval: requiresApproval),
    ]);
}
