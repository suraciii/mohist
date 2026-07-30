using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackOwnerClaimSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSecretStore _secrets = new();
    private readonly RecordingSlackApiClient _slack = new();
    private TestSqliteDatabase _database = null!;
    private SlackOwnerClaimService _claims = null!;
    private string _connectionId = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        await using var db = _database.CreateContext();
        _connectionId = "conn-claim";
        db.AgentConnections.Add(new Infrastructure.Data.Agent.AgentConnectionRow
        {
            Id = _connectionId,
            ProjectId = "project-1",
            AgentId = "agent-1",
            ProviderKind = ConnectionProviderKind.Slack,
            WorkspaceTeamId = "team-1",
            AppId = "app-1",
            BotUserId = "bot-1",
            BotName = "Mohist",
            SetupProgress = SetupProgressKind.ClaimOwner,
            DesiredState = DesiredStateKind.Enabled,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            AgentReadiness = AgentReadinessKind.Unknown,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await db.SaveChangesAsync();
        await _secrets.StoreAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes("bot-token"));
        _claims = new SlackOwnerClaimService(new TestDbContextFactory(_database.Options), _secrets, _slack, _time);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CodeIsHashedAndExpires()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId);

        await using (var db = _database.CreateContext())
        {
            var row = await db.SlackOwnerClaimCodes.SingleAsync();
            Assert.NotEqual(code.Value, row.CodeHash);
            Assert.Equal(64, row.CodeHash.Length);
            Assert.Null(row.UsedAt);
        }

        _time.Advance(TimeSpan.FromMinutes(10));
        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U1", code.Value));
        Assert.Equal(SlackInboundDecisionKind.Rejected, result.Kind);
        Assert.Contains("expired", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegenerationSupersedesPriorCode()
    {
        var first = await _claims.GenerateAsync("project-1", _connectionId);
        var second = await _claims.GenerateAsync("project-1", _connectionId);

        var oldResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U1", first.Value));
        Assert.Equal(SlackInboundDecisionKind.Rejected, oldResult.Kind);
        Assert.Contains("no longer valid", oldResult.Reason, StringComparison.OrdinalIgnoreCase);

        var currentResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U1", second.Value));
        Assert.Equal(SlackInboundDecisionKind.Claimed, currentResult.Kind);
    }

    [Theory]
    [InlineData(true, false, false, false, "team-1")]
    [InlineData(false, true, false, false, "team-1")]
    [InlineData(false, false, true, false, "team-1")]
    [InlineData(false, false, false, true, "team-1")]
    [InlineData(false, false, false, false, "team-2")]
    public async Task OnlyCurrentRegularBoundMemberCanClaim(
        bool regular,
        bool isBot,
        bool deleted,
        bool guest,
        string teamId)
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId);
        _slack.UsersInfo = new(true, null, new("U1", teamId, isBot, deleted, false, false, guest));

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U1", code.Value));

        if (regular)
        {
            Assert.Equal(SlackInboundDecisionKind.Claimed, result.Kind);
            Assert.Equal("U1", (await GetConnectionAsync()).OwnerSlackUserId);
            Assert.Equal(SetupProgressKind.Complete, (await GetConnectionAsync()).SetupProgress);
        }
        else
        {
            Assert.Equal(SlackInboundDecisionKind.Rejected, result.Kind);
            Assert.Contains("regular member", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await GetConnectionAsync()).OwnerSlackUserId);
        }
    }

    [Fact]
    public async Task ClaimIsSingleUseAndOwnerCannotBeReplaced()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId);
        var claimed = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U1", code.Value));
        var replayed = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U2", code.Value));

        Assert.Equal(SlackInboundDecisionKind.Claimed, claimed.Kind);
        Assert.Equal(SlackInboundDecisionKind.Rejected, replayed.Kind);
        Assert.Equal("U1", (await GetConnectionAsync()).OwnerSlackUserId);
    }

    [Fact]
    public async Task NonOwnerDmIsExplicitlyRejectedWithoutInboxEntry()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId);
        await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U1", code.Value));

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U2", "do work"));

        Assert.Equal(SlackInboundDecisionKind.Rejected, result.Kind);
        Assert.Contains("only to its owner", result.Reason, StringComparison.OrdinalIgnoreCase);
        await using var db = _database.CreateContext();
        Assert.Empty(await db.SlackProviderInboxRows.ToListAsync());
    }

    private async Task<Infrastructure.Data.Agent.AgentConnectionRow> GetConnectionAsync()
    {
        await using var db = _database.CreateContext();
        return await db.AgentConnections.SingleAsync(row => row.Id == _connectionId);
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
        public SlackUserInfoResponse UsersInfo { get; set; } = new(true, null, new("U1", "team-1", false, false, false, false, false));
        public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) => Task.FromResult(new SlackAppsConnectionOpenResponse(true, null, "wss://socket.slack.com/?app_id=app-1"));
        public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) => Task.FromResult(new SlackAuthTestResponse(true, null, "team-1", "Workspace", "bot-user-1", "Mohist", "bot-1", "app-1"));
        public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackBotInfoResponse(true, null, null));
        public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) => Task.FromResult(new SlackPermissionsScopesListResponse(true, null, null));
        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) => Task.FromResult(UsersInfo);
        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackConversationInfoResponse(true, null, null));
        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUsersListResponse(true, null, [], null));
    }
}
