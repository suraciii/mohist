using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class CheckRetrySpecs : WorkflowGrainSpecs
{
    public CheckRetrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static WorkflowDefinition StageWithRetryCheck(int retryLimit = 2) =>
        new("spec/workflow", [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check",
                    OnFailure: new CheckFailureAction(new CheckFailureRetry(retryLimit, new TaskDefinition("fix-check", "Fix check", "spec/fix"))))])
        ]);

    [Fact]
    public async Task CheckFails_RetryTaskRunsBeforeRecheck()
    {
        await StartWorkflowAsync(StageWithRetryCheck(retryLimit: 2));

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
        Assert.NotEqual(checks1.WorkId, checks2.WorkId);
        await ReportChecksPassAsync(r4, checks2, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task CheckFailsRepeatedly_RetryTaskRunsEachTime()
    {
        await StartWorkflowAsync(StageWithRetryCheck(retryLimit: 3));

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

    [Fact]
    public async Task CheckFailsBeyondRetryLimit_WorkflowFailsWithoutInjectingAnotherRepairTask()
    {
        await StartWorkflowAsync(StageWithRetryCheck(retryLimit: 2));

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
        Assert.Null(await runner.PollAsync());

        var status = await Grains.GetGrain<IWorkflowGrain>(_workflowId!).GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.Null(status.PendingWork);
        var build = Assert.Single(status.Stages);
        Assert.DoesNotContain(build.Tasks, t => t.Id.StartsWith("fix-check:3."));
    }

    [Fact]
    public void CheckRetryCount_IsStageCheckStateAndSurvivesSnapshotRestore()
    {
        var definition = StageWithRetryCheck(retryLimit: 2);
        var run = WorkflowRun.Create("wf-domain", definition);

        run.Start();
        run.InitStage([new("task-1", "Task 1", "spec/task")], definition.Stages[0].Checks);
        run.CompleteTask();
        run.InjectRetryTask("check-1", new("fix-check", "Fix check", "spec/fix"));

        var currentStage = run.Stages.First(s => s.StageId == run.CurrentStageId);
        Assert.Equal(1, currentStage.Checks.Single(c => c.Name == "check-1").RetryCount);

        var jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(run, jsonOptions);
        var restored = JsonSerializer.Deserialize<WorkflowRun>(json, jsonOptions)!;
        Assert.Equal(1, restored.RetryCountForCheck("check-1"));
    }

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
        Assert.Null(await runner.PollAsync());
    }
}
