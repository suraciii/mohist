using System.Net.Http.Headers;
using System.Text.Json;

namespace Mohist.Server.Slack;

public interface ISlackApiClient
{
    Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default);
    Task<SlackBotInfoResponse> BotsInfoAsync(string botUserId, string botToken, CancellationToken ct = default);
    Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default);
    Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default);
    Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default);
}

public sealed class SlackApiClient(HttpClient http) : ISlackApiClient
{
    public Task<SlackAuthTestResponse> AuthTestAsync(string botToken, CancellationToken ct = default) =>
        PostAsync<SlackAuthTestResponse>("auth.test", new { }, botToken, ct);

    public Task<SlackBotInfoResponse> BotsInfoAsync(string botUserId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackBotInfoResponse>("bots.info", new { bot = botUserId }, botToken, ct);

    public Task<SlackUserInfoResponse> UsersInfoAsync(string userId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackUserInfoResponse>("users.info", new { user = userId }, botToken, ct);

    public Task<SlackConversationInfoResponse> ConversationsInfoAsync(string conversationId, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackConversationInfoResponse>("conversations.info", new { channel = conversationId }, botToken, ct);

    public Task<SlackUsersListResponse> UsersListAsync(string? cursor, string botToken, CancellationToken ct = default) =>
        PostAsync<SlackUsersListResponse>("users.list", cursor is null ? new { } : new { cursor }, botToken, ct);

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

public sealed record SlackAuthTestResponse(bool Ok, string? Error, string? TeamId, string? Team, string? UserId, string? User, string? AppId);
public sealed record SlackBotInfoResponse(bool Ok, string? Error, SlackBotInfo? Bot);
public sealed record SlackBotInfo(string? Id, string? Name, string? AppId, IReadOnlyList<string>? Scopes);
public sealed record SlackUserInfoResponse(bool Ok, string? Error, SlackUserInfo? User);
public sealed record SlackUserInfo(string? Id, string? TeamId, bool IsBot, bool Deleted, bool IsRestricted, bool IsUltraRestricted, bool IsGuest, IReadOnlyList<string>? TeamIds = null);
public sealed record SlackConversationInfoResponse(bool Ok, string? Error, SlackConversationInfo? Channel);
public sealed record SlackConversationInfo(string? Id, string? Name, string? Creator, bool IsIm, bool IsMember);
public sealed record SlackUsersListResponse(bool Ok, string? Error, IReadOnlyList<SlackUserInfo>? Members, SlackResponseMetadata? ResponseMetadata);
public sealed record SlackResponseMetadata(string? NextCursor);
