using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class CheckRetrySpecs : WorkflowGrainSpecs
{
    public CheckRetrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static WorkflowDefinition StageWithRepairCheck(int repairLimit = 2) =>
        new("spec/workflow", [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check",
                    OnFailure: new CheckFailureAction(new CheckFailureRepair(repairLimit, new TaskDefinition("fix-check", "Fix check", "spec/fix"))))])
        ]);

    private static WorkflowDefinition StageWithRepairAndVerifyCheck() =>
        new("spec/workflow", [
            new StageDefinition("check",
                [new("ai-review", "AI review", "spec/review")],
                [new("review-passed", "Review passed", "spec/marker",
                    OnFailure: new CheckFailureAction(new CheckFailureRepair(
                        2,
                        new TaskDefinition("fix-review-findings", "Fix review findings", "spec/fix-review"))))])
        ]);

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_RepairTaskRunsBeforeRecheck()
    {
        await StartWorkflowAsync(StageWithRepairCheck(repairLimit: 2));

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "check-1", "needs fix");

        var (fixTask, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("fix-check:", fixTask.WorkId);
        Assert.Equal("spec/fix", fixTask.Uses);
        await ReportAsync(r3, fixTask.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksPassAsync(r4, checks2, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Null(await runner.PollAsync(Services));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_RepairTaskRunsThenCheckReRuns()
    {
        await StartWorkflowAsync(StageWithRepairAndVerifyCheck());

        var (review1, r1) = await PollWorkAnyAsync();
        Assert.Equal("ai-review.1", review1.WorkId);
        await ReportAsync(r1, review1.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "review-passed", "review failed");

        var (fix, r3) = await PollWorkAnyAsync();
        Assert.Equal("fix-review-findings:1.1", fix.WorkId);
        await ReportAsync(r3, fix.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksPassAsync(r4, checks2, "review-passed");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Null(await runner.PollAsync(Services));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_RepairTaskIsOnlyInjectedTaskBeforeRecheck()
    {
        // The check-repair path is exactly [repairTask] with no verify step.
        // After the repair task completes, the check is re-run directly.
        var captured = new List<string>();
        await StartWorkflowAsync(StageWithRepairAndVerifyCheck());

        var (review1, r1) = await PollWorkAnyAsync();
        captured.Add(review1.WorkId);
        await ReportAsync(r1, review1.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        captured.Add(checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "review-passed", "review failed");

        var (fix, r3) = await PollWorkAnyAsync();
        captured.Add(fix.WorkId);
        Assert.Equal("fix-review-findings:1.1", fix.WorkId);
        await ReportAsync(r3, fix.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        captured.Add(checks2.WorkType);
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksPassAsync(r4, checks2, "review-passed");

        // The captured sequence must be: ai-review.1 -> checks -> fix-review-findings:1.1 -> checks.
        // No additional task (the old verify task) is injected between fix and the re-check.
        Assert.Equal(
            new[] { "ai-review.1", "checks", "fix-review-findings:1.1", "checks" },
            captured);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task MergeReadyRepair_DoesNotInjectPrUpdateBeforeRecheck()
    {
        // Stage the same way as the historical pr-update-after-merge-ready
        // repair, but the verify task concept is gone. After a failed
        // merge-ready check, only the rebase repair task is injected
        // before the checks re-run.
        var definition = new WorkflowDefinition("spec/workflow", [
            new StageDefinition("check",
                [new("ai-review", "AI review", "spec/review")],
                [new("merge-ready", "Merge ready", "spec/merge-ready",
                    OnFailure: new CheckFailureAction(new CheckFailureRepair(
                        1,
                        new TaskDefinition("rebase-onto-base", "Rebase onto base branch", "spec/rebase"))))])
        ]);

        var captured = new List<string>();
        await StartWorkflowAsync(definition);

        var (review, r1) = await PollWorkAnyAsync();
        captured.Add(review.WorkId);
        await ReportAsync(r1, review.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        captured.Add(checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "merge-ready", "base moved");

        var (rebase, r3) = await PollWorkAnyAsync();
        captured.Add(rebase.WorkId);
        Assert.Equal("rebase-onto-base:1.1", rebase.WorkId);
        await ReportAsync(r3, rebase.WorkId, "completed");

        var (checks2, _) = await PollWorkAnyAsync();
        captured.Add(checks2.WorkType);

        Assert.Equal(
            new[] { "ai-review.1", "checks", "rebase-onto-base:1.1", "checks" },
            captured);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ReviewPassedRepairTask_DoesNotReceiveMarkerCheckResult()
    {
        await StartWorkflowAsync(StageWithRepairAndVerifyCheck());

        var (review1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, review1.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks1, "review-passed", "marker missing");

        var (fix, _) = await PollWorkAnyAsync();
        Assert.Equal("fix-review-findings:1.1", fix.WorkId);
        var with = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(fix.With!)!;
        Assert.DoesNotContain("failedCheckResult", with.Keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task NonReviewRepairTask_ReceivesFailedCheckResult()
    {
        await StartWorkflowAsync(StageWithRepairCheck());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks1, "check-1", "needs fix");

        var (fix, _) = await PollWorkAnyAsync();
        Assert.Equal("fix-check:1.1", fix.WorkId);
        var with = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(fix.With!)!;
        Assert.Contains("failedCheckResult", with.Keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFailsRepeatedly_RepairTaskRunsEachTime()
    {
        await StartWorkflowAsync(StageWithRepairCheck(repairLimit: 3));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks1.WorkType);
        await ReportChecksFailAsync(r2, checks1, "check-1", "first fail");

        var (fix1, r3) = await PollWorkAnyAsync();
        Assert.StartsWith("fix-check:", fix1.WorkId);
        await ReportAsync(r3, fix1.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        await ReportChecksFailAsync(r4, checks2, "check-1", "second fail");

        var (fix2, r5) = await PollWorkAnyAsync();
        Assert.StartsWith("fix-check:", fix2.WorkId);
        Assert.NotEqual(fix1.WorkId, fix2.WorkId);
        await ReportAsync(r5, fix2.WorkId, "completed");

        var (checks3, r6) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks3.WorkType);
        await ReportChecksPassAsync(r6, checks3, "check-1");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFailsBeyondRetryLimit_WorkflowFailsWithoutInjectingAnotherRepairTask()
    {
        await StartWorkflowAsync(StageWithRepairCheck(repairLimit: 2));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks1, "check-1", "first fail");

        var (fix1, r3) = await PollWorkAnyAsync();
        Assert.Equal("fix-check:1.1", fix1.WorkId);
        await ReportAsync(r3, fix1.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r4, checks2, "check-1", "second fail");

        var (fix2, r5) = await PollWorkAnyAsync();
        Assert.Equal("fix-check:2.1", fix2.WorkId);
        await ReportAsync(r5, fix2.WorkId, "completed");

        var (checks3, r6) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r6, checks3, "check-1", "third fail");

        var runner = Grains.GetGrain<IRunnerGrain>(r6);
        Assert.Null(await runner.PollAsync(Services));

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);
        Assert.Null(status.PendingWork);
        var build = Assert.Single(status.Stages);
        Assert.DoesNotContain(build.Tasks, t => t.Id.StartsWith("fix-check:3."));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFailsBeyondRetryLimit_UserRetries_InjectsRepairTaskIgnoringRetryLimit()
    {
        var workflow = await StartWorkflowAsync(StageWithRepairCheck(repairLimit: 2));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks1, "check-1", "first fail");

        var (fix1, r3) = await PollWorkAnyAsync();
        Assert.Equal("fix-check:1.1", fix1.WorkId);
        await ReportAsync(r3, fix1.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r4, checks2, "check-1", "second fail");

        var (fix2, r5) = await PollWorkAnyAsync();
        Assert.Equal("fix-check:2.1", fix2.WorkId);
        await ReportAsync(r5, fix2.WorkId, "completed");

        var (checks3, r6) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r6, checks3, "check-1", "third fail");

        await workflow.RetryAsync();

        var (manualFix, r7) = await PollWorkAnyAsync();
        Assert.Equal("fix-check:3.1", manualFix.WorkId);
        Assert.Equal("spec/fix", manualFix.Uses);

        var status2 = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status2);
        Assert.Equal("running", status2.Status);

        await using var db = new MohistDbContext(
            new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_fixture.ConnectionString)
                .Options);
        var runState = await db.WorkflowRuns.FindAsync(_workflowId!);
        Assert.NotNull(runState);
        var run = JsonSerializer.Deserialize<WorkflowRun>(runState!.State, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        })!;
        Assert.Equal(3, run.GetRepairCount("check-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CheckRepairCount_IsStageCheckStateAndSurvivesSnapshotRestore()
    {
        var definition = StageWithRepairCheck(repairLimit: 2);
        var run = WorkflowRun.Create("wf-domain", definition);

        run.Start();
        run.InitializeStage([new("task-1", "Task 1", "spec/task")], definition.Stages[0].Checks);
        run.CompleteTask();
        run.RepairFailedCheck(new("check-1", "fail", "broken"), new("fix-check", "Fix check", "spec/fix"));

        var currentStage = run.Stages.First(s => s.Id == run.CurrentStageId);
        Assert.Equal(1, currentStage.Checks.Single(c => c.Name == "check-1").RepairCount);

        var jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(run, jsonOptions);
        var restored = JsonSerializer.Deserialize<WorkflowRun>(json, jsonOptions)!;
        Assert.Equal(1, restored.GetRepairCount("check-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_NoRetryConfigured_WorkflowFails()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks.WorkType);
        await ReportChecksFailAsync(r2, checks, "check-1", "no retry");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync(Services));
    }
}
