using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.L0Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Events;

/// <summary>
/// Engine-level specs for the stream-lease dispatcher wiring. Drives the
/// production <see cref="EventDispatcherService"/> (real lease store on
/// SQLite, production subscription discovery) through explicit
/// <see cref="IEventDispatcher.DrainAsync"/> calls and asserts the
/// registered <c>[Subscription]</c> handlers (closed-generic +
/// non-generic + wildcard) observe the published event.
/// </summary>
[Collection("Dispatcher")]
[Trait("level", "L0")]
public class DispatcherEngineSpecs
{
    private static readonly DateTimeOffset EventTime = new(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

    private readonly DispatcherFixture _fixture;

    public DispatcherEngineSpecs(DispatcherFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetInvocationRecords();
    }

    [Fact]
    public async Task DrainAsync_AfterPublish_DeliversToMatchingClosedGenericHandler()
    {
        // A closed-generic [Subscription] handler (the same shape
        // EpicAutoDoneHandler uses) is in the fan-out set and is invoked
        // through the dispatcher's typed delegate.
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

        await _fixture.EventDispatcher.DrainAsync();

        Assert.Contains("evt_pulse_closed", _fixture.ClosedGenericInvocations);
        Assert.Contains("evt_pulse_closed", _fixture.CatchAllInvocations);
        Assert.DoesNotContain("evt_pulse_closed", _fixture.SpecificInvocations);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task RedeliverAsync_NoDeadLetter_NoOps()
    {
        // Re-delivery against a missing DL row reports not-found without
        // throwing.
        var result = await _fixture.EventDispatcher.RedeliverAsync(deadLetterId: 999_999);

        Assert.False(result.Found);
        Assert.False(result.Delivered);
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
        // Zero backoff: each drain is one attempt; the third exhausts the
        // budget and the fourth settles.
        await _fixture.EventDispatcher.DrainAsync();
        await _fixture.EventDispatcher.DrainAsync();
        await _fixture.EventDispatcher.DrainAsync();
        await _fixture.EventDispatcher.DrainAsync();

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
    public async Task PoisonSettlementFailure_ParksWithBudget_NextDrainRetriesSettlement()
    {
        // The settlement write throws (transactionally: nothing marked,
        // nothing dead-lettered). The stream parks holding its exhausted
        // budget; after the store recovers, the next drain re-drives the
        // head (handlers re-run — the idempotent contract) and settles.
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
            // Attempts 1 and 2 park normally; attempt 3 exhausts and the
            // dead-letter settlement write throws — the engine parks and
            // the drain completes without throwing.
            await _fixture.EventDispatcher.DrainAsync();
            await _fixture.EventDispatcher.DrainAsync();
            await _fixture.EventDispatcher.DrainAsync();

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

        await _fixture.EventDispatcher.DrainAsync();

        var deadLetter = Assert.Single(
            _fixture.DeadLetterStore.Written,
            row => row.EventId == eventId);
        Assert.Equal(typeof(DispatcherPoisonHandler).FullName, deadLetter.FailingHandler);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Equal(0, _fixture.EventStore.PendingCount);

        // The source row is marked dispatched; a further drain is a no-op.
        await _fixture.EventDispatcher.DrainAsync();
        Assert.Single(
            _fixture.DeadLetterStore.Written,
            row => row.EventId == eventId);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }
}

/// <summary>
/// xUnit collection definition for the dispatcher fixture. Tests in this
/// collection are serialized with each other because they share capture
/// state; the fixture owns an isolated in-memory database, so unrelated
/// collections may still run in parallel.
/// </summary>
[CollectionDefinition("Dispatcher")]
public class DispatcherCollection : ICollectionFixture<DispatcherFixture>;
