using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
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
        var delivery = EventDispatcherImmediateTriggerTestSupport.ExpectPokeDelivery(
            _fixture,
            row => row.Source == $"/mohist/workflow-runs/{runId}"
                && row.Type == EventCatalog.ReverseDns.WorkflowRunCompleted,
            DispatcherHandler.Specific);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var run = BuildRun(runId);
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        await EventDispatcherImmediateTriggerTestSupport.AwaitPokeDeliveryAsync(_fixture, delivery);
    }

    [Fact]
    public async Task IssueStore_Commit_PokesDispatcherAndLowersLatencyBelowReminderCadence()
    {
        // Mirrors the WorkflowRun poke for the Issue producer: a
        // SaveAsync on the issue store also fires the dispatcher grain
        // before the reminder cadence elapses. The IssueCreated event
        // type matches the catch-all DispatcherCatchAllHandler
        // subscription (Type = "*").
        var issue = BuildIssue();
        var delivery = EventDispatcherImmediateTriggerTestSupport.ExpectPokeDelivery(
            _fixture,
            row => row.Source == $"/mohist/projects/{issue.ProjectId}/issues/{issue.Number}"
                && row.Type == EventCatalog.ReverseDns.IssueCreated,
            DispatcherHandler.CatchAll);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var issueStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Issue.IIssueStore>();
            await issueStore.SaveAsync(
                GrainKey.Issue(new IssueKey(issue.ProjectId, issue.Number)),
                issue,
                [new IssueCreated("poke", "p2", new Dictionary<string, string>(), null, null)]);
        }

        await EventDispatcherImmediateTriggerTestSupport.AwaitPokeDeliveryAsync(_fixture, delivery);
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
        var delivery = EventDispatcherImmediateTriggerTestSupport.ExpectPokeDelivery(
            _fixture,
            row => row.Source == $"/mohist/agent-session/{sessionId}"
                && row.Type == EventCatalog.ReverseDns.AgentSessionRuntimeBound,
            DispatcherHandler.CatchAll);

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var sessionStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Sessions.IAgentSessionStore>();
            var session = BuildAgentSession(sessionId);
            await sessionStore.SaveAsync(session.Id, session, [new Mohist.Server.Sessions.Domain.AgentSessionRuntimeBound("runtime-1", null)]);
        }

        // Sanity check: the save actually persisted the event row.
        Assert.True(_fixture.EventStore.PendingCount > beforePending,
            $"Expected PendingCount to grow after AgentSessionStore.SaveAsync; was {beforePending}, now {_fixture.EventStore.PendingCount}");

        await EventDispatcherImmediateTriggerTestSupport.AwaitPokeDeliveryAsync(_fixture, delivery);
    }

    [Fact]
    public async Task EpicGrain_EventCommit_PokesDispatcherButIdempotentCommandDoesNot()
    {
        var projectId = $"proj_epic_poke_{Guid.NewGuid():N}";
        const int epicNumber = 1;
        await SeedEpicAsync(projectId, epicNumber);
        var delivery = EventDispatcherImmediateTriggerTestSupport.ExpectPokeDelivery(
            _fixture,
            row => row.Source == $"/mohist/projects/{projectId}/epics/{epicNumber}"
                && row.Type == EventCatalog.ReverseDns.EpicStatusChanged,
            DispatcherHandler.CatchAll);
        var epic = _fixture.Grains.GetGrain<IEpicGrain>(
            GrainKey.Epic(new EpicKey(projectId, epicNumber)));

        await epic.StartAsync();
        await EventDispatcherImmediateTriggerTestSupport.AwaitPokeDeliveryAsync(_fixture, delivery);
        Assert.True(_fixture.BackgroundTasks.LaunchCount > 0);

        _fixture.BackgroundTasks.Reset();
        await epic.StartAsync();
        Assert.Equal(0, _fixture.BackgroundTasks.LaunchCount);
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
        var runId = $"wr_lost_poke_{Guid.NewGuid():N}";

        var brokenFactory = new ThrowingDispatchGrainFactory();

        await using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope())
        {
            var runStore = new WorkflowRunStore(
                scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>(),
                _fixture.EventStore,
                brokenFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowRunStore>.Instance,
                new Mohist.Server.Infrastructure.BackgroundTaskLauncher(),
                new DispatchSnapshotStore(
                    scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mohist.Server.Infrastructure.Data.Db.MohistDbContext>>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DispatchSnapshotStore>.Instance) as IDispatchSnapshotStore);
            var run = BuildRun(runId, issueId);
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        // The poke was lost: the row remains in the event store and
        // hasn't been delivered to the matching subscription.
        Assert.Equal(1, _fixture.EventStore.PendingCount);
        var pending = Assert.Single(await _fixture.EventStore.ListUndeliveredAsync());
        Assert.Equal($"/mohist/workflow-runs/{runId}", pending.Source);
        Assert.Equal(EventCatalog.ReverseDns.WorkflowRunCompleted, pending.Type);
        var delivered = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(
            _fixture,
            DispatcherDeliveryKey.From(pending, DispatcherHandler.Specific));

        var reminderTime = EventTime.UtcDateTime.AddHours(1);
        await _fixture.EventDispatcher.ReceiveReminder(
            EventDispatcherGrain.ReminderName,
            new TickStatus(EventTime.UtcDateTime, TimeSpan.FromHours(1), reminderTime));

        Assert.True(delivered.IsCompletedSuccessfully,
            "Reminder cycle returned before the exact persisted event reached handler delivery and settlement.");
        await delivered;
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    private async Task SeedEpicAsync(string projectId, int epicNumber)
    {
        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Epics.Add(new EpicRow
        {
            ProjectId = projectId,
            Number = epicNumber,
            Title = "Immediate trigger poke",
            Description = "",
            Priority = "p2",
            Status = EpicStatusName.Idle,
            CreatedAt = EventTime,
            UpdatedAt = EventTime,
        });
        await db.SaveChangesAsync();
    }

    private static WorkflowRun BuildRun(string id, string? issueId = null)
    {
        return new WorkflowRun
        {
            Id = id,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: EventTime,
                ProjectId: "proj_poke",
                IssueNumber: issueId is null ? null : 1),
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
