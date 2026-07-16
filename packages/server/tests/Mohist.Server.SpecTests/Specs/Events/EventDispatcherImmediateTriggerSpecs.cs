using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Orleans;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
        var beforeCount = 0;
        lock (_fixture.SpecificInvocations)
            beforeCount = _fixture.SpecificInvocations.Count;

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var run = BuildRun(runId, $"evt_poke_latency_{Guid.NewGuid():N}");
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        await TestWait.ForAsync(
            () =>
            {
                lock (_fixture.SpecificInvocations)
                    return Task.FromResult(_fixture.SpecificInvocations.Count > beforeCount);
            },
            delivered => delivered,
            timeout: TimeSpan.FromSeconds(5),
            step: TimeSpan.FromMilliseconds(100),
            description: $"dispatcher delivered WorkflowRunCompleted event via WorkflowRunStore poke within 5s; pendingCount={_fixture.EventStore.PendingCount}, catchAll={_fixture.CatchAllInvocations.Count}, specific={_fixture.SpecificInvocations.Count}",
            advance: AdvanceClusterTurnAsync);
    }

    private async Task AdvanceClusterTurnAsync()
    {
        // Yields the test's CPU to the in-process silo scheduler and
        // pings a grain so queued messages (the dispatcher's
        // DispatchNowAsync from the producer's poke) get processed.
        await _fixture.Grains
            .GetGrain<IRunnerRegistryGrain>(Mohist.Server.Runner.Grains.RunnerRegistryKeys.Global)
            .ListRunnerIdsAsync();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task IssueStore_Commit_PokesDispatcherAndLowersLatencyBelowReminderCadence()
    {
        // Mirrors the WorkflowRun poke for the Issue producer: a
        // SaveAsync on the issue store also fires the dispatcher grain
        // before the reminder cadence elapses. The IssueCreated event
        // type matches the catch-all DispatcherCatchAllHandler
        // subscription (Type = "*").
        var issueId = $"issue_poke_{Guid.NewGuid():N}";
        var beforeCatchAll = 0;
        lock (_fixture.CatchAllInvocations)
            beforeCatchAll = _fixture.CatchAllInvocations.Count;

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var issueStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Issue.IIssueStore>();
            var issue = BuildIssue(issueId);
            await issueStore.SaveAsync(issue.Id, issue, [new IssueCreated("poke", "p2", new Dictionary<string, string>(), null, null)]);
        }

        await TestWait.ForAsync(
            () =>
            {
                lock (_fixture.CatchAllInvocations)
                    return Task.FromResult(_fixture.CatchAllInvocations.Count > beforeCatchAll);
            },
            delivered => delivered,
            timeout: TimeSpan.FromSeconds(5),
            step: TimeSpan.FromMilliseconds(100),
            description: "dispatcher delivered IssueCreated event via IssueStore poke within 5s",
            advance: AdvanceClusterTurnAsync);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task AgentSessionStore_Commit_PokesDispatcherAndLowersLatencyBelowReminderCadence()
    {
        // Same shape as the WorkflowRun and Issue specs, exercised
        // against the AgentSessionStore producer. Asserts the third
        // producer's poke path is wired. AgentSessionRuntimeBound
        // matches the catch-all subscription.
        var sessionId = $"agent_poke_{Guid.NewGuid():N}";
        var beforeCatchAll = 0;
        var beforePending = _fixture.EventStore.PendingCount;
        lock (_fixture.CatchAllInvocations)
            beforeCatchAll = _fixture.CatchAllInvocations.Count;

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var sessionStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Sessions.IAgentSessionStore>();
            var session = BuildAgentSession(sessionId);
            await sessionStore.SaveAsync(session.Id, session, [new Mohist.Server.Sessions.Domain.AgentSessionRuntimeBound("acp-1", null)]);
        }

        // Sanity check: the save actually persisted the event row.
        Assert.True(_fixture.EventStore.PendingCount > beforePending,
            $"Expected PendingCount to grow after AgentSessionStore.SaveAsync; was {beforePending}, now {_fixture.EventStore.PendingCount}");

        // Diagnostic: print what was captured
        var pendingRows = await _fixture.EventStore.ListUndeliveredAsync(int.MaxValue);
        var pendingTypes = string.Join(", ", pendingRows.Select(r => r.Type));

        await TestWait.ForAsync(
            () =>
            {
                lock (_fixture.CatchAllInvocations)
                    return Task.FromResult(_fixture.CatchAllInvocations.Count > beforeCatchAll);
            },
            delivered => delivered,
            timeout: TimeSpan.FromSeconds(5),
            step: TimeSpan.FromMilliseconds(100),
            description: $"dispatcher delivered AgentSessionRuntimeBound event via AgentSessionStore poke within 5s; pending types: {pendingTypes}",
            advance: AdvanceClusterTurnAsync);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
        // next reminder tick (advanced by 1 hour via the fixture's
        // FakeTimeProvider) queries and dispatches it through the
        // normal pull–fan-out cycle.
        var issueId = $"issue_lost_poke_{Guid.NewGuid():N}";
        var eventId = $"evt_lost_poke_{Guid.NewGuid():N}";

        var brokenFactory = new ThrowingDispatchGrainFactory();
        var beforeSpecific = 0;
        lock (_fixture.SpecificInvocations)
            beforeSpecific = _fixture.SpecificInvocations.Count;

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var runStore = new WorkflowRunStore(
                scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>(),
                _fixture.EventStore,
                brokenFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowRunStore>.Instance);
            var run = BuildRun($"wr_lost_poke_{Guid.NewGuid():N}", eventId, issueId);
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        // The poke was lost: the row remains in the event store and
        // hasn't been delivered to the matching subscription.
        Assert.Equal(1, _fixture.EventStore.PendingCount);
        lock (_fixture.SpecificInvocations)
            Assert.Equal(beforeSpecific, _fixture.SpecificInvocations.Count);

        // Advancing the fake clock past the reminder period lets the
        // dispatcher fire its next tick — the same path a real silo
        // takes. The query re-pulls the undelivered row and delivers
        // it through the normal fan-out.
        await AwaitReminderTickAsync();

        await TestWait.ForAsync(
            () =>
            {
                lock (_fixture.SpecificInvocations)
                    return Task.FromResult(_fixture.SpecificInvocations.Count > beforeSpecific);
            },
            delivered => delivered,
            timeout: TimeSpan.FromSeconds(5),
            step: TimeSpan.FromMilliseconds(100),
            description: "reminder tick recovered the lost poke and delivered the WorkflowRunCompleted event",
            advance: AdvanceClusterTurnAsync);
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    private async Task AwaitReminderTickAsync()
    {
        // Advance the fake clock past the 1-hour reminder period, then cross
        // a cluster turn so reminder bookkeeping and the dispatcher can run.
        _fixture.TimeProvider.Advance(TimeSpan.FromHours(2));
        // Drive a cluster turn to settle any pending reminder bookkeeping.
        await _fixture.Grains
            .GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
            .ListRunnerIdsAsync();
    }

    private static WorkflowRun BuildRun(string id, string eventId, string? issueId = null)
    {
        return new WorkflowRun
        {
            Id = id,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: EventTime,
                Annotations: issueId is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["projectId"] = "proj_poke",
                        ["issueId"] = issueId,
                        ["issueNumber"] = "1",
                    }),
            Stages = [],
        };
    }

    private static Mohist.Server.Issue.Domain.Issue BuildIssue(string id) => new()
    {
        Id = id,
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
