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

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

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
                new { path = "artifacts/changes/issue-1/tasks.json", contains = "\"passes\": true" },
            },
        });

        await arrangement.Grain.AddTasksAsync(new AddTasksBatchRequest([
            new AddTasksBatchItem("T-001", "Implement feature", "spec/task", withJson, expectJson),
        ]));

        await arrangement.ReportCompletedAsync(load);

        var claimed = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, claimed);

        Assert.StartsWith("T-001.", dispatch.WorkId);
        Assert.Equal("spec/task", dispatch.Uses);
        Assert.NotNull(dispatch.With);
        Assert.Contains("Add the feature flag service.", dispatch.With);
        Assert.Contains("service is registered", dispatch.With);
        Assert.NotNull(dispatch.Expect);
        Assert.Contains("contains", dispatch.Expect!);
        Assert.Contains("src/FeatureFlags.cs", dispatch.Expect!);
        Assert.DoesNotContain("expect", dispatch.With!);
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
    public async Task Dispatch_AfterTaskOutputs_IncludesTaskOutputsInVariables()
    {
        var arrangement = await ArrangeAsync(
            "wr-dvs-task-outputs",
            SingleStage(
                tasks: [new("proposal", "Generate proposal", "spec/task"), new("specs", "Write specs", "spec/task")],
                checks: []));

        var proposal = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("proposal.", proposal.Id);
        await arrangement.ReportTaskResultAsync(
            proposal,
            output: JsonSerializer.SerializeToElement(new
            {
                changeName = "issue-97",
                changeDir = "artifacts/changes/issue-97",
            }),
            addTasks: null);

        var specsItem = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("specs.", specsItem.Id);
        var specs = await ToDispatchAsync(arrangement, specsItem);
        Assert.NotNull(specs.Variables);

        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        var outputs = variables.GetProperty("tasks").GetProperty("proposal").GetProperty("outputs");
        Assert.Equal("issue-97", outputs.GetProperty("changeName").GetString());
        Assert.Equal("artifacts/changes/issue-97", outputs.GetProperty("changeDir").GetString());
    }

    [Fact]
    public async Task Dispatch_AfterCoreProcessOutput_ExposesTypedTaskOutputFields()
    {
        var arrangement = await ArrangeAsync(
            "wr-dvs-core-process-output",
            SingleStage(
                tasks: [new("process", "Run process", "core/process"), new("consume", "Consume process output", "spec/task")],
                checks: []));

        var process = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            process,
            output: JsonSerializer.SerializeToElement(new { stdout = "artifact.zip", exitCode = 0 }),
            addTasks: null);

        var consumeItem = (await arrangement.AssignAndClaimAsync())!;
        var consume = await ToDispatchAsync(arrangement, consumeItem);
        Assert.NotNull(consume.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(consume.Variables);
        var output = variables.GetProperty("tasks").GetProperty("process").GetProperty("outputs");
        Assert.Equal("artifact.zip", output.GetProperty("stdout").GetString());
        Assert.Equal(JsonValueKind.Number, output.GetProperty("exitCode").ValueKind);
        Assert.Equal(0, output.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Dispatch_RuntimeVariablesTakePrecedenceOverLowerPrecedenceSources()
    {
        var arrangement = await ArrangeAsync(
            "wr-dvs-runtime-precedence",
            SingleStage(
                tasks: [new("proposal", "Generate proposal", "spec/task"), new("specs", "Write specs", "spec/task")],
                checks: []));

        var proposal = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportTaskResultAsync(
            proposal,
            output: JsonSerializer.SerializeToElement(new { changeName = "runtime-value" }),
            addTasks: null);

        var specsItem = (await arrangement.AssignAndClaimAsync())!;
        var specs = await ToDispatchAsync(arrangement, specsItem);
        Assert.NotNull(specs.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        var changeName = variables.GetProperty("tasks").GetProperty("proposal").GetProperty("outputs").GetProperty("changeName");
        Assert.Equal("runtime-value", changeName.GetString());
    }

    [Fact]
    public async Task Dispatch_EmptyTaskOutput_DoesNotAlterVariables()
    {
        var arrangement = await ArrangeAsync(
            "wr-dvs-empty-output",
            SingleStage(
                tasks: [new("proposal", "Generate proposal", "spec/task"), new("specs", "Write specs", "spec/task")],
                checks: []));

        var proposal = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(proposal);

        var specsItem = (await arrangement.AssignAndClaimAsync())!;
        var specs = await ToDispatchAsync(arrangement, specsItem);
        Assert.NotNull(specs.Variables);
        var variables = JsonSerializer.Deserialize<JsonElement>(specs.Variables);
        Assert.False(variables.TryGetProperty("tasks", out _));
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
