using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Slack;

public interface ISlackApiClient
{
    Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default);
    Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default);
    Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default);
    Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default);
    Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default);
    Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default);
    Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default);
    Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
        string conversationId,
        string threadTs,
        string? cursor,
        string botToken,
        CancellationToken ct = default);
}

public sealed class SlackApiClient(HttpClient http) : ISlackApiClient
{
    public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) =>
        PostAsync<SlackAppsConnectionOpenResponse>("apps.connections.open", new { }, appToken, ct);

    public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) =>
        PostAsync<SlackAuthTestResponse>("auth.test", new { }, botToken, ct);

    public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackBotInfoResponse>("bots.info", new { bot = botId }, botToken, ct);

    public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) =>
        PostAsync<SlackPermissionsScopesListResponse>("apps.permissions.scopes.list", new { }, botToken, ct);

    public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackUserInfoResponse>("users.info", new { user = userId }, botToken, ct);

    public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackConversationInfoResponse>("conversations.info", new { channel = conversationId }, botToken, ct);

    public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackUsersListResponse>("users.list", cursor is null ? new { } : new { cursor }, botToken, ct);

    public Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
        string conversationId,
        string threadTs,
        string? cursor,
        string botToken,
        CancellationToken ct = default) =>
        PostAsync<SlackConversationsRepliesPage>(
            "conversations.replies",
            new
            {
                channel = conversationId,
                ts = threadTs,
                cursor,
                limit = 200,
                inclusive = false,
            },
            botToken,
            ct);

    private async Task<T> PostAsync<T>(string method, object body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"Slack returned an empty response for {method}.");
    }
}

public sealed record SlackAuthTestResponse(
    bool Ok,
    string? Error,
    [property: JsonPropertyName("team_id")] string? TeamId,
    string? Team,
    [property: JsonPropertyName("user_id")] string? UserId,
    string? User,
    [property: JsonPropertyName("bot_id")] string? BotId,
    [property: JsonPropertyName("app_id")] string? AppId);
public sealed record SlackAppsConnectionOpenResponse(bool Ok, string? Error, string? Url);
public sealed record SlackBotInfoResponse(bool Ok, string? Error, SlackBotInfo? Bot);
public sealed record SlackBotInfo(
    string? Id,
    string? Name,
    [property: JsonPropertyName("app_id")] string? AppId,
    SlackBotIcons? Icons = null)
{
    [JsonIgnore]
    public string? IconUrl => Icons?.HighestResolutionUrl;
}

public sealed record SlackBotIcons(
    [property: JsonPropertyName("image_36")] string? Image36 = null,
    [property: JsonPropertyName("image_48")] string? Image48 = null,
    [property: JsonPropertyName("image_72")] string? Image72 = null,
    [property: JsonPropertyName("image_192")] string? Image192 = null,
    [property: JsonPropertyName("image_512")] string? Image512 = null,
    [property: JsonPropertyName("image_1024")] string? Image1024 = null)
{
    [JsonIgnore]
    public string? HighestResolutionUrl =>
        Image1024 ?? Image512 ?? Image192 ?? Image72 ?? Image48 ?? Image36;
}
public sealed record SlackPermissionsScopesListResponse(bool Ok, string? Error, IReadOnlyDictionary<string, IReadOnlyList<string>>? Scopes);
public sealed record SlackUserInfoResponse(bool Ok, string? Error, SlackUserInfo? User);
public sealed record SlackUserInfo(
    string? Id,
    string? TeamId,
    bool IsBot,
    bool Deleted,
    bool IsRestricted,
    bool IsUltraRestricted,
    bool IsGuest,
    IReadOnlyList<string>? TeamIds = null,
    string? DisplayName = null,
    string? RealName = null,
    string? Email = null,
    string? AvatarUrl = null);
public sealed record SlackConversationInfoResponse(bool Ok, string? Error, SlackConversationInfo? Channel);
public sealed record SlackConversationInfo(string? Id, string? Name, string? Creator, bool IsIm, bool IsMember);
public sealed record SlackUsersListResponse(bool Ok, string? Error, IReadOnlyList<SlackUserInfo>? Members, SlackResponseMetadata? ResponseMetadata);
public sealed record SlackResponseMetadata(string? NextCursor);
public sealed record SlackConversationsRepliesPage(
    bool Ok,
    string? Error,
    [property: JsonPropertyName("messages")] IReadOnlyList<SlackConversationMessage>? Messages,
    [property: JsonPropertyName("response_metadata")] SlackResponseMetadata? ResponseMetadata);
public sealed record SlackConversationMessage(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("ts")] string? Ts,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("bot_id")] string? BotId,
    [property: JsonPropertyName("thread_ts")] string? ThreadTs,
    [property: JsonPropertyName("parent_user_id")] string? ParentUserId);
