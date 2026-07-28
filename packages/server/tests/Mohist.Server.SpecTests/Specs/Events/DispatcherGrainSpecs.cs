using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Silo-level specs for the dispatcher wiring. Drives the
/// production <see cref="EventDispatcherService"/> through the
/// <see cref="IEventDispatcherGrain"/>'s <see cref="IEventDispatcherGrain.DispatchNowAsync"/>
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

    [Fact]
    public async Task EventDispatcherGrain_DispatchNowRegistersReminderAndRunsCycle()
    {
        var eventId = $"evt_event_dispatcher_{Guid.NewGuid():N}";
        await PublishWorkflowCompletedAsync(eventId, "issue_event_dispatcher");

        await _fixture.EventDispatcher.DispatchNowAsync();

        Assert.Contains(eventId, _fixture.SpecificInvocations);
        Assert.NotNull(await _fixture.ReminderTable.ReadRow(
            _fixture.EventDispatcher.GetGrainId(),
            EventDispatcherGrain.ReminderName));
    }

    [Fact]
    public async Task DispatchNowAsync_AfterPublish_DeliversToMatchingClosedGenericHandler()
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

        await _fixture.EventDispatcher.DispatchNowAsync();

        Assert.Contains("evt_pulse_closed", _fixture.ClosedGenericInvocations);
        Assert.Contains("evt_pulse_closed", _fixture.CatchAllInvocations);
        Assert.DoesNotContain("evt_pulse_closed", _fixture.SpecificInvocations);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task DispatchNowAsync_TriggersImmediateTick_BypassesReminderCadence()
    {
        // Closes out D2: DispatchNowAsync is the latency-optimization entry
        // point — the production reminder is configured to ~1h in this fixture
        // but the spec doesn't need to wait that long. After DispatchNow, the
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

        await _fixture.EventDispatcher.DispatchNowAsync();

        Assert.Contains("evt_pulse_immediate", _fixture.SpecificInvocations);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task EventDispatcherGrain_ResolveByFixedKey_ReturnsSingletonActivation()
    {
        // Closes out D1: IEventDispatcherGrain is reachable under the
        // fixed global key and the call resolves (Orleans'
        // IGrainWithStringKey + IRemindable activation path).
        var second = _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global);
        await second.DispatchNowAsync();
    }

    [Fact]
    public async Task EventDispatcherGrain_NonFixedKey_SilentlyNoOpsAndDoesNotDispatch()
    {
        // The cluster-singleton dispatcher grain refuses to register its
        // reminder under any key other than the well-known Global key,
        // so a rogue activation no-ops without throwing. No dispatch
        // work or reminder registration must run on the rogue grain.
        var eventId = $"evt_non_fixed_{Guid.NewGuid():N}";
        await PublishWorkflowCompletedAsync(eventId, "issue_non_fixed");
        var rogue = _fixture.Grains.GetGrain<IEventDispatcherGrain>($"rogue-{Guid.NewGuid():N}");

        // Soft no-op: the call completes (does not throw) and no work
        // runs on the rogue grain.
        await rogue.DispatchNowAsync();

        Assert.Null(await _fixture.ReminderTable.ReadRow(
            rogue.GetGrainId(),
            EventDispatcherGrain.ReminderName));
        Assert.Equal(1, _fixture.EventStore.PendingCount);
        Assert.DoesNotContain(eventId, _fixture.SpecificInvocations);

        await _fixture.EventDispatcher.DispatchNowAsync();
    }

    [Fact]
    public async Task RedeliverAsync_NoDeadLetter_NoOps()
    {
        // The silo uses a Noop dead-letter store; re-delivery is a
        // no-op. Verifies the grain method resolves and the
        // dispatcher doesn't throw on a missing DL row.
        var result = await _fixture.EventDispatcher.RedeliverAsync(deadLetterId: 999_999);

        Assert.False(result.Found);
        Assert.False(result.Delivered);
    }

    [Fact]
    public async Task OnActivateAsync_RegistersPersistedReminderWithConfiguredCadence()
    {
        // Closes out D2: the spec grain's OnActivateAsync registers a
        // persisted reminder under the cluster-singleton key, with the
        // cadence the EventDispatcherOptions exposes. Reads from the
        // silo's IReminderTable — both the grain's own reminder
        // registration and the table write round-trip.
        var dispatcherGrainId = _fixture.Grains
            .GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global)
            .GetGrainId();
        var row = await _fixture.ReminderTable.ReadRow(
            dispatcherGrainId,
            EventDispatcherGrain.ReminderName);
        Assert.NotNull(row);
        Assert.Equal(EventDispatcherGrain.ReminderName, row.ReminderName);
        Assert.True(row.Period > TimeSpan.Zero,
            $"Reminder period must be positive (got {row.Period})");
    }

    [Fact]
    public async Task HostedActivation_FirstReminderDeliversWithoutPulse()
    {
        var eventId = $"evt_hosted_reminder_{Guid.NewGuid():N}";
        var delivered = _fixture.WaitForSpecificInvocationAsync(eventId);
        await PublishWorkflowCompletedAsync(eventId, "issue_hosted_reminder");

        await AwaitSignalAsync(delivered, "hosted dispatcher reminder delivery");

        Assert.Contains(eventId, _fixture.SpecificInvocations);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task ReminderCallback_DeliversBeforeAndAfterHostingSiloLoss()
    {
        var dispatcherId = _fixture.EventDispatcher.GetGrainId();
        Assert.True(_fixture.Cluster.TryGetGrainContext(dispatcherId, out var initialContext));
        var initialSilo = initialContext.Address.SiloAddress
            ?? throw new InvalidOperationException("Dispatcher activation has no silo address");

        var beforeTick = _fixture.WaitForSpecificInvocationAsync("evt_reminder_before");
        await PublishWorkflowCompletedAsync("evt_reminder_before", "issue_reminder_before");
        await AwaitSignalAsync(beforeTick, "dispatcher reminder delivery before silo loss");
        Assert.Contains("evt_reminder_before", _fixture.SpecificInvocations);

        var hostingSilo = _fixture.Cluster.GetSiloForAddress(initialSilo);
        Assert.NotNull(hostingSilo);
        var reminderReloaded = _fixture.ReminderTable.PrepareRangeReadSignal();
        await _fixture.Cluster.KillSiloAsync(hostingSilo);
        try
        {
            await _fixture.Cluster.WaitForLivenessToStabilizeAsync(didKill: true);
            await AwaitSignalAsync(reminderReloaded, "persisted reminder reload after silo loss");

            var afterTick = _fixture.WaitForSpecificInvocationAsync("evt_reminder_after");
            await PublishWorkflowCompletedAsync("evt_reminder_after", "issue_reminder_after");
            await AwaitSignalAsync(afterTick, "dispatcher reminder delivery after silo loss");

            Assert.Contains("evt_reminder_after", _fixture.SpecificInvocations);
            Assert.Equal(0, _fixture.EventStore.PendingCount);
        }
        finally
        {
            await _fixture.Cluster.StartAdditionalSiloAsync();
            await _fixture.Cluster.WaitForLivenessToStabilizeAsync();
        }
    }

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
        await _fixture.EventDispatcher.DispatchNowAsync();
        await _fixture.EventDispatcher.DispatchNowAsync();
        await _fixture.EventDispatcher.DispatchNowAsync();
        await _fixture.EventDispatcher.DispatchNowAsync();

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

    [Fact]
    public async Task PoisonSettlementFailure_RollsBackSourceMarkAndDeadLetterRows()
    {
        var eventId = $"evt_poison_rollback_{Guid.NewGuid():N}";
        var envelope = new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/issues/issue_poison_rollback_{Guid.NewGuid():N}", UriKind.Relative),
            type: "test.poison",
            time: EventTime,
            data: null);
        _fixture.DeadLetterStore.ThrowAfterSourceMark = true;

        try
        {
            await _fixture.EventPublisher.PublishAsync(envelope);
            await _fixture.EventDispatcher.DispatchNowAsync();
            await _fixture.EventDispatcher.DispatchNowAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _fixture.EventDispatcher.DispatchNowAsync());

            Assert.Contains(
                await _fixture.EventStore.ListUndeliveredAsync(),
                row => row.EventId == eventId);
            Assert.DoesNotContain(
                _fixture.DeadLetterStore.Written,
                row => row.EventId == eventId);
        }
        finally
        {
            _fixture.DeadLetterStore.ThrowAfterSourceMark = false;
        }

        await _fixture.EventDispatcher.DispatchNowAsync();
    }

    [Fact]
    public async Task PoisonSettlementFailure_RecoverySettlesWithoutReinvokingHandler()
    {
        // The DispatcherPoisonHandler exhausts MaxAttempts (3) invocations
        // before the dead-letter settlement write throws. After the store
        // recovers, the next dispatch must settle the row using the
        // retained in-process state — the handler is not invoked again
        // and the dead-letter row records the original attempt count.
        var eventId = $"evt_poison_recovery_{Guid.NewGuid():N}";
        var envelope = new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/issues/issue_poison_recovery_{Guid.NewGuid():N}", UriKind.Relative),
            type: "test.poison",
            time: EventTime,
            data: null);
        _fixture.DeadLetterStore.ThrowAfterSourceMark = true;

        try
        {
            await _fixture.EventPublisher.PublishAsync(envelope);
            await _fixture.EventDispatcher.DispatchNowAsync();
            await _fixture.EventDispatcher.DispatchNowAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _fixture.EventDispatcher.DispatchNowAsync());

            Assert.Contains(
                await _fixture.EventStore.ListUndeliveredAsync(),
                row => row.EventId == eventId);
            Assert.DoesNotContain(
                _fixture.DeadLetterStore.Written,
                row => row.EventId == eventId);
        }
        finally
        {
            _fixture.DeadLetterStore.ThrowAfterSourceMark = false;
        }

        await _fixture.EventDispatcher.DispatchNowAsync();

        var deadLetter = Assert.Single(
            _fixture.DeadLetterStore.Written,
            row => row.EventId == eventId);
        Assert.Equal(typeof(DispatcherPoisonHandler).FullName, deadLetter.FailingHandler);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Equal(0, _fixture.EventStore.PendingCount);

        // A subsequent cycle must not rewrite the dead-letter row or
        // re-invoke the handler — the source row is already marked
        // dispatched and the dispatcher's in-process state is empty.
        await _fixture.EventDispatcher.DispatchNowAsync();
        Assert.Single(
            _fixture.DeadLetterStore.Written,
            row => row.EventId == eventId);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    private Task PublishWorkflowCompletedAsync(string eventId, string issueId) =>
        _fixture.EventPublisher.PublishAsync(new CloudEvent(
            id: eventId,
            source: new Uri($"/mohist/issues/{issueId}", UriKind.Relative),
            type: EventCatalog.ReverseDns.WorkflowRunCompleted,
            time: EventTime,
            data: null));

    private Task AwaitSignalAsync(Task signal, string description) =>
        TestWait.ForAsync(
            () => signal.IsCompleted,
            timeout: TimeSpan.FromSeconds(5),
            step: TimeSpan.FromMilliseconds(100),
            description,
            advance: AdvanceClusterTurnAsync);

    private async Task AdvanceClusterTurnAsync()
    {
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(1));
        await _fixture.Grains
            .GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
            .ListRunnerIdsAsync();
    }
}

/// <summary>
/// xUnit collection definition for the dispatcher silo fixture.
/// Serialized because the silo activates the dispatcher grain on
/// first use and the capture lists are shared state.
/// </summary>
[CollectionDefinition("Dispatcher", DisableParallelization = true)]
public class DispatcherCollection : ICollectionFixture<DispatcherFixture>;