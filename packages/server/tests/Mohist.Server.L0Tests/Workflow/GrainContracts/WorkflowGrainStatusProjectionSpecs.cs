using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// Workflow status projection through the real querier: stage/pending-work
/// views across the run lifecycle, the pending state before any claim,
/// awaiting-approval surfaces, view-type context isolation, and tolerance
/// of legacy failure reasons (#681).
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowGrainStatusProjectionSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainStatusProjectionSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WorkflowStatusShowsCurrentStage()
    {
        var a = await ArrangeAsync("wr-status-current-stage");
        await a.Arrangement.AssignAndClaimAsync();

        var status = await a.Querier.GetStatusAsync(a.RunId);

        Assert.NotNull(status);
        Assert.Equal("running", status.Status);
        Assert.Equal("build", status.CurrentStage);
        Assert.Single(status.Stages);
        Assert.Equal("build", status.Stages[0].Stage);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);
    }

    [Fact]
    public async Task WorkflowStatusBeforeRunnerAssignmentIsPending()
    {
        var a = await ArrangeAsync("wr-status-pending");

        var status = await a.Querier.GetStatusAsync(a.RunId);

        Assert.NotNull(status);
        // Started, has dispatchable work, no runner claimed it yet →
        // Pending in the new state machine.
        Assert.Equal("pending", status.Status);
    }

    [Fact]
    public async Task WorkflowStatusShowsPendingWork()
    {
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            "wr-status-pending-work",
            new WorkflowDefinition([
                new StageDefinition(
                    "build",
                    [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
                    [])
            ]),
            TimeProvider);

        var task1 = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("task-1.", task1.Id);

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);

        await arrangement.ReportCompletedAsync(task1);

        var task2 = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("task-2.", task2.Id);

        var status2 = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status2!.PendingWork);
        Assert.Equal("Task 2", status2.PendingWork.Title);

        await arrangement.ReportCompletedAsync(task2);
    }

    [Fact]
    public async Task WorkflowStatusShowsTasksChecksAndApproval()
    {
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            "wr-status-approval",
            ApprovalStage(),
            TimeProvider);

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);
        var check = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(check, "plan-ok");

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);

        Assert.NotNull(status);
        Assert.Equal("awaiting-approval", status.Status);
        var planStage = status.Stages.Find(stage => stage.Stage == "plan");
        Assert.NotNull(planStage);
        Assert.Equal("awaiting-approval", planStage.Status);
        Assert.Single(planStage.Tasks);
        Assert.Equal("completed", planStage.Tasks[0].Status);
        Assert.Single(planStage.Checks);
        Assert.Equal("passed", planStage.Checks[0].Status);
        Assert.NotNull(planStage.ApprovalStatus);
        Assert.Null(planStage.ApprovalStatus.Result);
        Assert.Contains(status.AvailableActions, action => action.Name == "approve");
        Assert.Contains(status.AvailableActions, action => action.Name == "stop");
        Assert.DoesNotContain(status.AvailableActions, action => action.Name == "request-changes");
    }

    [Fact]
    public async Task WorkflowDoesNotStoreIssueOrWorkspaceContext()
    {
        var a = await ArrangeAsync("wr-status-no-context");

        var status = await a.Querier.GetStatusAsync(a.RunId);

        Assert.NotNull(status);
        // No runner is registered at this point, so the workflow is in
        // Pending (started, has dispatchable work, waiting for a runner
        // to claim it). The new state machine disambiguates that from
        // the assigned/Ready and in-flight/Running states.
        Assert.Equal("pending", status.Status);
        Assert.DoesNotContain("Issue", typeof(WorkflowStatusView).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("Worktree", typeof(WorkflowStatusView).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("ChangeDir", typeof(WorkflowStatusView).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task WorkflowStatusToleratesUnknownLegacyFailureReason()
    {
        var workflowId = "wr-status-legacy-failure";
        var definition = SingleStage([new("task-1", "Task 1", "spec/task")]);
        var projectId = $"prof-{Math.Abs(WorkflowYamlSerializer.ToYaml(definition).GetHashCode()):x8}";
        await WorkflowGrainContractSupport.SeedTemplateAsync(_fixture, projectId, definition, TimeProvider.GetUtcNow());

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;
        await using (var db = new MohistDbContext(options))
        {
            db.WorkflowRuns.Add(new WorkflowRunRow
            {
                WorkflowRunId = workflowId,
                State = JSON.Serialize(new
                {
                    Id = workflowId,
                    Metadata = new { CreatedAt = TimeProvider.GetUtcNow() },
                    Status = "Failed",
                    CurrentStageId = "build",
                    Stages = new[]
                    {
                        new
                        {
                            Id = "build",
                            Attempt = 1,
                            RequiresApproval = false,
                            Status = "Failed",
                            Initialized = true,
                            Tasks = Array.Empty<object>(),
                            Checks = Array.Empty<object>(),
                            Failure = new
                            {
                                Reason = "RemovedReason",
                                Stage = "build",
                                Message = "legacy failure"
                            }
                        }
                    },
                    Failure = new
                    {
                        Reason = "RemovedReason",
                        Stage = "build",
                        Message = "legacy failure"
                    }
                })
            });
            await db.SaveChangesAsync();
        }

        var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowQuerier>();
        var status = await querier.GetStatusAsync(workflowId);

        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);
        Assert.Equal("legacy failure", status.Failure?.Message);
    }

    private async Task<StatusArrangement> ArrangeAsync(string runId)
    {
        var definition = SingleStage([new("task-1", "Task 1", "spec/task")]);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, definition, TimeProvider, workerId: $"runner-{runId}");
        return new StatusArrangement(arrangement);
    }

    private static WorkflowDefinition SingleStage(List<TaskDefinition> tasks) => new(
    [
        new StageDefinition("build", tasks, [new("check-1", "Check 1", "spec/check")]),
    ]);

    private static WorkflowDefinition ApprovalStage() => new(
    [
        new StageDefinition("plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            RequiresApproval: true),
        new StageDefinition("build",
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]),
    ]);

    private sealed record StatusArrangement(WorkflowGrainArrangement Arrangement)
    {
        public WorkflowGrainArrangement Arrangement { get; } = Arrangement;
        public WorkflowQuerier Querier => Arrangement.Querier;
        public string RunId => Arrangement.RunId;
    }
}
