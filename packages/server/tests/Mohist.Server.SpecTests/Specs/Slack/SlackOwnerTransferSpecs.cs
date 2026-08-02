using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class SlackOwnerTransferSpecs : IAsyncLifetime
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
        _connectionId = "conn-transfer";
        await using (var db = _database.CreateContext())
        {
            db.AgentConnections.Add(new AgentConnectionRow
            {
                Id = _connectionId,
                ProjectId = "project-1",
                AgentId = "agent-1",
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = "T1",
                AppId = "A1",
                BotUserId = "U_BOT",
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
        await _secrets.StoreAsync(new SecretStoreAddress("project-1", _connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes("bot-token"));
        var factory = new TestDbContextFactory(_database.Options);
        _claims = new SlackOwnerClaimService(factory, _secrets, _slack, _time, new SlackConnectionAccessDecider(factory, new SlackConnectionAllowedMemberStore(factory, _time), _slack, _secrets));
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GenerateTransferAsync_generates_initial_kind_for_existing_owner_with_complete_setup()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);

        await using var db = _database.CreateContext();
        var row = await db.SlackOwnerClaimCodes.SingleAsync();
        Assert.Equal(SlackOwnerClaimCodeKinds.Transfer, row.Kind);
        Assert.NotEqual(code.Value, row.CodeHash);
    }

    [Fact]
    public async Task GenerateAsync_without_kind_defaults_to_initial()
    {
        await using (var db = _database.CreateContext())
        {
            var row = await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
            row.OwnerSlackUserId = null;
            row.SetupProgress = SetupProgressKind.ClaimOwner;
            await db.SaveChangesAsync();
        }

        var code = await _claims.GenerateAsync("project-1", _connectionId);

        await using var db2 = _database.CreateContext();
        var claim = await db2.SlackOwnerClaimCodes.SingleAsync();
        Assert.Equal(SlackOwnerClaimCodeKinds.Initial, claim.Kind);
    }

    [Fact]
    public async Task GenerateTransferAsync_rejects_when_no_existing_owner()
    {
        await using (var db = _database.CreateContext())
        {
            var row = await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
            row.OwnerSlackUserId = null;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer));
    }

    [Fact]
    public async Task GenerateTransferAsync_rejects_when_setup_not_complete()
    {
        await using (var db = _database.CreateContext())
        {
            var row = await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
            row.SetupProgress = SetupProgressKind.ClaimOwner;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer));
    }

    [Fact]
    public async Task RegeneratingTransferCode_invalidatesPriorTransferCode()
    {
        var first = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        var second = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);

        _slack.UsersInfo = new(true, null, new("U_NEW", "T1", false, false, false, false, false));

        var oldResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", first.Value));
        Assert.Equal(SlackInboundDecisionKind.Rejected, oldResult.Kind);
        Assert.Contains("no longer valid", oldResult.Reason, StringComparison.OrdinalIgnoreCase);

        var newResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", second.Value));
        Assert.Equal(SlackInboundDecisionKind.Transferred, newResult.Kind);
    }

    [Fact]
    public async Task Supersession_isScopedToKind()
    {
        var firstTransfer = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        var secondTransfer = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);

        await using var db = _database.CreateContext();
        var transferRows = await db.SlackOwnerClaimCodes
            .Where(row => row.Kind == SlackOwnerClaimCodeKinds.Transfer)
            .ToListAsync();
        Assert.Equal(2, transferRows.Count);
        var supersededCount = transferRows.Count(row => row.SupersededBy is not null);
        var activeCount = transferRows.Count(row => row.SupersededBy is null);
        Assert.Equal(1, supersededCount);
        Assert.Equal(1, activeCount);
    }

    [Fact]
    public async Task ExpiredTransferCode_isRejectedAndUserToldToGenerateNewOne()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);

        _time.Advance(TimeSpan.FromMinutes(11));

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", code.Value));

        Assert.Equal(SlackInboundDecisionKind.Rejected, result.Kind);
        Assert.Contains("expired", result.Reason, StringComparison.OrdinalIgnoreCase);
        await using var db = _database.CreateContext();
        Assert.Equal("U_OWNER", (await db.AgentConnections.SingleAsync(r => r.Id == _connectionId)).OwnerSlackUserId);
    }

    [Fact]
    public async Task AtomicSwap_changes_owner_and_old_owner_loses_privileges()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        _slack.UsersInfo = new(true, null, new("U_NEW", "T1", false, false, false, false, false));

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", code.Value));

        Assert.Equal(SlackInboundDecisionKind.Transferred, result.Kind);

        await using var db = _database.CreateContext();
        var connection = await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
        Assert.Equal("U_NEW", connection.OwnerSlackUserId);

        var priorResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_OWNER", "do work"));
        Assert.Equal(SlackInboundDecisionKind.Rejected, priorResult.Kind);
        Assert.Contains("only to its owner", priorResult.Reason, StringComparison.OrdinalIgnoreCase);

        var newResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", "do work"));
        Assert.Equal(SlackInboundDecisionKind.AcceptedOwnerTask, newResult.Kind);
    }

    [Fact]
    public async Task AlreadyUsedTransferCode_isRejected()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        _slack.UsersInfo = new(true, null, new("U_NEW", "T1", false, false, false, false, false));

        var first = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", code.Value));
        Assert.Equal(SlackInboundDecisionKind.Transferred, first.Kind);

        var replay = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", code.Value));
        Assert.Equal(SlackInboundDecisionKind.Rejected, replay.Kind);
        Assert.Contains("no longer valid", replay.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelfTransfer_isRejected()
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        _slack.UsersInfo = new(true, null, new("U_OWNER", "T1", false, false, false, false, false));

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_OWNER", code.Value));

        Assert.Equal(SlackInboundDecisionKind.Rejected, result.Kind);
        Assert.Contains("already the owner", result.Reason, StringComparison.OrdinalIgnoreCase);
        await using var db = _database.CreateContext();
        Assert.Equal("U_OWNER", (await db.AgentConnections.SingleAsync(r => r.Id == _connectionId)).OwnerSlackUserId);
    }

    [Theory]
    [InlineData(true, false, false, false, "T1", true)]
    [InlineData(true, true, false, false, "T1", false)]
    [InlineData(true, false, true, false, "T1", false)]
    [InlineData(true, false, false, true, "T1", false)]
    [InlineData(true, false, false, false, "T_OTHER", false)]
    public async Task OnlyCurrentRegularBoundMemberCanTransfer(
        bool setResponse,
        bool isBot,
        bool deleted,
        bool guest,
        string teamId,
        bool expectTransferred)
    {
        var code = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        if (setResponse)
        {
            _slack.UsersInfoResponse = u => new(true, null, new("U_NEW", teamId, isBot, deleted, false, false, guest));
        }
        else
        {
            _slack.UsersInfoResponse = _ => new(true, null, null);
        }

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_NEW", code.Value));

        if (expectTransferred)
        {
            Assert.Equal(SlackInboundDecisionKind.Transferred, result.Kind);
            await using var db = _database.CreateContext();
            Assert.Equal("U_NEW", (await db.AgentConnections.SingleAsync(r => r.Id == _connectionId)).OwnerSlackUserId);
        }
        else
        {
            Assert.Equal(SlackInboundDecisionKind.Rejected, result.Kind);
            await using var db = _database.CreateContext();
            Assert.Equal("U_OWNER", (await db.AgentConnections.SingleAsync(r => r.Id == _connectionId)).OwnerSlackUserId);
        }
    }

    [Fact]
    public async Task ReplayOfSupersededTransferCode_isRejected()
    {
        var firstCode = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);
        var secondCode = await _claims.GenerateAsync("project-1", _connectionId, SlackOwnerClaimCodeKinds.Transfer);

        _slack.UsersInfoResponse = u => new SlackUserInfoResponse(true, null, new(u, "T1", false, false, false, false, false));

        var firstResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_FIRST", firstCode.Value));
        Assert.Equal(SlackInboundDecisionKind.Rejected, firstResult.Kind);
        Assert.Contains("no longer valid", firstResult.Reason, StringComparison.OrdinalIgnoreCase);

        var secondResult = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_SECOND", secondCode.Value));
        Assert.Equal(SlackInboundDecisionKind.Transferred, secondResult.Kind);

        await using var db = _database.CreateContext();
        Assert.Equal("U_SECOND", (await db.AgentConnections.SingleAsync(r => r.Id == _connectionId)).OwnerSlackUserId);
    }

    private async Task<string?> GetOwnerSlackUserIdAsync()
    {
        await using var db = _database.CreateContext();
        return (await db.AgentConnections.SingleAsync(r => r.Id == _connectionId)).OwnerSlackUserId;
    }

    [Fact]
    public async Task InitialClaimPath_isUnchanged()
    {
        await using (var db = _database.CreateContext())
        {
            var row = await db.AgentConnections.SingleAsync(r => r.Id == _connectionId);
            row.OwnerSlackUserId = null;
            row.SetupProgress = SetupProgressKind.ClaimOwner;
            await db.SaveChangesAsync();
        }

        _claims = new SlackOwnerClaimService(new TestDbContextFactory(_database.Options), _secrets, _slack, _time, new SlackConnectionAccessDecider(new TestDbContextFactory(_database.Options), new SlackConnectionAllowedMemberStore(new TestDbContextFactory(_database.Options), _time), _slack, _secrets));

        var code = await _claims.GenerateAsync("project-1", _connectionId);
        _slack.UsersInfo = new(true, null, new("U_CLAIMER", "T1", false, false, false, false, false));

        var result = await _claims.HandleInboundDmAsync("project-1", _connectionId, new("U_CLAIMER", code.Value));

        Assert.Equal(SlackInboundDecisionKind.Claimed, result.Kind);
        await using var db2 = _database.CreateContext();
        var connection = await db2.AgentConnections.SingleAsync(r => r.Id == _connectionId);
        Assert.Equal("U_CLAIMER", connection.OwnerSlackUserId);
        Assert.Equal(SetupProgressKind.Complete, connection.SetupProgress);
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
        public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackAppsConnectionOpenResponse(true, null, "wss://socket.slack.com/?app_id=A1"));

        public Func<string, SlackUserInfoResponse>? UsersInfoResponse { get; set; }
        public SlackUserInfoResponse UsersInfo
        {
            get => new(true, null, new("U_NEW", "T1", false, false, false, false, false));
            set => UsersInfoResponse = _ => value;
        }
        public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) => Task.FromResult(new SlackAuthTestResponse(true, null, "T1", "Workspace", "U_BOT", "Mohist", "B1", "A1"));
        public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackBotInfoResponse(true, null, null));
        public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) => Task.FromResult(new SlackPermissionsScopesListResponse(true, null, null));
        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) =>
            Task.FromResult(UsersInfoResponse?.Invoke(userId) ?? new SlackUserInfoResponse(true, null, new(userId, "T1", false, false, false, false, false)));
        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackConversationInfoResponse(true, null, null));
        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUsersListResponse(true, null, [], null));
        public Task<SlackFileContent> OpenFileContentAsync(string fileId, string botToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
            string conversationId,
            string threadTs,
            string? cursor,
            string botToken,
            CancellationToken ct = default) => Task.FromResult(new SlackConversationsRepliesPage(true, null, [], null));
    }
}
