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

public sealed class SlackSetupVerifierSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeSecretStore _secrets = new();
    private readonly RecordingSlackApiClient _slack = new();
    private TestSqliteDatabase _database = null!;
    private SlackSetupVerifier _verifier = null!;

    public async ValueTask InitializeAsync()
    {
        _database = TestSqliteDatabase.CreateMigrated();
        await using (var db = _database.CreateContext())
        {
            db.AgentConnections.Add(new AgentConnectionRow
            {
                Id = "conn-1",
                ProjectId = "project-1",
                AgentId = "agent-1",
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = string.Empty,
                AppId = string.Empty,
                BotUserId = string.Empty,
                SetupProgress = SetupProgressKind.WaitingForSlackService,
                DesiredState = DesiredStateKind.Enabled,
                ConnectionHealth = ConnectionHealthKind.Healthy,
                AgentReadiness = AgentReadinessKind.Unknown,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await db.SaveChangesAsync();
        }

        await _secrets.StoreAsync(new SecretStoreAddress("project-1", "conn-1", SecretKind.BotToken), Encoding.UTF8.GetBytes("xoxb-token"));
        var factory = new TestDbContextFactory(_database.Options);
        var connections = new AgentConnectionStore(factory, new AgentQuerier(factory), _secrets, [], _time);
        _verifier = new SlackSetupVerifier(_slack, _secrets, connections, _time);
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task VerifiedSlackPayloadsBindWorkspaceAppAndBotUserBeforeClaim()
    {
        var result = await _verifier.VerifyAsync("project-1", "conn-1");

        var connection = await GetConnectionAsync();
        Assert.True(result.Verified);
        Assert.Equal(SetupProgressKind.ClaimOwner, connection.SetupProgress);
        Assert.Equal("T123", connection.WorkspaceTeamId);
        Assert.Equal("A123", connection.AppId);
        Assert.Equal("U123", connection.BotUserId);
        Assert.Equal("Mohist", connection.VerifiedBotName);
        Assert.Equal("https://slack/icon-48.png", connection.VerifiedBotIconUrl);
        Assert.Equal(["auth.test", "bots.info:B123", "apps.permissions.scopes.list"], _slack.Calls);
    }

    [Fact]
    public async Task DifferentBotIdentityLeavesConnectionUnbound()
    {
        _slack.BotsInfo = new(true, null, new("B999", "Mohist", "A123"));

        var result = await _verifier.VerifyAsync("project-1", "conn-1");

        var connection = await GetConnectionAsync();
        Assert.False(result.Verified);
        Assert.Equal(SetupProgressKind.FixSlackSetup, connection.SetupProgress);
        Assert.Equal(string.Empty, connection.WorkspaceTeamId);
        Assert.Equal(string.Empty, connection.AppId);
        Assert.Equal(string.Empty, connection.BotUserId);
    }

    [Fact]
    public void SlackBotInfo_uses_the_highest_resolution_icon()
    {
        var bot = new SlackBotInfo(
            "B123",
            "Mohist",
            "A123",
            new SlackBotIcons(Image48: "48", Image1024: "1024"));

        Assert.Equal("1024", bot.IconUrl);
    }

    [Fact]
    public async Task MissingGrantedScopeLeavesConnectionUnbound()
    {
        _slack.PermissionsScopesList = new(true, null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["im"] = ["chat:write"],
            ["team"] = ["users:read"],
        });

        var result = await _verifier.VerifyAsync("project-1", "conn-1");

        var connection = await GetConnectionAsync();
        Assert.False(result.Verified);
        Assert.Equal(SetupProgressKind.FixSlackSetup, connection.SetupProgress);
        Assert.Contains("im:history", result.Reason);
        Assert.Equal(string.Empty, connection.WorkspaceTeamId);
        Assert.Equal(string.Empty, connection.AppId);
        Assert.Equal(string.Empty, connection.BotUserId);
    }

    private async Task<AgentConnectionRow> GetConnectionAsync()
    {
        await using var db = _database.CreateContext();
        return await db.AgentConnections.SingleAsync();
    }

    private sealed class RecordingSlackApiClient : ISlackApiClient
    {
        public List<string> Calls { get; } = [];
        public SlackAuthTestResponse AuthTest { get; set; } = new(true, null, "T123", "Workspace", "U123", "Mohist", "B123", "A123");
        public SlackBotInfoResponse BotsInfo { get; set; } = new(true, null,
            new("B123", "Mohist", "A123", new SlackBotIcons(Image48: "https://slack/icon-48.png")));
        public SlackPermissionsScopesListResponse PermissionsScopesList { get; set; } = new(true, null, new Dictionary<string, IReadOnlyList<string>>
        {
            ["im"] = ["chat:write", "im:history"],
            ["team"] = ["users:read"],
        });

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

        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUserInfoResponse(true, null, null));
        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackConversationInfoResponse(true, null, null));
        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUsersListResponse(true, null, [], null));
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _values = [];
        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default) { _values[address] = plaintext; return Task.CompletedTask; }
        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(_values.GetValueOrDefault(address));
        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) => Task.FromResult(_values.Remove(address));
        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }
}
