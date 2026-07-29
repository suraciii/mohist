using Mohist.Server.Slack;

namespace Mohist.Server.Tests.Slack;

public sealed class RecordingSlackApiClient : ISlackApiClient
{
    public List<string> Calls { get; } = [];
    public SlackAuthTestResponse AuthTest { get; set; } = new(true, null, "T1", "Workspace", "U1", "bot", "A1");
    public SlackBotInfoResponse BotsInfo { get; set; } = new(true, null, new("U1", "bot", "A1", ["chat:write", "users:read", "im:history"]));
    public SlackUserInfoResponse UsersInfo { get; set; } = new(true, null, new("U1", "T1", false, false, false, false, false));
    public SlackConversationInfoResponse ConversationsInfo { get; set; } = new(true, null, new("D1", null, null, true, true));
    public SlackUsersListResponse UsersList { get; set; } = new(true, null, [], null);

    public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default)
    {
        Calls.Add("auth.test");
        return Task.FromResult(AuthTest);
    }

    public Task<SlackBotInfoResponse> BotsInfoAsync(string botUserId, string botToken, CancellationToken ct = default)
    {
        Calls.Add("bots.info");
        return Task.FromResult(BotsInfo);
    }

    public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default)
    {
        Calls.Add("users.info");
        return Task.FromResult(UsersInfo);
    }

    public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default)
    {
        Calls.Add("conversations.info");
        return Task.FromResult(ConversationsInfo);
    }

    public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default)
    {
        Calls.Add("users.list");
        return Task.FromResult(UsersList);
    }
}
