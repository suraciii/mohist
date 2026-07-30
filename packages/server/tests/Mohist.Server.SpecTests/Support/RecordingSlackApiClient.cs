using Mohist.Server.Slack;

namespace Mohist.Server.SpecTests.Support;

public sealed class RecordingSlackApiClient : ISlackApiClient
{
    public SlackAppsConnectionOpenResponse AppsConnectionOpen { get; set; } = new(true, null, "wss://socket.slack.com/?app_id=A123");
    public SlackAuthTestResponse AuthTest { get; set; } = new(true, null, "T123", "Workspace", "U123", "Mohist", "B123", "A123");
    public SlackBotInfoResponse BotsInfo { get; set; } = new(true, null, new("B123", "Mohist", "A123"));
    public SlackPermissionsScopesListResponse PermissionsScopesList { get; set; } = new(true, null, new Dictionary<string, IReadOnlyList<string>>
    {
        ["im"] = ["chat:write", "im:history"],
        ["team"] = ["users:read"],
    });
    public SlackUserInfoResponse UsersInfo { get; set; } = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));

    public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) => Task.FromResult(AppsConnectionOpen);
    public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) => Task.FromResult(AuthTest);
    public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) => Task.FromResult(BotsInfo);
    public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) => Task.FromResult(PermissionsScopesList);
    public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) => Task.FromResult(UsersInfo);
    public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackConversationInfoResponse(true, null, null));
    public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUsersListResponse(true, null, [], null));
}
