using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public sealed class DispatcherDeliverySignalSpecs
{
    [Fact]
    public async Task DeliveryAck_RequiresSuccessfulInvocationAndDurableSettlement()
    {
        var signals = new DispatcherDeliverySignals();
        var envelope = CreateEvent("complete-event-id", "/mohist/agent-job/complete");
        var events = new CapturingEventStore();
        await events.AppendAsync(envelope);
        var key = DispatcherDeliveryKey.From(
            Assert.Single(await events.ListUndeliveredAsync()),
            DispatcherHandler.CatchAll);
        var delivery = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(signals, key);

        signals.RecordSettlement(key);
        Assert.False(delivery.IsCompleted);

        signals.RecordInvocation(key.EventKey);
        await delivery;
    }

    [Fact]
    public async Task DeliveryAck_RejectsWrongSource()
    {
        await AssertWrongKeyDoesNotSettleAsync(
            target => CreateEvent(target.Id, "/mohist/agent-job/other-source"),
            DispatcherHandler.CatchAll);
    }

    [Fact]
    public async Task DeliveryAck_RejectsWrongType()
    {
        await AssertWrongKeyDoesNotSettleAsync(
            target => CreateEvent(target.Id, target.Source.ToString(), "com.mohist.agent-job.other"),
            DispatcherHandler.CatchAll);
    }

    [Fact]
    public async Task DeliveryAck_RejectsWrongEventId()
    {
        await AssertWrongKeyDoesNotSettleAsync(
            target => CreateEvent("other-event-id", target.Source.ToString()),
            DispatcherHandler.CatchAll);
    }

    [Fact]
    public async Task DeliveryAck_RejectsWrongHandler()
    {
        await AssertWrongKeyDoesNotSettleAsync(
            target => target,
            DispatcherHandler.Specific);
    }

    [Fact]
    public async Task Append_RejectsUnknownSourceWithoutCreatingWorkflowRow()
    {
        var events = new CapturingEventStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => events.AppendAsync(CreateEvent(
            "unknown-source-event",
            "/mohist/unknown/aggregate")));

        Assert.Equal(0, events.PendingCount);
    }

    private static async Task AssertWrongKeyDoesNotSettleAsync(
        Func<CloudEvent, CloudEvent> mutate,
        DispatcherHandler reportedHandler)
    {
        var signals = new DispatcherDeliverySignals();
        var events = new CapturingEventStore();
        var target = CreateEvent("target-event-id", "/mohist/agent-job/target");
        var sibling = CreateEvent("sibling-event-id", "/mohist/agent-job/sibling");
        events.SettlementObserver = row =>
            EventDispatcherImmediateTriggerTestSupport.RecordEventSettlement(signals, row);
        await events.AppendAsync(target);
        await events.AppendAsync(sibling);
        var targetRow = Assert.Single(
            await events.ListUndeliveredAsync(),
            row => row.EventId == target.Id);

        var targetKey = DispatcherDeliveryKey.From(targetRow, DispatcherHandler.CatchAll);
        var invocation = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerInvocationAsync(
            signals,
            targetKey);
        var delivery = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(
            signals,
            targetKey);

        EventDispatcherImmediateTriggerTestSupport.RecordHandlerInvocation(
            signals,
            reportedHandler,
            mutate(target));
        Assert.False(delivery.IsCompleted);
        Assert.Equal(2, events.PendingCount);

        EventDispatcherImmediateTriggerTestSupport.RecordHandlerInvocation(
            signals,
            DispatcherHandler.CatchAll,
            target);
        await invocation;
        Assert.False(delivery.IsCompleted);

        await events.MarkDispatchedAsync(
            EventOrigin.AgentJob,
            target.Source.ToString(),
            1,
            DateTimeOffset.UnixEpoch);
        await delivery;
        var remaining = Assert.Single(await events.ListUndeliveredAsync());
        Assert.Equal(sibling.Id, remaining.EventId);
        Assert.Equal(sibling.Source.ToString(), remaining.Source);
        Assert.Equal(EventOrigin.AgentJob, remaining.Origin);
    }

    [Fact]
    public async Task DeliveryAck_RejectsWrongPersistedOriginAndRowId()
    {
        var signals = new DispatcherDeliverySignals();
        var events = new CapturingEventStore();
        var target = CreateEvent("persisted-key-event", "/mohist/agent-job/persisted-key");
        events.SettlementObserver = row =>
            EventDispatcherImmediateTriggerTestSupport.RecordEventSettlement(signals, row);
        await events.AppendAsync(target);
        var row = Assert.Single(await events.ListUndeliveredAsync());
        var targetKey = DispatcherDeliveryKey.From(row, DispatcherHandler.CatchAll);
        var delivery = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(
            signals,
            targetKey);

        EventDispatcherImmediateTriggerTestSupport.RecordHandlerInvocation(
            signals,
            DispatcherHandler.CatchAll,
            target);
        signals.RecordSettlement(targetKey with { Origin = EventOrigin.WorkflowRun });
        signals.RecordSettlement(targetKey with { RowId = targetKey.RowId + 1 });

        Assert.False(delivery.IsCompleted);
        await events.MarkDispatchedAsync(row.Origin, row.Source, row.Id, DateTimeOffset.UnixEpoch);

        await delivery;
    }

    [Fact]
    public void DeliveryAck_RejectsAmbiguousRowsForOneCloudEventIdentity()
    {
        var signals = new DispatcherDeliverySignals();
        var target = new DispatcherDeliveryKey(
            EventOrigin.AgentJob,
            7,
            "/mohist/agent-job/ambiguous",
            EventCatalog.ReverseDns.AgentJobFailed,
            "ambiguous-event",
            DispatcherHandler.CatchAll);
        _ = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(signals, target);

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(
                signals,
                target with { RowId = target.RowId + 1 });
        });

        Assert.Contains("more than one persisted dispatcher row", error.Message, StringComparison.Ordinal);
    }

    private static CloudEvent CreateEvent(
        string eventId,
        string source,
        string type = EventCatalog.ReverseDns.AgentJobFailed) =>
        new(
            id: eventId,
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: DateTimeOffset.UnixEpoch,
            data: null);
}
