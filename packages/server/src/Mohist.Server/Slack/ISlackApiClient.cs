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
    Task<SlackFileContent> OpenFileContentAsync(string fileId, string botToken, CancellationToken ct = default);
    Task<SlackConversationsRepliesPage> ConversationsRepliesAsync(
        string conversationId,
        string threadTs,
        string? cursor,
        string botToken,
        CancellationToken ct = default);
    Task<SlackChatPostMessageResponse> ChatPostMessageAsync(string conversationId, string text, string? threadTs, string? clientMessageId, string botToken, CancellationToken ct = default) =>
        Task.FromException<SlackChatPostMessageResponse>(new NotSupportedException("Slack chat.postMessage is not implemented by this client."));
    Task<SlackChatUpdateResponse> ChatUpdateAsync(string conversationId, string messageTs, string text, string botToken, CancellationToken ct = default) =>
        Task.FromException<SlackChatUpdateResponse>(new NotSupportedException("Slack chat.update is not implemented by this client."));
    Task<SlackReactionResponse> ReactionsAddAsync(string conversationId, string reaction, string messageTs, string botToken, CancellationToken ct = default) =>
        Task.FromException<SlackReactionResponse>(new NotSupportedException("Slack reactions.add is not implemented by this client."));
    Task<SlackReactionResponse> ReactionsRemoveAsync(string conversationId, string reaction, string messageTs, string botToken, CancellationToken ct = default) =>
        Task.FromException<SlackReactionResponse>(new NotSupportedException("Slack reactions.remove is not implemented by this client."));
    Task<SlackReactionGetResponse> ReactionsGetAsync(string conversationId, string messageTs, string botToken, CancellationToken ct = default) =>
        Task.FromException<SlackReactionGetResponse>(new NotSupportedException("Slack reactions.get is not implemented by this client."));
    Task<SlackConversationsHistoryPage> ConversationsHistoryAsync(string conversationId, string? latest, string? oldest, string? cursor, string botToken, CancellationToken ct = default) =>
        Task.FromException<SlackConversationsHistoryPage>(new NotSupportedException("Slack conversations.history is not implemented by this client."));
}

public sealed class SlackApiClient(HttpClient http) : ISlackApiClient
{
    public Task<SlackAppsConnectionOpenResponse> AppsConnectionsOpenAsync(string appToken, CancellationToken ct = default) =>
        PostAsync<SlackAppsConnectionOpenResponse>("apps.connections.open", new { }, appToken, ct);

