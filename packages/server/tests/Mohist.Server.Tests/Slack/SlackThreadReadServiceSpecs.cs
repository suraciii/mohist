using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Support;
using NoopEventStore = Mohist.Server.TestSupport.NoopEventStore;
using Xunit;

namespace Mohist.Server.Tests.Slack;

/// <summary>
/// One read resolves the Session's recorded thread, proves the Connection
/// still holds a verified Bot credential, verifies the continuation against
/// that binding, and reads exactly one provider page — or it refuses without
/// touching Slack. Refusals never fall back to another thread or transcript.
/// </summary>
[Trait("level", "L0")]
public sealed class SlackThreadReadServiceSpecs : IAsyncLifetime
{
    private static readonly DateTime FixedAt = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset ClockNow = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private const string ChannelId = "C-channel";
    private const string RootMessageId = "1710.0001";
    private const string BotToken = "xoxb-bound-bot";

    private readonly TestSqliteDatabase _database = TestSqliteDatabase.CreateMigrated();
    private readonly FakeTimeProvider _clock = new(ClockNow);
    private readonly FakeSlackThreadQueryPort _threads = new();
    private readonly InMemorySlackLeaseTargetProvider _targets = new();
    private readonly RecordingSecretResolver _secrets = new();
    private readonly AgentSessionStore _sessions;
    private readonly SlackThreadSessionMappingStore _threadMappings;
    private readonly SlackDmSessionMappingStore _dmMappings;
    private readonly AgentConnectionStore _connections;
    private readonly SlackAdapterLeaseService _leases;
    private readonly string _projectId = $"project_{Guid.NewGuid():N}";
    private readonly string _connectionId = $"connection_{Guid.NewGuid():N}";
    private readonly string _agentAppId = $"slackapp_{Guid.NewGuid():N}";

