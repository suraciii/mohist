using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackConnectionLifecycleSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSecretStore _secrets = new();
    private readonly RecordingSlackApiClient _slack = new();
    private TestSqliteDatabase _database = null!;
    private AgentConnectionStore _store = null!;
    private string _connectionId = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _connectionId = "conn-lifecycle";
        await using (var db = _database.CreateContext())
        {
            db.AgentConnections.Add(new AgentConnectionRow
            {
                Id = _connectionId,
                ProjectId = "project-1",
                AgentId = "agent-1",
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = "T123",
                AppId = "A123",
                BotUserId = "U123",
                BotName = "Mohist",
                SetupProgress = SetupProgressKind.Complete,
                DesiredState = DesiredStateKind.Enabled,
                ConnectionHealth = ConnectionHealthKind.Healthy,
                AgentReadiness = AgentReadinessKind.Ready,
                OwnerSlackUserId = "U_OWNER",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
        }
        await _secrets.StoreAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-1"));
        await _secrets.StoreAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-1"));
        var factory = new TestDbContextFactory(_database.Options);
        _store = new AgentConnectionStore(factory, new AgentQuerier(factory), _secrets, [], _time);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Disable_sets_DesiredState_to_Disabled()
    {
        var updated = await RunDisableAsync();

        Assert.NotNull(updated);
        Assert.Equal(DesiredStateKind.Disabled, updated!.DesiredState);
    }

    [Fact]
    public async Task Enable_sets_DesiredState_to_Enabled()
    {
        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Disabled);

        var updated = await RunEnableAsync();

        Assert.NotNull(updated);
        Assert.Equal(DesiredStateKind.Enabled, updated!.DesiredState);
    }

    [Fact]
    public async Task Disable_is_idempotent_and_keeps_state_Disabled()
    {
        await RunDisableAsync();
        var second = await RunDisableAsync();

        Assert.NotNull(second);
        Assert.Equal(DesiredStateKind.Disabled, second!.DesiredState);
        Assert.Equal(Now, _time.GetUtcNow());
    }

    [Fact]
    public async Task Enable_is_idempotent_and_keeps_state_Enabled()
    {
        var first = await RunEnableAsync();
        var second = await RunEnableAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(DesiredStateKind.Enabled, second!.DesiredState);
        Assert.Equal(Now, _time.GetUtcNow());
    }

    [Fact]
    public async Task Disable_does_not_touch_credentials_owner_setup_or_health()
    {
        var appBefore = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken));
        var botBefore = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken));

        await RunDisableAsync();

        var after = await GetConnectionAsync();
        Assert.Equal("U_OWNER", after.OwnerSlackUserId);
        Assert.Equal(SetupProgressKind.Complete, after.SetupProgress);
        Assert.Equal(ConnectionHealthKind.Healthy, after.ConnectionHealth);
        Assert.Null(after.HealthReason);
        Assert.Equal("T123", after.WorkspaceTeamId);
        Assert.Equal("A123", after.AppId);
        Assert.Equal("U123", after.BotUserId);
        var appAfter = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken));
        var botAfter = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken));
        Assert.Equal(appBefore, appAfter);
        Assert.Equal(botBefore, botAfter);
    }

    [Fact]
    public async Task Enable_does_not_touch_credentials_owner_setup_or_health()
    {
        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Disabled);

        await RunEnableAsync();

        var after = await GetConnectionAsync();
        Assert.Equal("U_OWNER", after.OwnerSlackUserId);
        Assert.Equal(SetupProgressKind.Complete, after.SetupProgress);
        Assert.Equal(ConnectionHealthKind.Healthy, after.ConnectionHealth);
        Assert.Equal("T123", after.WorkspaceTeamId);
    }

    [Fact]
    public async Task Disabled_connection_reports_Healthy_health_and_Disabled_DesiredState()
    {
        await RunDisableAsync();

        var connection = await _store.GetAsync("project-1", _connectionId);

        Assert.NotNull(connection);
        Assert.Equal(DesiredStateKind.Disabled, connection!.DesiredState);
        Assert.Equal(ConnectionHealthKind.Healthy, connection.ConnectionHealth);
        Assert.Null(connection.HealthReason);
    }

    [Fact]
    public async Task Degraded_connection_with_Enabled_DesiredState_reports_Degraded_not_Disabled()
    {
        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "connectionHealth", "healthReason" },
            connectionHealth: ConnectionHealthKind.Degraded,
            healthReason: "backpressured");

        var connection = await _store.GetAsync("project-1", _connectionId);

        Assert.NotNull(connection);
        Assert.Equal(ConnectionHealthKind.Degraded, connection!.ConnectionHealth);
        Assert.Equal(DesiredStateKind.Enabled, connection.DesiredState);
    }

    [Fact]
    public async Task Disable_does_not_alter_running_accepted_work_records()
    {
        await using (var db = _database.CreateContext())
        {
            db.AgentConnections.Single(r => r.Id == _connectionId);
            await db.SaveChangesAsync();
        }
        var appBefore = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken));
        var botBefore = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken));

        await RunDisableAsync();

        var after = await GetConnectionAsync();
        Assert.Equal("U_OWNER", after.OwnerSlackUserId);
        Assert.Equal(SetupProgressKind.Complete, after.SetupProgress);
        var appAfter = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken));
        var botAfter = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken));
        Assert.Equal(appBefore, appAfter);
        Assert.Equal(botBefore, botAfter);
    }

    [Fact]
    public async Task Disable_preserves_running_AgentJob_and_AgentSession_records()
    {
        const string jobKey = "job-running";
        const string sessionId = "sess-running";
        await using (var db = _database.CreateContext())
        {
            db.AgentJobs.Add(new AgentJobRow
            {
                JobKey = jobKey,
                State = "{\"input\":{\"projectId\":\"project-1\",\"agentId\":\"agent-1\"},\"status\":\"executing\",\"submittedAt\":\"2026-07-29T12:00:00Z\"}",
            });
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                AgentSessionId = sessionId,
                Status = "bound",
                State = "{}",
                CreatedAt = Now.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        await RunDisableAsync();

        await using (var verify = _database.CreateContext())
        {
            var job = await verify.AgentJobs.AsNoTracking().SingleAsync(r => r.JobKey == jobKey);
            Assert.Equal("executing", job.Status);
            Assert.Equal("project-1", job.ProjectId);
            var session = await verify.AgentSessions.AsNoTracking().SingleAsync(r => r.Id == sessionId);
            Assert.Equal("bound", session.Status);
        }
    }

    [Fact]
    public async Task Delete_preserves_running_AgentJob_and_AgentSession_records()
    {
        const string jobKey = "job-running-delete";
        const string sessionId = "sess-running-delete";
        await using (var db = _database.CreateContext())
        {
            db.AgentJobs.Add(new AgentJobRow
            {
                JobKey = jobKey,
                State = "{\"input\":{\"projectId\":\"project-1\",\"agentId\":\"agent-1\"},\"status\":\"executing\",\"submittedAt\":\"2026-07-29T12:00:00Z\"}",
            });
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = sessionId,
                AgentSessionId = sessionId,
                Status = "bound",
                State = "{}",
                CreatedAt = Now.UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        await RunDeleteAsync();

        await using (var verify = _database.CreateContext())
        {
            var job = await verify.AgentJobs.AsNoTracking().SingleAsync(r => r.JobKey == jobKey);
            Assert.Equal("executing", job.Status);
            Assert.Equal("project-1", job.ProjectId);
            var session = await verify.AgentSessions.AsNoTracking().SingleAsync(r => r.Id == sessionId);
            Assert.Equal("bound", session.Status);
        }
    }

    [Fact]
    public async Task Disable_returns_not_found_for_missing_connection()
    {
        var otherDb = TestSqliteDatabase.CreateMigrated();
        try
        {
            var orphanStore = new AgentConnectionStore(
                new TestDbContextFactory(otherDb.Options),
                new AgentQuerier(new TestDbContextFactory(otherDb.Options)),
                _secrets,
                [],
                _time);

            var updated = await DisableAsync(orphanStore, "project-1", "no-such-connection");

            Assert.Null(updated);
        }
        finally
        {
            otherDb.Dispose();
        }
    }

    [Fact]
    public async Task Ingress_guard_returns_disabled_rejection_at_http_200_for_Disabled_connection()
    {
        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Disabled);

        var decision = await RunIngressGuardAsync();

        Assert.Equal("rejected", decision.Kind);
        Assert.Equal("This Connection is disabled.", decision.Reason);
    }

    [Fact]
    public async Task Ingress_guard_does_not_block_Enabled_connection()
    {
        var decision = await RunIngressGuardAsync();

        Assert.Null(decision.Kind);
    }

    [Fact]
    public async Task Ingress_guard_does_not_create_inbox_entry_for_Disabled_connection()
    {
        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Disabled);

        await using var db = _database.CreateContext();
        var before = await db.SlackProviderInboxRows.CountAsync();

        var decision = await RunIngressGuardAsync();

        Assert.Equal("rejected", decision.Kind);
        var after = await db.SlackProviderInboxRows.CountAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Ingress_guard_rejects_disabled_DM_before_claim_or_owner_check()
    {
        await using (var db = _database.CreateContext())
        {
            var row = await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
            row.OwnerSlackUserId = null;
            row.SetupProgress = SetupProgressKind.ClaimOwner;
            row.DesiredState = DesiredStateKind.Disabled;
            await db.SaveChangesAsync();
        }

        var decision = await RunIngressGuardAsync();

        Assert.Equal("rejected", decision.Kind);
        Assert.Equal("This Connection is disabled.", decision.Reason);
    }

    [Fact]
    public async Task Enable_does_not_replay_or_process_messages_from_disabled_period()
    {
        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Disabled);

        var disabledGuard = await RunIngressGuardAsync();
        Assert.Equal("rejected", disabledGuard.Kind);

        await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Enabled);

        await using var db = _database.CreateContext();
        var inboxRows = await db.SlackProviderInboxRows.CountAsync();
        Assert.Equal(0, inboxRows);
    }

    [Fact]
    public async Task Delete_returns_connection_with_slack_app_removal_note()
    {
        var outcome = await RunDeleteAsync();

        Assert.NotNull(outcome.Connection);
        Assert.Equal(_connectionId, outcome.Connection!.Id);
        Assert.NotNull(outcome.Connection.DeletedAt);
        Assert.False(string.IsNullOrWhiteSpace(outcome.SlackAppRemovalNote));
        Assert.Contains("Slack App remains installed", outcome.SlackAppRemovalNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_removes_credentials_but_preserves_other_facts()
    {
        await RunDeleteAsync();

        var app = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken));
        var bot = await _secrets.LoadAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken));
        Assert.Null(app);
        Assert.Null(bot);
    }

    private async Task<AgentConnectionRow> GetConnectionAsync()
    {
        await using var db = _database.CreateContext();
        return await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
    }

    private static async Task<AgentConnection?> DisableAsync(
        AgentConnectionStore store,
        string projectId,
        string connectionId)
    {
        var connection = await store.GetAsync(projectId, connectionId);
        if (connection is null) return null;
        if (connection.DesiredState == DesiredStateKind.Disabled) return connection;
        return await store.UpdateAsync(projectId, connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Disabled);
    }

    private async Task<AgentConnection?> RunDisableAsync()
    {
        return await DisableAsync(_store, "project-1", _connectionId);
    }

    private async Task<AgentConnection?> RunEnableAsync()
    {
        var connection = await _store.GetAsync("project-1", _connectionId);
        if (connection is null) return null;
        if (connection.DesiredState == DesiredStateKind.Enabled) return connection;
        return await _store.UpdateAsync("project-1", _connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: DesiredStateKind.Enabled);
    }

    private async Task<IngressGuardDecision> RunIngressGuardAsync()
    {
        var connection = await _store.GetAsync("project-1", _connectionId);
        if (connection is null) return new IngressGuardDecision(null, null);
        if (connection.DesiredState == DesiredStateKind.Disabled)
            return new IngressGuardDecision("rejected", "This Connection is disabled.");
        return new IngressGuardDecision(null, null);
    }

    private async Task<DeleteOutcome> RunDeleteAsync()
    {
        var connection = await _store.DeleteAsync("project-1", _connectionId);
        if (connection is null) return new DeleteOutcome(null, null);
        return new DeleteOutcome(connection, "Mohist-side records (credentials, inbox entries, conversation mappings, pending outbound deliveries, and owner claim codes) were removed. The Slack App remains installed on the workspace until a workspace admin uninstalls it manually.");
    }

    private sealed record IngressGuardDecision(string? Kind, string? Reason);
    private sealed record DeleteOutcome(AgentConnection? Connection, string? SlackAppRemovalNote);

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _values = [];
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) { _values[address] = plaintext; return Task.CompletedTask; }
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(address));
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(_values.Remove(address));
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class RecordingSlackApiClient : ISlackApiClient
    {
        public SlackAuthTestResponse AuthTest { get; set; } = new(true, null, "T123", "Workspace", "U123", "Mohist", "B123", "A123");
        public SlackBotInfoResponse BotsInfo { get; set; } = new(true, null, new("B123", "Mohist", "A123"));
        public SlackPermissionsScopesListResponse PermissionsScopesList { get; set; } = new(true, null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["im"] = ["chat:write", "im:history"],
            ["team"] = ["users:read"],
        });
        public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) => Task.FromResult(AuthTest);
        public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) => Task.FromResult(BotsInfo);
        public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) => Task.FromResult(PermissionsScopesList);
        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUserInfoResponse(true, null, null));
        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackConversationInfoResponse(true, null, null));
        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUsersListResponse(true, null, [], null));
    }
}