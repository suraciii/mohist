using System.Net;
using System.Text;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Slack;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Slack;

public sealed class ConnectionDiagnosticSpecs
{
    [Fact]
    public async Task Owner_probe_reports_unavailable_for_departed_or_ineligible_member()
    {
        var secrets = new FakeSecretStore();
        await StoreBotTokenAsync(secrets);
        var slack = new RecordingSlackApiClient
        {
            UsersInfo = new(true, null, new("U_OWNER", "T123", false, true, false, false, false)),
        };

        var availability = await SlackConnectionRoutes.ProbeOwnerAvailabilityAsync(
            Connection(), secrets, slack, CancellationToken.None);

        Assert.Equal(OwnerAvailabilityKind.Unavailable, availability);
        Assert.Equal(["U_OWNER"], slack.UserIds);
    }

    [Fact]
    public async Task Owner_probe_reports_available_for_current_regular_member()
    {
        var secrets = new FakeSecretStore();
        await StoreBotTokenAsync(secrets);
        var slack = new RecordingSlackApiClient
        {
            UsersInfo = new(true, null, new("U_OWNER", "T123", false, false, false, false, false)),
        };

        var availability = await SlackConnectionRoutes.ProbeOwnerAvailabilityAsync(
            Connection(), secrets, slack, CancellationToken.None);

        Assert.Equal(OwnerAvailabilityKind.Available, availability);
    }

    [Fact]
    public async Task Owner_probe_degrades_to_unknown_when_Slack_is_unreachable()
    {
        var secrets = new FakeSecretStore();
        await StoreBotTokenAsync(secrets);
        var slack = new RecordingSlackApiClient { ThrowOnUsersInfo = true };

        var availability = await SlackConnectionRoutes.ProbeOwnerAvailabilityAsync(
            Connection(), secrets, slack, CancellationToken.None);

        Assert.Equal(OwnerAvailabilityKind.Unknown, availability);
    }

    private static AgentConnection Connection() => new()
    {
        ProjectId = "project-1",
        Id = "connection-1",
        WorkspaceTeamId = "T123",
        OwnerSlackUserId = "U_OWNER",
    };

    private static Task StoreBotTokenAsync(FakeSecretStore secrets) => secrets.StoreAsync(
        new SecretStoreAddress("project-1", "connection-1", SecretKind.BotToken),
        Encoding.UTF8.GetBytes("xoxb-token"));

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<SecretStoreAddress, byte[]> _values = [];

        public Task StoreAsync(SecretStoreAddress address, byte[] plaintext, CancellationToken ct = default)
        {
            _values[address] = plaintext;
            return Task.CompletedTask;
        }

        public Task<byte[]?> LoadAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.GetValueOrDefault(address));

        public Task<bool> DeleteAsync(SecretStoreAddress address, CancellationToken ct = default) =>
            Task.FromResult(_values.Remove(address));

        public IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> values) => values;
    }

    private sealed class RecordingSlackApiClient : ISlackApiClient
    {
        public List<string> UserIds { get; } = [];
        public SlackUserInfoResponse UsersInfo { get; init; } = new(true, null, null);
        public bool ThrowOnUsersInfo { get; init; }

        public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackAppsConnectionOpenResponse(true, null, "wss://socket.slack.com/?app_id=A123"));

        public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackAuthTestResponse(false, null, null, null, null, null, null, null));

        public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackBotInfoResponse(false, null, null));

        public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackPermissionsScopesListResponse(false, null, null));

        public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default)
        {
            UserIds.Add(userId);
            if (ThrowOnUsersInfo)
                throw new HttpRequestException("Slack unavailable");
            return Task.FromResult(UsersInfo);
        }

        public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackConversationInfoResponse(false, null, null));

        public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) =>
            Task.FromResult(new SlackUsersListResponse(false, null, [], null));
    }
}
