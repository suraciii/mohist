using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.Tests.Workflow.Grain;

[Collection("WorkflowGrain")]
[Trait("level", "L1")]
public sealed class LegacyRunnerLossReminderSpecs : WorkflowGrainSpecs
{
    public LegacyRunnerLossReminderSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task DeliveredLegacyReminderRemovesOnlyItself()
    {
        var workflowRunId = $"wf-{Guid.NewGuid():N}";
        var grain = await StartWorkflowAsync(SingleStage(checks: []), workflowRunId);
        var grainId = grain.GetGrainId();
        var reminders = Services.GetRequiredService<IReminderTable>();
        await reminders.UpsertRow(new ReminderEntry
        {
            GrainId = grainId,
            ReminderName = WorkflowGrain.RunnerLossRecoveryReminderName,
            StartAt = TestTime.UtcNow.AddMinutes(1).UtcDateTime,
            Period = TimeSpan.FromDays(1),
        });
        var before = await LoadRunAsync(workflowRunId);
        var eventCount = EventStore.Appended.Count;

        await grain.AsReference<IRemindable>().ReceiveReminder(
            WorkflowGrain.RunnerLossRecoveryReminderName,
            default);

        Assert.Null(await reminders.ReadRow(grainId, WorkflowGrain.RunnerLossRecoveryReminderName));
        var after = await LoadRunAsync(workflowRunId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.CurrentStageId, after.CurrentStageId);
        Assert.Equal(eventCount, EventStore.Appended.Count);
    }
}