    public async Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default)
    {
        var (response, scopesHeader) = await PostWithHeadersAsync<SlackAuthTestResponse>("auth.test", new { }, botToken, ct).ConfigureAwait(false);
        return response with { GrantedScopes = ParseScopesHeader(scopesHeader) };
    }

    public Task<SlackBotInfoResponse> BotsInfoAsync(string botId, string botToken, CancellationToken ct = default) =>
        PostFormAsync<SlackBotInfoResponse>("bots.info", new Dictionary<string, string> { ["bot"] = botId }, botToken, ct);

    public Task<SlackPermissionsScopesListResponse> PermissionsScopesListAsync(string botToken, CancellationToken ct = default) =>
        PostAsync<SlackPermissionsScopesListResponse>("apps.permissions.scopes.list", new { }, botToken, ct);

    public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) =>
        PostFormAsync<SlackUserInfoResponse>("users.info", new Dictionary<string, string> { ["user"] = userId }, botToken, ct);

    public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackConversationInfoResponse>("conversations.info", new { channel = conversationId }, botToken, ct);

    public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackUsersListResponse>("users.list", cursor is null ? new { } : new { cursor }, botToken, ct);

    public async Task<SlackFileContent> OpenFileContentAsync(string fileId, string botToken, CancellationToken ct = default)
    {
        try
        {
            var info = await PostAsync<SlackFileInfoResponse>("files.info", new { file = fileId }, botToken, ct).ConfigureAwait(false);
            var file = info.Ok ? info.File : null;
            if (file is null
                || string.IsNullOrWhiteSpace(file.Name)
                || string.IsNullOrWhiteSpace(file.Mimetype)
                || string.IsNullOrWhiteSpace(file.UrlPrivate)
                || file.Size < 0)
                throw new SlackFileNotReadableException(fileId);

            var response = await GetAsync(file.UrlPrivate, botToken, ct).ConfigureAwait(false);
            return new SlackFileContent(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                file.Name,
                file.Mimetype,
                file.Size,
                response);
        }
        catch (SlackFileNotReadableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new SlackFileNotReadableException(fileId, exception);
        }
    }

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

    public Task<SlackChatPostMessageResponse> ChatPostMessageAsync(string conversationId, string text, string? threadTs, string? clientMessageId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackChatPostMessageResponse>("chat.postMessage", new { channel = conversationId, text, thread_ts = threadTs, client_msg_id = clientMessageId }, botToken, ct);

    public Task<SlackChatUpdateResponse> ChatUpdateAsync(string conversationId, string messageTs, string text, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackChatUpdateResponse>("chat.update", new { channel = conversationId, ts = messageTs, text }, botToken, ct);

    public Task<SlackReactionResponse> ReactionsAddAsync(string conversationId, string reaction, string messageTs, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackReactionResponse>("reactions.add", new { channel = conversationId, name = reaction, timestamp = messageTs }, botToken, ct);

    public Task<SlackReactionResponse> ReactionsRemoveAsync(string conversationId, string reaction, string messageTs, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackReactionResponse>("reactions.remove", new { channel = conversationId, name = reaction, timestamp = messageTs }, botToken, ct);

    public Task<SlackReactionGetResponse> ReactionsGetAsync(string conversationId, string messageTs, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackReactionGetResponse>("reactions.get", new { channel = conversationId, timestamp = messageTs, full = true }, botToken, ct);

    public Task<SlackConversationsHistoryPage> ConversationsHistoryAsync(string conversationId, string? latest, string? oldest, string? cursor, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackConversationsHistoryPage>("conversations.history", new { channel = conversationId, latest, oldest, cursor, limit = 200, inclusive = true }, botToken, ct);

    private async Task<HttpResponseMessage> GetAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

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

    private async Task<(T Result, string? ScopesHeader)> PostWithHeadersAsync<T>(string method, object body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Slack returned an empty response for {method}.");
        var scopesHeader = response.Headers.TryGetValues("x-oauth-scopes", out var values)
            ? string.Join(',', values)
            : null;
        return (result, scopesHeader);
    }

    private static IReadOnlySet<string>? ParseScopesHeader(string? value)
    {
        if (value is null)
            return null;

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<T> PostFormAsync<T>(string method, IReadOnlyDictionary<string, string> values, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = new FormUrlEncodedContent(values),
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
    [property: JsonPropertyName("app_id")] string? AppId)
{
    [JsonIgnore]
    public IReadOnlySet<string>? GrantedScopes { get; init; }
}
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
    [property: JsonPropertyName("team_id")] string? TeamId,
    [property: JsonPropertyName("is_bot")] bool IsBot,
    bool Deleted,
    [property: JsonPropertyName("is_restricted")] bool IsRestricted,
    [property: JsonPropertyName("is_ultra_restricted")] bool IsUltraRestricted,
    [property: JsonPropertyName("is_guest")] bool IsGuest,
    IReadOnlyList<string>? TeamIds = null,
    string? DisplayName = null,
    string? RealName = null,
    string? Email = null,
    string? AvatarUrl = null);
public sealed record SlackConversationInfoResponse(bool Ok, string? Error, SlackConversationInfo? Channel);
public sealed record SlackConversationInfo(string? Id, string? Name, string? Creator, bool IsIm, bool IsMember);
public sealed record SlackUsersListResponse(bool Ok, string? Error, IReadOnlyList<SlackUserInfo>? Members, SlackResponseMetadata? ResponseMetadata);
public sealed record SlackFileInfoResponse(bool Ok, string? Error, SlackFileInfo? File);
public sealed record SlackFileInfo(
    string? Id,
    string? Name,
    string? Mimetype,
    long Size,
    [property: JsonPropertyName("url_private")] string? UrlPrivate);
public sealed class SlackFileContent(Stream stream, string fileName, string contentType, long size, IDisposable response) : IDisposable
{
    public Stream Stream { get; } = stream;
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public long Size { get; } = size;

    public void Dispose()
    {
        Stream.Dispose();
        response.Dispose();
    }
}
public sealed class SlackFileNotReadableException : Exception
{
    public SlackFileNotReadableException(string fileId)
        : base($"Slack file '{fileId}' is not readable.")
    {
    }

    public SlackFileNotReadableException(string fileId, Exception innerException)
        : base($"Slack file '{fileId}' is not readable.", innerException)
    {
    }
}
public sealed record SlackResponseMetadata(string? NextCursor);
public sealed record SlackConversationsRepliesPage(
    bool Ok,
    string? Error,
    [property: JsonPropertyName("messages")] IReadOnlyList<SlackConversationMessage>? Messages,
    [property: JsonPropertyName("response_metadata")] SlackResponseMetadata? ResponseMetadata);
public sealed record SlackChatPostMessageResponse(
    bool Ok,
    string? Error,
    [property: JsonPropertyName("ts")] string? Ts,
    [property: JsonPropertyName("message")] SlackConversationMessage? Message);
public sealed record SlackChatUpdateResponse(
    bool Ok,
    string? Error,
    [property: JsonPropertyName("ts")] string? Ts,
    [property: JsonPropertyName("message")] SlackConversationMessage? Message);
public sealed record SlackReactionResponse(bool Ok, string? Error);
public sealed record SlackReactionGetResponse(bool Ok, string? Error, SlackReactionMessage? Message);
public sealed record SlackReactionMessage([property: JsonPropertyName("reactions")] IReadOnlyList<SlackReaction>? Reactions);
public sealed record SlackReaction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("users")] IReadOnlyList<string>? Users,
    [property: JsonPropertyName("count")] int Count);
public sealed record SlackConversationsHistoryPage(
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
    [property: JsonPropertyName("parent_user_id")] string? ParentUserId,
    [property: JsonPropertyName("client_msg_id")] string? ClientMessageId = null);
