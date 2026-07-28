using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Specs for the best-effort immediate-trigger poke producers wire after
/// their state transaction commits. The poke is a pure latency
/// optimization — never a correctness guarantee — so these specs verify
/// (a) the poke lowers latency vs. the 1-hour reminder cadence the
/// fixture configures, and (b) a lost poke is recovered by the next
/// reminder tick. Spec:
/// <c>openspec/changes/issue-362/specs/event-dispatcher/spec.md#best-effort-immediate-trigger-from-producers</c>.
/// </summary>
[Collection("Dispatcher")]
public class EventDispatcherImmediateTriggerSpecs
{
    private static readonly DateTimeOffset EventTime = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly DispatcherFixture _fixture;

    public EventDispatcherImmediateTriggerSpecs(DispatcherFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetInvocationRecords();
    }

    [Fact]
    public async Task WorkflowRunStore_Commit_PokesDispatcherAndLowersLatencyBelowReminderCadence()
    {
        // Closes out the "Immediate trigger lowers latency but is not
        // required for correctness" scenario. The fixture configures
        // EventDispatcherOptions.ReminderPeriod to 1 hour — without the
        // post-commit poke the dispatcher would not fire within the
        // window below. The poke triggers DispatchNowAsync which runs a
        // dispatch cycle and delivers the event to the matching
        // DispatcherSpecificHandler subscription well before the
        // reminder period elapses.
        var runId = $"wr_poke_latency_{Guid.NewGuid():N}";
        int beforeCount;
        lock (_fixture.SpecificInvocations)
            beforeCount = _fixture.SpecificInvocations.Count;
        // Register the deterministic delivery signal before the producer
        // commits so the awaited task resolves from the handler's own
        // invocation rather than a wall-clock timeout.
        var delivered = _fixture.WaitForSpecificBeyondAsync(beforeCount);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var run = BuildRun(runId, $"evt_poke_latency_{Guid.NewGuid():N}");
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        // Cross cluster turns so the dispatcher's DispatchNowAsync (queued
        // by the producer's poke) runs to completion. The signal resolves
        // the moment the specific handler records the delivery.
        await AdvanceClusterTurnUntilSettledAsync(delivered);
    }

    /// <summary>
    /// Drives cluster turns until <paramref name="signal"/> completes. Each
    /// turn pings a grain so the in-process silo scheduler processes queued
    /// messages (the dispatcher's DispatchNowAsync from the producer's poke,
    /// or the reminder tick's fan-out). Awaiting the signal is the
    /// deterministic completion — it resolves from the handler's own
    /// invocation, never from a wall-clock timeout. The bounded loop only
    /// guards against an indefinite hang if the scheduler ever stalls.
    /// </summary>
    private async Task AdvanceClusterTurnUntilSettledAsync(Task signal)
    {
        // The signal may already be resolved (handler ran synchronously
        // during the producer's commit).
        for (var i = 0; i < 200 && !signal.IsCompleted; i++)
        {
            await _fixture.Grains
                .GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .ListRunnerIdsAsync();
        }

        Assert.True(signal.IsCompleted, "Event delivery did not settle after 200 cluster turns");
        await signal;
    }

    [Fact]
    public async Task IssueStore_Commit_PokesDispatcherAndLowersLatencyBelowReminderCadence()
    {
        // Mirrors the WorkflowRun poke for the Issue producer: a
        // SaveAsync on the issue store also fires the dispatcher grain
        // before the reminder cadence elapses. The IssueCreated event
        // type matches the catch-all DispatcherCatchAllHandler
        // subscription (Type = "*").
        int beforeCatchAll;
        lock (_fixture.CatchAllInvocations)
            beforeCatchAll = _fixture.CatchAllInvocations.Count;
        var delivered = _fixture.WaitForCatchAllBeyondAsync(beforeCatchAll);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var issueStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Issue.IIssueStore>();
            var issue = BuildIssue();
            await issueStore.SaveAsync(
                GrainKey.Issue(new IssueKey(issue.ProjectId, issue.Number)),
                issue,
                [new IssueCreated("poke", "p2", new Dictionary<string, string>(), null, null)]);
        }

