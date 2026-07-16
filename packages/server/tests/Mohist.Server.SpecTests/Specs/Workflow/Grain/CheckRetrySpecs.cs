using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class CheckRetrySpecs : WorkflowGrainSpecs
{
    public CheckRetrySpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CheckFails_DoesNotInjectRecoveryTask()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "check-1", "no retry");

        var status = await GetQuerier().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckFailed", status.Failure.Reason);
        Assert.Equal("check-1", status.Failure.CheckName);

        var build = Assert.Single(status.Stages);
        Assert.DoesNotContain(build.Tasks, t => t.Id.StartsWith("recover:", StringComparison.Ordinal));

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync(Services));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskLevelRecoveryTasks_RunBeforeRetrySelf()
    {
        var recovery = new RecoveryDefinition(
            1,
            [
                new RecoveryHandlerDefinition(
                    "errorCode=script-failed",
                    [
                        new TaskDefinition(
                            "recover:fix-tests",
                            "Fix failing tests",
                            "spec/fix")
                    ],
                    RetrySelf: true)
            ]);

        await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition("verify", "Verify", "spec/verify", Recovery: recovery),
                new TaskDefinition("next", "Next", "spec/next")
            ],
            checks: []));

        var (verify, r1) = await PollWorkAnyAsync();
        Assert.Equal("verify.1", verify.WorkId);
        Assert.NotNull(verify.Recovery);

        await ReportAsync(r1, verify.WorkId, new WorkResult(
            "completed",
            Output: """{"errorCode":"script-failed"}""",
            AddTasks:
            [
                new RuntimeTaskInput("recover:fix-tests", "Fix failing tests", "spec/fix"),
                new RuntimeTaskInput("verify", "Verify", "spec/verify", Recovery: recovery, RecoveryRemaining: 0)
            ]));

        var (fix, r2) = await PollWorkAnyAsync();
        Assert.Equal("recover:fix-tests.1", fix.WorkId);
        await ReportAsync(r2, fix.WorkId, "completed");

        var (retry, _) = await PollWorkAnyAsync();
        Assert.Equal("verify.2", retry.WorkId);
    }
}