    public SlackThreadReadServiceSpecs()
    {
        var factory = new TestDbContextFactory(_database.Options);
        _sessions = new AgentSessionStore(
            factory,
            new NoopEventStore(),
            NullLogger<AgentSessionStore>.Instance,
            new EventDispatchSignal());
        _threadMappings = new SlackThreadSessionMappingStore(factory, _clock);
        _dmMappings = new SlackDmSessionMappingStore(factory, _clock);
        _connections = new AgentConnectionStore(
            factory,
            new AgentQuerier(factory),
            new NoopSecretStore(),
            [],
            _clock);
        _leases = new SlackAdapterLeaseService(
            new InMemorySlackLeaseStore(), _targets, _secrets, _clock, _connections);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Read_returns_the_bound_thread_page_and_a_continuation_without_creating_work()
    {
        var sessionId = await SeedBoundSessionAsync();
        _threads.Result = SlackThreadPage.Ok([Message("1710.0001"), Message("1710.0002")], "cursor-2");

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        var view = result.View!;
        Assert.Null(result.Error);
        Assert.Equal(
            new SlackThreadViewThread("T123", _connectionId, ChannelId, RootMessageId),
            view.Thread);
        Assert.Equal(["1710.0001", "1710.0002"], view.Messages.Select(message => message.Ts));
        Assert.NotNull(view.Continuation);

        var query = Assert.Single(_threads.Queries);
        Assert.Equal(BotToken, query.BotToken);
        Assert.Equal(ChannelId, query.ConversationId);
        Assert.Equal(RootMessageId, query.ThreadRootTs);
        Assert.Null(query.Continuation);
        Assert.Equal(15, query.Limit);

        // A read is a query: the Session gains no Input, Turn, or Job.
        var session = await _sessions.LoadAsync(sessionId);
        Assert.Single(session!.Status.Inputs!);
        Assert.Single(session.Status.Turns!);
    }

    [Fact]
    public async Task Read_continues_from_the_page_continuation_it_issued()
    {
        var sessionId = await SeedBoundSessionAsync();
        _threads.Result = SlackThreadPage.Ok([Message("1710.0001")], "cursor-2");
        var first = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        _threads.Result = SlackThreadPage.Ok([Message("1710.0002")], null);
        var second = await Service().ReadAsync(
            new SlackThreadReadRequest(_projectId, sessionId, 15, first.View!.Continuation));

        Assert.Null(second.Error);
        Assert.Null(second.View!.Continuation);
        Assert.Equal("cursor-2", _threads.Queries[1].Continuation);
    }

    [Fact]
    public async Task Read_refuses_a_continuation_issued_for_another_session_without_asking_slack()
    {
        var sessionId = await SeedBoundSessionAsync();
        var continuation = SlackThreadContinuation.Encode(
            BotToken,
            new SlackThreadContinuationScope(
                _projectId, "session-other", _connectionId, "T123", ChannelId, RootMessageId),
            "cursor-2");

        var result = await Service().ReadAsync(
            new SlackThreadReadRequest(_projectId, sessionId, 15, continuation));

        Assert.Equal("continuation_invalid", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_a_continuation_issued_for_another_thread_without_asking_slack()
    {
        var sessionId = await SeedBoundSessionAsync();
        var continuation = SlackThreadContinuation.Encode(
            BotToken,
            new SlackThreadContinuationScope(
                _projectId, sessionId, _connectionId, "T123", "C-other", RootMessageId),
            "cursor-2");

        var result = await Service().ReadAsync(
            new SlackThreadReadRequest(_projectId, sessionId, 15, continuation));

        Assert.Equal("continuation_invalid", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_when_the_connection_has_no_verified_credential()
    {
        var sessionId = await SeedBoundSessionAsync(withCredential: false);

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("credential_unavailable", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_a_disabled_connection()
    {
        var sessionId = await SeedBoundSessionAsync(desiredState: DesiredStateKind.Disabled);

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("connection_unavailable", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_a_soft_deleted_connection()
    {
        var sessionId = await SeedBoundSessionAsync(deletedAt: ClockNow);

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("connection_unavailable", result.Error!.Code);
        Assert.Contains("no longer exists", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_a_direct_message_session_with_its_own_code()
    {
        var sessionId = await SeedSessionAsync(ChannelProvenance(conversationId: "D-dm", threadRootMessageId: "1709.0001"));
        await _dmMappings.SetCurrentSessionIdAsync(
            _projectId, _connectionId, "T123", "U_SENDER", "D-dm", sessionId);

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("dm_not_supported", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_a_session_that_is_not_bound_to_a_channel_thread()
    {
        var sessionId = await SeedSessionAsync();

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("not_bound", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Read_refuses_an_unknown_session()
    {
        var result = await Service().ReadAsync(
            new SlackThreadReadRequest(_projectId, $"session_{Guid.NewGuid():N}", 15, null));

        Assert.Equal("session_not_found", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Read_refuses_a_limit_outside_the_supported_range(int limit)
    {
        var sessionId = await SeedBoundSessionAsync();

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, limit, null));

        Assert.Equal("invalid_request", result.Error!.Code);
        Assert.Empty(_threads.Queries);
    }

    [Fact]
    public async Task Rate_limited_page_reports_the_provider_delay()
    {
        var sessionId = await SeedBoundSessionAsync();
        _threads.Result = SlackThreadPage.RateLimited(TimeSpan.FromSeconds(30));

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("rate_limited", result.Error!.Code);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Error.RetryAfter);
        Assert.Contains("30 seconds", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_rejection_keeps_the_slack_error_actionable()
    {
        var sessionId = await SeedBoundSessionAsync();
        _threads.Result = SlackThreadPage.ProviderRejected("not_in_channel");

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("provider_rejected", result.Error!.Code);
        Assert.Equal("not_in_channel", result.Error.ProviderError);
        Assert.Contains("no longer in this channel", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_cursor_rejection_asks_the_caller_to_restart_the_read()
    {
        var sessionId = await SeedBoundSessionAsync();
        var continuation = SlackThreadContinuation.Encode(
            BotToken,
            new SlackThreadContinuationScope(
                _projectId, sessionId, _connectionId, "T123", ChannelId, RootMessageId),
            "cursor-2");
        _threads.Result = SlackThreadPage.ProviderRejected("invalid_cursor");

        var result = await Service().ReadAsync(
            new SlackThreadReadRequest(_projectId, sessionId, 15, continuation));

        Assert.Equal("cursor_rejected", result.Error!.Code);
        Assert.Equal("cursor-2", Assert.Single(_threads.Queries).Continuation);
        Assert.Contains("without --continuation", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_pagination_is_not_reported_as_completion()
    {
        var sessionId = await SeedBoundSessionAsync();
        _threads.Result = SlackThreadPage.InvalidResponse();

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("invalid_provider_response", result.Error!.Code);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task Transport_failure_is_not_reported_as_an_empty_thread()
    {
        var sessionId = await SeedBoundSessionAsync();
        _threads.Result = SlackThreadPage.TransportError;

        var result = await Service().ReadAsync(new SlackThreadReadRequest(_projectId, sessionId, 15, null));

        Assert.Equal("provider_unavailable", result.Error!.Code);
        Assert.Null(result.View);
    }

    private SlackThreadReadService Service() =>
        new(
            new SlackThreadReadBindingResolver(_sessions, _threadMappings, _dmMappings),
            _connections,
            _leases,
            _threads);

    private static SlackThreadMessage Message(string ts) =>
        new(
            ts,
            AuthorUserId: "U_AUTHOR",
            AuthorBotId: null,
            Text: $"text {ts}",
            ThreadRoot: ts == RootMessageId,
            Edited: false,
            EditedTs: null,
            Deleted: false,
            UnavailableContent: [],
            Permalink: null);

    private async Task<string> SeedBoundSessionAsync(
        bool withCredential = true,
        string desiredState = DesiredStateKind.Enabled,
        DateTimeOffset? deletedAt = null)
    {
        var sessionId = await SeedSessionAsync(ChannelProvenance());
        await _threadMappings.UpsertAsync(
            _projectId, "T123", _connectionId, ChannelId, RootMessageId, "U_SENDER", sessionId, RootMessageId);
        await SeedConnectionAsync(desiredState, deletedAt);
        var address = SecretStoreAddress.ForManagedSlackAgentApp(_agentAppId, SecretKind.BotToken);
        if (withCredential)
            await _secrets.StoreAsync(address, Encoding.UTF8.GetBytes(BotToken));
        _targets.Add(new SlackLeaseTarget(
            new SlackLeaseTargetRef.Connection(_projectId, _connectionId),
            ExpectedAppId: "A123",
            Active: true,
            AppLevelTokenProvisioned: true,
            BotTokenProvisioned: withCredential,
            CredentialVerified: withCredential,
            AppLevelTokenAddress: SecretStoreAddress.ForManagedSlackAgentApp(_agentAppId, SecretKind.AppToken),
            BotTokenAddress: address,
            CandidateAppLevelTokenAddress: null));
        return sessionId;
    }

    private async Task SeedConnectionAsync(string desiredState, DateTimeOffset? deletedAt = null)
    {
        await using var db = new MohistDbContext(_database.Options);
        db.AgentConnections.Add(new AgentConnectionRow
        {
            Id = _connectionId,
            ProjectId = _projectId,
            AgentId = "agent-1",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "T123",
            AppId = "A123",
            BotUserId = "U_BOT",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.Complete,
            DesiredState = desiredState,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            CreatedAt = ClockNow,
            UpdatedAt = ClockNow,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync();
    }

    private SlackThreadReadTarget ChannelTarget() =>
        new(_projectId, _connectionId, "T123", ChannelId, RootMessageId);

    private AgentSessionInputProvenance ChannelProvenance(
        string conversationId = ChannelId,
        string threadRootMessageId = RootMessageId) =>
        new(
            ProviderKind: "slack",
            WorkspaceId: "T123",
            ConversationId: conversationId,
            ThreadId: threadRootMessageId,
            MemberId: "U_SENDER",
            MessageId: "1710.0002",
            ConnectionId: _connectionId,
            BoundThreadRootMessageId: threadRootMessageId);

    private async Task<string> SeedSessionAsync(params AgentSessionInputProvenance[] provenance)
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

    private sealed class RecordingSecretResolver : ISlackLeaseSecretResolver
    {
        private readonly Dictionary<SecretStoreAddress, string> _values = [];

        public Task<string?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(address));

        public Task StoreAsync(SecretStoreAddress address, byte[] value)
        {
            _values[address] = Encoding.UTF8.GetString(value);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopSecretStore : ISecretStore
    {
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public Task StoreAsync(SecretStoreAddress address, byte[] value, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(true);

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
