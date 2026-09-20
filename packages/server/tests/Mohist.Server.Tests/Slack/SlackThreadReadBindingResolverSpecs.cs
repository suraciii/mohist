using Mohist.Server.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using NoopEventStore = Mohist.Server.TestSupport.NoopEventStore;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// The binding resolver is the security boundary of a thread read: it decides
/// which channel thread a Session id selects, and refuses when the recorded
/// facts are missing, contradictory, or not a channel thread at all.
/// </summary>
[Trait("level", "L0")]
public sealed class SlackThreadReadBindingResolverSpecs : IAsyncLifetime
{
    private static readonly DateTime FixedAt = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset ClockNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly FakeTimeProvider _clock = new(ClockNow);
    private readonly AgentSessionStore _sessions;
    private readonly SlackThreadSessionMappingStore _threadMappings;
    private readonly SlackDmSessionMappingStore _dmMappings;
    private readonly string _projectId = $"project_{Guid.NewGuid():N}";
    private readonly string _connectionId = $"connection_{Guid.NewGuid():N}";

    public SlackThreadReadBindingResolverSpecs()
    {
        var factory = new TestDbContextFactory(_database.Options);
        _sessions = new AgentSessionStore(
            factory,
            new NoopEventStore(),
            NullLogger<AgentSessionStore>.Instance,
            new EventDispatchSignal());
        _threadMappings = new SlackThreadSessionMappingStore(factory, _clock);
        _dmMappings = new SlackDmSessionMappingStore(factory, _clock);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Resolve_returns_the_channel_thread_recorded_by_the_session()
    {
        var sessionId = await PersistSessionAsync(ChannelProvenance());

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.Resolved, result.Outcome);
        Assert.Null(result.Reason);
        Assert.Equal(
            new SlackThreadReadTarget(_projectId, _connectionId, "T123", "C-channel", "1710.0001"),
            result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_a_session_from_another_project()
    {
        var sessionId = await PersistSessionAsync(ChannelProvenance());

        var result = await Resolver().ResolveAsync($"project_{Guid.NewGuid():N}", sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.SessionNotFound, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_an_unknown_session()
    {
        var result = await Resolver().ResolveAsync(_projectId, $"session_{Guid.NewGuid():N}");

        Assert.Equal(SlackThreadReadTargetOutcome.SessionNotFound, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_a_session_without_slack_provenance()
    {
        var sessionId = await PersistSessionAsync();

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.NotBound, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_a_session_whose_inputs_disagree_on_the_thread()
    {
        var sessionId = await PersistSessionAsync(
            ChannelProvenance(),
            ChannelProvenance(conversationId: "C-other"));

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.Conflicting, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_accepts_repeated_provenance_for_the_same_thread()
    {
        var sessionId = await PersistSessionAsync(ChannelProvenance(), ChannelProvenance());

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.Resolved, result.Outcome);
        Assert.Equal("1710.0001", result.Target!.RootMessageId);
    }

    [Fact]
    public async Task Resolve_refuses_a_thread_whose_mapping_points_at_another_session()
    {
        var sessionId = await PersistSessionAsync(ChannelProvenance());
        await _threadMappings.UpsertAsync(
            _projectId, "T123", _connectionId, "C-channel", "1710.0001",
            "U_OWNER", "session-other", "1710.0001");

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.Conflicting, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_a_direct_message_conversation()
    {
        var sessionId = await PersistSessionAsync(
            ChannelProvenance(conversationId: "D-dm", threadRootMessageId: "1709.0001"));
        await _dmMappings.SetCurrentSessionIdAsync(
            _projectId, _connectionId, "T123", "U_SENDER", "D-dm", sessionId);

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.DirectMessage, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_the_manager_conversation()
    {
        var sessionId = await PersistSessionAsync(
            ChannelProvenance() with { OriginMarker = AgentOriginMarkers.SlackManager });

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.ManagerConversation, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task Resolve_refuses_a_session_with_incomplete_thread_provenance()
    {
        var sessionId = await PersistSessionAsync(ChannelProvenance(threadRootMessageId: ""));

        var result = await Resolver().ResolveAsync(_projectId, sessionId);

        Assert.Equal(SlackThreadReadTargetOutcome.NotBound, result.Outcome);
        Assert.Null(result.Target);
    }

    private SlackThreadReadBindingResolver Resolver() =>
        new(_sessions, _threadMappings, _dmMappings);

    private AgentSessionInputProvenance ChannelProvenance(
        string conversationId = "C-channel",
        string threadRootMessageId = "1710.0001") =>
        new(
            ProviderKind: "slack",
            WorkspaceId: "T123",
            ConversationId: conversationId,
            ThreadId: threadRootMessageId,
            MemberId: "U_SENDER",
            MessageId: "1710.0002",
            ConnectionId: _connectionId,
            BoundThreadRootMessageId: threadRootMessageId);

    private async Task<string> PersistSessionAsync(params AgentSessionInputProvenance[] provenance)
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var session = AgentSession.Create(
            sessionId,
            "runner-1",
            "/work",
            metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, _projectId)
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-connection")
                .WithLabel("mohist.io/agent-id", "agent-1"),
            now: FixedAt,
            runtime: "opencode");

        for (var index = 0; index < provenance.Length; index++)
        {
            session.EnsureInitialLaunch(
                inputId: $"input-{index}",
                turnId: $"turn-{index}",
                prompt: $"prompt {index}",
                source: "agent-connection",
                jobId: $"job-{index}",
                now: FixedAt,
                provenance: provenance[index]);
        }

        await _sessions.SaveAsync(sessionId, session);
        return sessionId;
    }
}
