using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowGrain3")]
public class StatusSpecs : WorkflowGrainSpecs
{
    public StatusSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusShowsCurrentStage()
    {
        await StartWorkflowAsync(SingleStage());

        var (_, r1) = await PollWorkAnyAsync();

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

        Assert.NotNull(status);
        Assert.Equal("running", status.Status);
        Assert.Equal("build", status.CurrentStage);
        Assert.Single(status.Stages);
        Assert.Equal("build", status.Stages[0].Stage);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusBeforeRunnerAssignmentIsPending()
    {
        var workflow = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

        Assert.NotNull(status);
        // Started, has dispatchable work, no runner claimed it yet →
        // Pending in the new state machine.
        Assert.Equal("pending", status.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusShowsPendingWork()
    {
        var wf = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
            checks: []));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.NotNull(status.PendingWork);
        Assert.Equal("task", status.PendingWork.WorkType);

        await ReportAsync(r1, task1.WorkId, "completed");

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task2.WorkId);

        var status2 = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status2!.PendingWork);
        Assert.Equal("Task 2", status2.PendingWork.Title);

        await ReportAsync(r2, task2.WorkId, "completed");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusShowsTasksChecksAndApproval()
    {
        await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

        Assert.NotNull(status);
        Assert.Equal("awaiting-approval", status.Status);
        var planStage = status.Stages.Find(s => s.Stage == "plan");
        Assert.NotNull(planStage);
        Assert.Equal("awaiting-approval", planStage.Status);
        Assert.Single(planStage.Tasks);
        Assert.Equal("completed", planStage.Tasks[0].Status);
        Assert.Single(planStage.Checks);
        Assert.Equal("passed", planStage.Checks[0].Status);
        Assert.NotNull(planStage.ApprovalStatus);
        Assert.Null(planStage.ApprovalStatus.Result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowDoesNotStoreIssueOrWorkspaceContext()
    {
        var wf = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await wf.StartAsync(TestInput());

        var status = await GetQuerier().GetStatusAsync(_workflowId!);

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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowStatusToleratesUnknownLegacyFailureReason()
    {
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        await SeedWorkflowTemplateAsync(workflowId, SingleStage());

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
                    Metadata = new { CreatedAt = TestTime.UtcNow },
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

        var status = await GetQuerier().GetStatusAsync(workflowId);

        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);
        Assert.Equal("legacy failure", status.Failure?.Message);
    }
}
