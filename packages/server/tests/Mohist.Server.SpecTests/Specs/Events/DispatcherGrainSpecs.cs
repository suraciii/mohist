using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Silo-level specs for the dispatcher wiring. Drives the
/// production <see cref="EventDispatcherService"/> through the
/// <see cref="IDispatcherGrain"/>'s <see cref="IDispatcherGrain.PulseAsync"/>
/// entry point and asserts the registered <c>[Subscription]</c> handlers
/// (closed-generic + non-generic + wildcard) observe the published event.
/// Spec: <c>openspec/changes/issue-362/specs/event-dispatch/spec.md</c>.
/// </summary>
[Collection("Dispatcher")]
public class DispatcherGrainSpecs
{
    private static readonly DateTimeOffset EventTime = new(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

    private readonly DispatcherFixture _fixture;

    public DispatcherGrainSpecs(DispatcherFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetInvocationRecords();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PulseAsync_AfterPublish_DeliversToMatchingClosedGenericHandler()
    {
        // Closes out D5 / D8: a closed-generic [Subscription] handler
        // (the same shape EpicAutoDoneHandler uses) is in the fan-out
        // set and is invoked through the dispatcher's typed delegate.
        var envelope = new CloudEvent(
            id: "evt_pulse_closed",
            source: new Uri("/mohist/issues/issue_pulse", UriKind.Relative),
            type: EventCatalog.ReverseDns.IssueCompleted,
            time: EventTime,
            data: System.Text.Json.JsonSerializer.SerializeToElement(
                new IssueCompleted(WorkflowRunId: "wr_pulse"), CloudEvent.JsonOptions),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project_pulse",
                ["issueid"] = "issue_pulse",
            });

        await _fixture.EventPublisher.PublishAsync(envelope);
        Assert.Equal(1, _fixture.EventStore.PendingCount);

        await _fixture.Dispatcher.PulseAsync();

        Assert.Contains("evt_pulse_closed", _fixture.ClosedGenericInvocations);
        Assert.Contains("evt_pulse_closed", _fixture.CatchAllInvocations);
        Assert.DoesNotContain("evt_pulse_closed", _fixture.SpecificInvocations);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PulseAsync_TriggersImmediateTick_BypassesReminderCadence()
    {
        // Closes out D2: PulseAsync is the latency-optimization entry
        // point — the production reminder is configured to ~1s but the
        // spec doesn't need to wait that long. After Pulse, the
        // append was delivered even though no reminder tick fired.
        var envelope = new CloudEvent(
            id: "evt_pulse_immediate",
            source: new Uri("/mohist/issues/issue_pulse_imm", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: EventTime,
            data: null);

        await _fixture.EventPublisher.PublishAsync(envelope);
        Assert.Equal(1, _fixture.EventStore.PendingCount);
        Assert.Empty(_fixture.SpecificInvocations);

        await _fixture.Dispatcher.PulseAsync();

        Assert.Contains("evt_pulse_immediate", _fixture.SpecificInvocations);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task DispatcherGrain_ResolveByFixedKey_ReturnsSingletonActivation()
    {
        // Closes out D1: IDispatcherGrain is reachable under the
        // fixed key "dispatcher" and the call resolves (Orleans'
        // IGrainWithStringKey + IRemindable activation path).
        var second = _fixture.Grains.GetGrain<IDispatcherGrain>("dispatcher");
        await second.PulseAsync();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RedeliverAsync_NoDeadLetter_NoOps()
    {
        // The silo uses a Noop dead-letter store; re-delivery is a
        // no-op. Verifies the grain method resolves and the
        // dispatcher doesn't throw on a missing DL row.
        var result = await _fixture.Dispatcher.RedeliverAsync(deadLetterId: 999_999);

        Assert.False(result.Found);
        Assert.False(result.Delivered);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task OnActivateAsync_RegistersPersistedReminderWithConfiguredCadence()
    {
        // Closes out D2: the dispatcher's OnActivateAsync registers a
        // persisted reminder under the fixed grain id, with the
        // cadence the DispatcherOptions exposes. Reads from the
        // silo's IReminderTable — both the dispatcher's own reminder
        // registration and the table write round-trip.
        var dispatcherGrainId = _fixture.Grains.GetGrain<IDispatcherGrain>("dispatcher")
            .GetGrainId();
        var row = await _fixture.ReminderTable.ReadRow(dispatcherGrainId, "dispatcher-tick");
        Assert.NotNull(row);
        Assert.Equal("dispatcher-tick", row.ReminderName);
        Assert.True(row.Period > TimeSpan.Zero,
            $"Reminder period must be positive (got {row.Period})");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task ReminderCallback_DeliversBeforeAndAfterHostingSiloLoss()
    {
        var dispatcherId = _fixture.Dispatcher.GetGrainId();
        Assert.True(_fixture.Cluster.TryGetGrainContext(dispatcherId, out var initialContext));
        var initialSilo = initialContext.Address.SiloAddress
            ?? throw new InvalidOperationException("Dispatcher activation has no silo address");

        var beforeTick = _fixture.WaitForSpecificInvocationAsync("evt_reminder_before");
        await PublishWorkflowCompletedAsync("evt_reminder_before", "issue_reminder_before");
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(1));
        await AwaitSignalAsync(beforeTick, "dispatcher reminder delivery for evt_reminder_before");
        Assert.Contains("evt_reminder_before", _fixture.SpecificInvocations);

        var hostingSilo = _fixture.Cluster.GetSiloForAddress(initialSilo);
        Assert.NotNull(hostingSilo);
        var reminderReloaded = _fixture.ReminderTable.PrepareRangeReadSignal();
        await _fixture.Cluster.KillSiloAsync(hostingSilo);
        try
        {
            await _fixture.Cluster.WaitForLivenessToStabilizeAsync(didKill: true);
            _fixture.TimeProvider.Advance(TimeSpan.FromHours(1));
            await AwaitSignalAsync(reminderReloaded, "dispatcher reminder reload after silo loss");

            var afterTick = _fixture.WaitForSpecificInvocationAsync("evt_reminder_after");
            await PublishWorkflowCompletedAsync("evt_reminder_after", "issue_reminder_after");
            _fixture.TimeProvider.Advance(TimeSpan.FromHours(1));
            await AwaitSignalAsync(afterTick, "dispatcher reminder delivery for evt_reminder_after");

            Assert.Contains("evt_reminder_after", _fixture.SpecificInvocations);
            Assert.Equal(0, _fixture.EventStore.PendingCount);
            Assert.True(_fixture.Cluster.TryGetGrainContext(dispatcherId, out var recoveredContext));
            Assert.NotEqual(initialSilo, recoveredContext.Address.SiloAddress);
        }
        finally
        {
            await _fixture.Cluster.StartAdditionalSiloAsync();
            await _fixture.Cluster.WaitForLivenessToStabilizeAsync();
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task PoisonEvent_ExhaustsRetries_DeadLettersAndMarksDispatched()
    {
        var envelope = new CloudEvent(
            id: "evt_poison_spec",
            source: new Uri("/mohist/issues/issue_poison", UriKind.Relative),
            type: "test.poison",
            time: EventTime,
            data: null);

        await _fixture.EventPublisher.PublishAsync(envelope);
        await _fixture.Dispatcher.PulseAsync();

        var deadLetters = _fixture.DeadLetterStore.Written;
        var dl = deadLetters.SingleOrDefault(r => r.EventId == "evt_poison_spec");
        Assert.NotNull(dl);
        Assert.Equal("test.poison", dl.Type);
        Assert.Contains("DispatcherPoisonHandler", dl.FailingHandler);
        Assert.Contains("poison test handler", dl.ErrorMessage);
        Assert.NotNull(dl.ErrorStack);
        Assert.Contains("InvalidOperationException", dl.ErrorStack);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    private Task PublishWorkflowCompletedAsync(string eventId, string issueId) =>
        _fixture.EventPublisher.PublishAsync(new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/issues/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: EventTime,
            data: null));

    private static async Task AwaitSignalAsync(Task signal, string description)
    {
        try
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out waiting for {description}.");
        }
    }
}

/// <summary>
/// xUnit collection definition for the dispatcher silo fixture.
/// Serialized because the silo activates the dispatcher grain on
/// first use and the capture lists are shared state.
/// </summary>
[CollectionDefinition("Dispatcher", DisableParallelization = true)]
public class DispatcherCollection : ICollectionFixture<DispatcherFixture>;
