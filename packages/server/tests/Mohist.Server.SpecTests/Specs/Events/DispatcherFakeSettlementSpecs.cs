using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public sealed class DispatcherFakeSettlementSpecs
{
    [Fact]
    public async Task EventAndDeadLetterSettlement_RejectsWrongSourceAndOrigin()
    {
        var events = new CapturingEventStore();
        var deadLetters = new CapturingDeadLetterStore(events);
        await events.AppendAsync(new CloudEvent(
            id: "agent-job-settlement-event",
            source: new Uri("/mohist/agent-job/settlement", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobFailed,
            time: DateTimeOffset.UnixEpoch,
            data: null));

        var pending = Assert.Single(await events.ListUndeliveredAsync());
        Assert.Equal(EventOrigin.AgentJob, pending.Origin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            events.MarkDispatchedAsync(
                EventOrigin.AgentJob,
                "/mohist/agent-job/other-source",
                pending.Id,
                DateTimeOffset.UnixEpoch));
        var retained = Assert.Single(await events.ListUndeliveredAsync());
        Assert.Equal(pending.Id, retained.Id);
        Assert.Equal(pending.Source, retained.Source);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            events.MarkDispatchedAsync(
                EventOrigin.WorkflowRun,
                pending.Source,
                pending.Id,
                DateTimeOffset.UnixEpoch));
        Assert.Single(await events.ListUndeliveredAsync());

        await events.MarkDispatchedAsync(
            pending.Origin,
            pending.Source,
            pending.Id,
            DateTimeOffset.UnixEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            events.MarkDispatchedAsync(
                pending.Origin,
                pending.Source,
                pending.Id,
                DateTimeOffset.UnixEpoch));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            deadLetters.DeleteAsync(404));
    }

    [Fact]
    public async Task FailedDeadLetterSettlement_DoesNotNotifyDeliveryUntilPersistenceSucceeds()
    {
        var events = new CapturingEventStore();
        var settlementNotifications = 0;
        events.SettlementObserver = _ => settlementNotifications++;
        var deadLetters = new CapturingDeadLetterStore(events)
        {
            ThrowAfterSourceMark = true,
        };
        await events.AppendAsync(new CloudEvent(
            id: "agent-job-dead-letter-event",
            source: new Uri("/mohist/agent-job/dead-letter", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentJobFailed,
            time: DateTimeOffset.UnixEpoch,
            data: null));

        var pending = Assert.Single(await events.ListUndeliveredAsync());
        var deadLetter = BuildDeadLetter(pending);

        await Assert.ThrowsAsync<InvalidOperationException>(() => deadLetters.SettleAsync(
            pending,
            [deadLetter],
            DateTimeOffset.UnixEpoch));

        Assert.Equal(0, settlementNotifications);
        Assert.Single(await events.ListUndeliveredAsync());
        Assert.Empty(deadLetters.Written);

        deadLetters.ThrowAfterSourceMark = false;
        await deadLetters.SettleAsync(pending, [deadLetter], DateTimeOffset.UnixEpoch);

        Assert.Equal(1, settlementNotifications);
        Assert.Empty(await events.ListUndeliveredAsync());
        Assert.Single(deadLetters.Written);
    }

    private static DeadLetterRow BuildDeadLetter(UndeliveredEvent sourceEvent) => new()
    {
        Origin = sourceEvent.Origin.ToString(),
        Id = sourceEvent.Id,
        Source = sourceEvent.Source,
        EventId = sourceEvent.EventId,
        Type = sourceEvent.Type,
        Time = sourceEvent.Time,
        SpecVersion = sourceEvent.SpecVersion,
        Subject = sourceEvent.Subject,
        DataContentType = sourceEvent.DataContentType,
        Data = sourceEvent.Data,
        ExtensionsJson = sourceEvent.ExtensionsJson,
        FailingHandler = "test.handler",
        ErrorMessage = "poison",
        AttemptCount = 1,
        DeadLetteredAt = DateTimeOffset.UnixEpoch,
    };
}