        await AdvanceClusterTurnUntilSettledAsync(delivered);
    }

    [Fact]
    public async Task AgentSessionStore_Commit_PokesDispatcherAndLowersLatencyBelowReminderCadence()
    {
        // Same shape as the WorkflowRun and Issue specs, exercised
        // against the AgentSessionStore producer. Asserts the third
        // producer's poke path is wired. AgentSessionRuntimeBound
        // matches the catch-all subscription.
        var sessionId = $"agent_poke_{Guid.NewGuid():N}";
        var beforePending = _fixture.EventStore.PendingCount;
        int beforeCatchAll;
        lock (_fixture.CatchAllInvocations)
            beforeCatchAll = _fixture.CatchAllInvocations.Count;
        var delivered = _fixture.WaitForCatchAllBeyondAsync(beforeCatchAll);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var sessionStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Sessions.IAgentSessionStore>();
            var session = BuildAgentSession(sessionId);
            await sessionStore.SaveAsync(session.Id, session, [new Mohist.Server.Sessions.Domain.AgentSessionRuntimeBound("runtime-1", null)]);
        }

        // Sanity check: the save actually persisted the event row.
        Assert.True(_fixture.EventStore.PendingCount > beforePending,
            $"Expected PendingCount to grow after AgentSessionStore.SaveAsync; was {beforePending}, now {_fixture.EventStore.PendingCount}");

        await AdvanceClusterTurnUntilSettledAsync(delivered);
    }

    [Fact]
    public async Task LostImmediateTrigger_LeavesRowUndispatched_AndReminderTickRecovers()
    {
        // Closes out the "Lost immediate trigger is recovered by the
        // next tick" scenario. The WorkflowRunStore poke targets the
        // dispatcher's well-known Global grain; we replace the default
        // IGrainFactory in a fresh DI scope with one that throws on
        // the dispatcher reference, simulating a lost poke (the
        // exception is swallowed by the store's best-effort wrapper).
        // The event row remains DispatchedAt IS NULL — verifiable on
        // the in-memory event store as PendingCount == 1 — and the
        // reminder callback queries and dispatches it through the
        // normal pull–fan-out cycle.
        var issueId = $"issue_lost_poke_{Guid.NewGuid():N}";
        var eventId = $"evt_lost_poke_{Guid.NewGuid():N}";

        var brokenFactory = new ThrowingDispatchGrainFactory();
        int beforeSpecific;
        lock (_fixture.SpecificInvocations)
            beforeSpecific = _fixture.SpecificInvocations.Count;

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var runStore = new WorkflowRunStore(
                scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>(),
                _fixture.EventStore,
                brokenFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowRunStore>.Instance, new Mohist.Server.Infrastructure.BackgroundTaskLauncher());
            var run = BuildRun($"wr_lost_poke_{Guid.NewGuid():N}", eventId, issueId);
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        // The poke was lost: the row remains in the event store and
        // hasn't been delivered to the matching subscription.
        Assert.Equal(1, _fixture.EventStore.PendingCount);
        lock (_fixture.SpecificInvocations)
            Assert.Equal(beforeSpecific, _fixture.SpecificInvocations.Count);

        var reminderTime = EventTime.UtcDateTime.AddHours(1);
        await _fixture.EventDispatcher.ReceiveReminder(
            EventDispatcherGrain.ReminderName,
            new TickStatus(EventTime.UtcDateTime, TimeSpan.FromHours(1), reminderTime));

        lock (_fixture.SpecificInvocations)
            Assert.Equal(beforeSpecific + 1, _fixture.SpecificInvocations.Count);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    private static WorkflowRun BuildRun(string id, string eventId, string? issueId = null)
    {
        return new WorkflowRun
        {
            Id = id,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: EventTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "proj_poke",
                    ["issueId"] = issueId ?? string.Empty,
                    ["issueNumber"] = issueId is null ? string.Empty : "1",
                }),
            Stages = [],
        };
    }

    private static Mohist.Server.Issue.Domain.Issue BuildIssue() => new()
    {
        ProjectId = "proj_poke",
        Number = 1,
        Title = "Immediate trigger poke",
        Priority = "p2",
    };

    private static Mohist.Server.Sessions.Domain.AgentSession BuildAgentSession(string id)
    {
        var session = new Mohist.Server.Sessions.Domain.AgentSession
        {
            Id = id,
            Runtime = new Mohist.Server.Sessions.Domain.AgentSessionRuntime("runner-1", null),
            Settings = new Mohist.Server.Sessions.Domain.AgentSessionSettings("opencode"),
            Metadata = new Mohist.Server.Sessions.Domain.AgentSessionMetadata(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [Mohist.Server.Sessions.Services.AgentSessionQueryMetadataKeys.ProjectId] = "proj_poke",
                }),
        };
        session.Status = session.Status with
        {
            CreatedAt = TestTime.UtcDateTime,
            LastDataAt = TestTime.UtcDateTime,
        };
        return session;
    }

    /// <summary>
    /// IGrainFactory that throws when asked for the event dispatcher.
    /// Simulates the "poke is lost" scenario: the producer's
    /// best-effort wrapper swallows the exception so the transaction
    /// commit succeeds, but the dispatcher never receives the trigger.
    /// </summary>
    private sealed class ThrowingDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                throw new InvalidOperationException("simulated dispatcher unavailable");
            throw new NotSupportedException();
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }
}
