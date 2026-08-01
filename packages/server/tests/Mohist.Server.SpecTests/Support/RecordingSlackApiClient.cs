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
    public SlackUserInfoResponse DefaultUsersInfo { get; set; } = new(true, null, new("U_OWNER", "T123", false, false, false, false, false));
    public SlackUserInfoResponse UsersInfo
    {
        get => DefaultUsersInfo;
        set => DefaultUsersInfo = value;
    }
    public Dictionary<string, SlackUserInfoResponse> UsersInfoByUser { get; } = new(StringComparer.Ordinal);
    public Func<string, SlackUserInfoResponse>? UsersInfoResolver { get; set; }
    public Queue<SlackConversationInfoResponse> ConversationsInfoResponses { get; } = new();
    public Func<string, SlackConversationInfoResponse>? ConversationsInfoResolver { get; set; }
    public SlackConversationInfoResponse DefaultConversationsInfo { get; set; } = new(true, null, new("C-default", null, null, false, true));
    public Queue<SlackConversationsRepliesPage> ConversationsRepliesPages { get; } = new();
    public SlackConversationsRepliesPage? ConversationsRepliesError { get; set; }
    public List<string> UsersInfoCalls { get; } = new();
    public List<string> ConversationsInfoCalls { get; } = new();

    public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) => Task.FromResult(AppsConnectionOpen);
    public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) => Task.FromResult(AuthTest);
    public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) => Task.FromResult(BotsInfo);
    public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) => Task.FromResult(PermissionsScopesList);
    public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default)
    {
        UsersInfoCalls.Add(userId);
        if (UsersInfoResolver is not null)
            return Task.FromResult(UsersInfoResolver(userId));
        if (UsersInfoByUser.TryGetValue(userId, out var configured))
            return Task.FromResult(configured);
        return Task.FromResult(DefaultUsersInfo);
    }
    public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default)
    {
        ConversationsInfoCalls.Add(conversationId);
        if (ConversationsInfoResolver is not null)
            return Task.FromResult(ConversationsInfoResolver(conversationId));
        if (ConversationsInfoResponses.Count > 0)
            return Task.FromResult(ConversationsInfoResponses.Dequeue());
        return Task.FromResult(DefaultConversationsInfo);
    }
    public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) => Task.FromResult(new SlackUsersListResponse(true, null, [], null));
    public Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
        string conversationId,
        string threadTs,
        string? cursor,
        string botToken,
        CancellationToken ct = default)
    {
        if (ConversationsRepliesError is not null)
            return Task.FromResult(ConversationsRepliesError);
        if (ConversationsRepliesPages.Count > 0)
            return Task.FromResult(ConversationsRepliesPages.Dequeue());
        return Task.FromResult(new SlackConversationsRepliesPage(true, null, [], null));
    }
}