using Mohist.Server.Slack;

namespace Mohist.Server.Tests.Slack;

public sealed class RecordingSlackApiClient : ISlackApiClient
{
    public List<string> Calls { get; } = [];
    public SlackAppsConnectionOpenResponse AppsConnectionOpen { get; set; } = new(true, null, "wss://socket.slack.com/?app_id=A1");
    public SlackAuthTestResponse AuthTest { get; set; } = new(true, null, "T1", "Workspace", "U1", "bot", "B1", "A1");
    public SlackBotInfoResponse BotsInfo { get; set; } = new(true, null, new("B1", "bot", "A1"));
    public SlackPermissionsScopesListResponse PermissionsScopesList { get; set; } = new(true, null, new Dictionary<string, IReadOnlyList<string>>
    {
        ["im"] = ["chat:write", "im:history", "channels:history", "groups:history", "mpim:history", "reactions:write"],
        ["team"] = ["users:read"],
    });
    public SlackUserInfoResponse UsersInfo { get; set; } = new(true, null, new("U1", "T1", false, false, false, false, false));
    public SlackConversationInfoResponse ConversationsInfo { get; set; } = new(true, null, new("D1", null, null, true, true));
    public SlackUsersListResponse UsersList { get; set; } = new(true, null, [], null);
    public Queue<SlackConversationsRepliesPage> ConversationsRepliesPages { get; } = new();
    public SlackConversationsRepliesPage ConversationsRepliesResult { get; set; } = new(true, null, [], null);
    public SlackConversationsRepliesPage? ConversationsRepliesError { get; set; }

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
        Calls.Add("bots.info");
        return Task.FromResult(BotsInfo);
    }

    public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default)
    {
        Calls.Add("apps.permissions.scopes.list");
        return Task.FromResult(PermissionsScopesList);
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

    public Task<SlackFileContent> OpenFileContentAsync(string fileId, string botToken, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
        string conversationId,
        string threadTs,
        string? cursor,
        string botToken,
        CancellationToken ct = default)
    {
        Calls.Add($"conversations.replies:{conversationId}:{threadTs}:{cursor}");
        if (ConversationsRepliesError is not null)
            return Task.FromResult(ConversationsRepliesError);
        if (ConversationsRepliesPages.Count > 0)
            return Task.FromResult(ConversationsRepliesPages.Dequeue());
        return Task.FromResult(ConversationsRepliesResult);
    }

    public Task<SlackChatPostMessageResponse> ChatPostMessageAsync(string conversationId, string text, string? threadTs, string? clientMessageId, string botToken, CancellationToken ct = default)
    {
        Calls.Add("chat.postMessage");
        return Task.FromResult(new SlackChatPostMessageResponse(true, null, "1.000", null));
    }

    public Task<SlackChatUpdateResponse> ChatUpdateAsync(string conversationId, string messageTs, string text, string botToken, CancellationToken ct = default)
    {
        Calls.Add("chat.update");
        return Task.FromResult(new SlackChatUpdateResponse(true, null, messageTs, null));
    }

    public Task<SlackReactionResponse> ReactionsAddAsync(string conversationId, string reaction, string messageTs, string botToken, CancellationToken ct = default)
    {
        Calls.Add("reactions.add");
        return Task.FromResult(new SlackReactionResponse(true, null));
    }

    public Task<SlackReactionResponse> ReactionsRemoveAsync(string conversationId, string reaction, string messageTs, string botToken, CancellationToken ct = default)
    {
        Calls.Add("reactions.remove");
        return Task.FromResult(new SlackReactionResponse(true, null));
    }

    public Task<SlackReactionGetResponse> ReactionsGetAsync(string conversationId, string messageTs, string botToken, CancellationToken ct = default)
    {
        Calls.Add("reactions.get");
        return Task.FromResult(new SlackReactionGetResponse(true, null, new SlackReactionMessage([])));
    }

    public Task<SlackConversationsHistoryPage> ConversationsHistoryAsync(string conversationId, string? latest, string? oldest, string? cursor, string botToken, CancellationToken ct = default)
    {
        Calls.Add("conversations.history");
        return Task.FromResult(new SlackConversationsHistoryPage(true, null, [], null));
    }
}
