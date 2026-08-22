using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Specs for the best-effort wake signal producers write after their
/// state transaction commits. The signal is a pure latency optimization —
/// never a correctness guarantee — so these specs verify (a) every
/// producer commits exactly one observable wake, and (b) an unconsumed
/// signal loses nothing: the row stays pending and the next explicit
/// drain delivers it.
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
    public async Task WorkflowRunStore_Commit_WakesDispatchWorkers()
    {
        var runId = $"wr_wake_{Guid.NewGuid():N}";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var run = BuildRun(runId);
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        await AssertWokenAsync();
        Assert.Equal(1, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task IssueStore_Commit_WakesDispatchWorkers()
    {
        var issue = BuildIssue();

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var issueStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Issue.IIssueStore>();
            await issueStore.SaveAsync(
                Infrastructure.Orleans.GrainKey.Issue(new Infrastructure.Orleans.IssueKey(issue.ProjectId, issue.Number)),
                issue,
                [new IssueCreated("wake", "p2", new Dictionary<string, string>(), null, null)]);
        }

        await AssertWokenAsync();
        Assert.Equal(1, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task AgentSessionStore_Commit_WakesDispatchWorkers()
    {
        var sessionId = $"agent_wake_{Guid.NewGuid():N}";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var sessionStore = scope.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Data.Sessions.IAgentSessionStore>();
            var session = BuildAgentSession(sessionId);
            await sessionStore.SaveAsync(session.Id, session, [new Mohist.Server.Sessions.Domain.AgentSessionRuntimeBound("runtime-1", null)]);
        }

        await AssertWokenAsync();
        Assert.Equal(1, _fixture.EventStore.PendingCount);
    }

    [Fact]
    public async Task UnconsumedSignal_LeavesRowPending_AndDrainDelivers()
    {
        // No worker consumed the wake in this fixture. The row stays
        // undispatched (nothing lost) and an explicit drain delivers it —
        // the same path the slow poll takes when a signal is lost.
        var runId = $"wr_lost_wake_{Guid.NewGuid():N}";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var run = BuildRun(runId);
            await runStore.SaveAsync(run, [new WorkflowRunCompleted()]);
        }

        // Drain the wake so it does not leak into the next test, then
        // deliver through the pull path.
        await _fixture.DispatchSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None);

        var pending = Assert.Single(await _fixture.EventStore.ListUndeliveredAsync());
        Assert.Equal($"/mohist/workflow-runs/{runId}", pending.Source);
        Assert.Equal(EventCatalog.ReverseDns.WorkflowRunCompleted, pending.Type);
        var delivered = EventDispatcherImmediateTriggerTestSupport.WaitForHandlerDeliveryAsync(
            _fixture,
            DispatcherDeliveryKey.From(pending, DispatcherHandler.Specific));

        await _fixture.EventDispatcher.DrainAsync();

        Assert.True(delivered.IsCompletedSuccessfully,
            "Drain returned before the event reached handler delivery and settlement.");
        await delivered;
        Assert.Equal(0, _fixture.EventStore.PendingCount);
    }

    private async Task AssertWokenAsync()
    {
        // The producer wrote one wake; a zero-timeout wait observes it.
        var woken = await _fixture.DispatchSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.True(woken, "Producer commit did not wake the dispatch signal.");
    }

    private static WorkflowRun BuildRun(string id) => new()
    {
        Id = id,
        Metadata = new WorkflowRunMetadata(
            Name: null,
            CreatedAt: EventTime,
            ProjectId: "proj_wake",
            IssueNumber: null),
        Stages = [],
    };

    private static Mohist.Server.Issue.Domain.Issue BuildIssue() => new()
    {
        ProjectId = "proj_wake",
        Number = 1,
        Title = "Wake signal probe",
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
                    [Mohist.Server.Sessions.Services.AgentSessionQueryMetadataKeys.ProjectId] = "proj_wake",
                }),
        };
        session.Status = session.Status with
        {
            CreatedAt = TestTime.UtcDateTime,
            LastDataAt = TestTime.UtcDateTime,
        };
        return session;
    }
}
