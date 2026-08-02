using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackConnectionRotateCredentialsSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly SlackConnectionRotateCredentialsSpecs.FakeSecretStore _secrets = new();
    private readonly RecordingSlackApiClient _slack = new();
    private TestSqliteDatabase _database = null!;
    private SlackSetupVerifier _verifier = null!;
    private AgentConnectionStore _store = null!;
    private string _connectionId = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        _connectionId = "conn-rotate";
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
        await _secrets.StoreAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.AppToken), Encoding.UTF8.GetBytes("xapp-old"));
        await _secrets.StoreAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-old"));
        var factory = new TestDbContextFactory(_database.Options);
        _store = new AgentConnectionStore(factory, new AgentQuerier(factory), _secrets, [], _time);
        _verifier = new SlackSetupVerifier(_slack, _secrets, _store, _time);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task VerifyRotationAsync_returns_resolved_identity_for_valid_tokens()
    {
        var result = await _verifier.VerifyRotationAsync("project-1", _connectionId, "xapp-new", "xoxb-new");

        Assert.True(result.Verified);
        Assert.Null(result.Reason);
        Assert.Equal("T123", result.ResolvedTeamId);
        Assert.Equal("A123", result.ResolvedAppId);
        Assert.Equal("U123", result.ResolvedBotUserId);
        Assert.Equal("Mohist", result.VerifiedBotName);
        Assert.Equal("https://slack/icon-48.png", result.VerifiedBotIconUrl);
    }

    [Fact]
    public async Task VerifyRotationAsync_rejects_invalid_token_with_reason()
    {
        _slack.AuthTest = new(false, "invalid_auth", null, null, null, null, null, null);

        var result = await _verifier.VerifyRotationAsync("project-1", _connectionId, "xapp-new", "xoxb-bad");

        Assert.False(result.Verified);
        Assert.Contains("invalid_auth", result.Reason);
        Assert.Null(result.ResolvedTeamId);
    }

    [Fact]
    public async Task VerifyRotationAsync_rejects_missing_scope()
    {
        _slack.PermissionsScopesList = new(true, null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["im"] = ["chat:write"],
            ["team"] = ["users:read"],
        });

        var result = await _verifier.VerifyRotationAsync("project-1", _connectionId, "xapp-new", "xoxb-new");

        Assert.False(result.Verified);
        Assert.Contains("im:history", result.Reason);
    }

    [Fact]
    public async Task VerifyRotationAsync_returns_different_identity_when_workspace_changes()
    {
        _slack.AppsConnectionOpen = new(true, null, "wss://socket.slack.com/?app_id=A999");
        _slack.AuthTest = new(true, null, "T999", "Other", "U999", "OtherBot", "B999", "A999");
        _slack.BotsInfo = new(true, null, new("B999", "OtherBot", "A999"));

        var result = await _verifier.VerifyRotationAsync("project-1", _connectionId, "xapp-new", "xoxb-new");

        Assert.True(result.Verified);
        Assert.Equal("T999", result.ResolvedTeamId);
        Assert.Equal("A999", result.ResolvedAppId);
        Assert.Equal("U999", result.ResolvedBotUserId);
    }

    [Fact]
    public async Task VerifyRotationAsync_does_not_rebind_or_mutate_Store()
    {
        await _verifier.VerifyRotationAsync("project-1", _connectionId, "xapp-new", "xoxb-new");

        var connection = await GetConnectionAsync();
        Assert.Equal("T123", connection.WorkspaceTeamId);
        Assert.Equal("A123", connection.AppId);
        Assert.Equal("U123", connection.BotUserId);
        Assert.Equal(SetupProgressKind.Complete, connection.SetupProgress);
        Assert.Equal("U_OWNER", connection.OwnerSlackUserId);
    }

    [Fact]
    public async Task VerifyRotationAsync_works_on_FixSlackSetup_connection_with_bound_identity()
    {
        await _store.UpdateAsync("project-1", _connectionId, new HashSet<string>(StringComparer.Ordinal)
        {
            "setupProgress", "connectionHealth", "healthReason"
        }, setupProgress: SetupProgressKind.FixSlackSetup, connectionHealth: ConnectionHealthKind.Unhealthy, healthReason: "Slack rejected the Bot token: invalid_auth");

        var result = await _verifier.VerifyRotationAsync("project-1", _connectionId, "xapp-new", "xoxb-new");

        Assert.True(result.Verified);
    }

    private async Task<AgentConnectionRow> GetConnectionAsync()
    {
        await using var db = _database.CreateContext();
        return await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
    }

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
        public List<string> Calls { get; } = [];
        public SlackAppsConnectionOpenResponse AppsConnectionOpen { get; set; } = new(true, null, "wss://socket.slack.com/?app_id=A123");
        public SlackAuthTestResponse AuthTest { get; set; } = new(true, null, "T123", "Workspace", "U123", "Mohist", "B123", "A123");
        public SlackBotInfoResponse BotsInfo { get; set; } = new(true, null,
            new("B123", "Mohist", "A123", new SlackBotIcons(Image48: "https://slack/icon-48.png")));
        public SlackPermissionsScopesListResponse PermissionsScopesList { get; set; } = new(true, null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["im"] = ["chat:write", "im:history"],
            ["team"] = ["users:read"],
        });

        public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default)
        {
            Calls.Add("apps.connections.open");
            return Task.FromResult(AppsConnectionOpen);
        }

        public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default)
        {
            Calls.Add("auth.test");
            return Task.FromResult(AuthTest);
        }

        public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default)
        {
            Calls.Add($"bots.info:{botId}");
            return Task.FromResult(BotsInfo);
        }

        public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default)
        {
            Calls.Add("apps.permissions.scopes.list");
            return Task.FromResult(PermissionsScopesList);
        }

        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackUserInfoResponse(true, null, null));
        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackConversationInfoResponse(true, null, null));
        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackUsersListResponse(true, null, [], null));
        public Task<SlackFileContent> OpenFileContentAsync(string fileId, string botToken, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
            string conversationId,
            string threadTs,
            string? cursor,
            string botToken,
            CancellationToken ct = default) =>
            Task.FromResult(new SlackConversationsRepliesPage(true, null, [], null));
    }
}
