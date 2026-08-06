using System.Text.Json;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Production <see cref="ISlackMemberIdentityPort"/>: queries
/// <c>users.info</c> and <c>conversations.info</c> with the caller-supplied
/// Bot token through <see cref="SlackApiTransport"/>. Fail-closed by
/// construction: every non-OK outcome (Slack rejection, unparseable body,
/// transport error) and every missing required field yields
/// <c>Confirmed: false</c> with an error class, never partial facts.
/// </summary>
public sealed class SlackMemberIdentityPortAdapter(SlackApiTransport transport) : ISlackMemberIdentityPort
{
    public const string UsersInfoEndpoint = "users.info";
    public const string ConversationsInfoEndpoint = "conversations.info";

    public async Task<SlackMemberIdentityResult> LookupMemberAsync(
        SlackMemberIdentityRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SlackUserId);

        var response = await transport.PostFormAsync(
            UsersInfoEndpoint,
            new Dictionary<string, string> { ["user"] = request.SlackUserId },
            request.BotToken,
            ct).ConfigureAwait(false);

        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => ParseMember(response.Body),
            SlackApiCallOutcome.Rejected => new(false, ErrorClass: response.Error ?? "users_info_rejected"),
            SlackApiCallOutcome.Unparseable => new(false, ErrorClass: "unparseable_response"),
            _ => new(false, ErrorClass: "transport_error"),
        };
    }

    public async Task<SlackConversationMembershipResult> LookupConversationAsync(
        SlackConversationMembershipRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);

        var response = await transport.PostFormAsync(
            ConversationsInfoEndpoint,
            new Dictionary<string, string> { ["channel"] = request.ConversationId },
            request.BotToken,
            ct).ConfigureAwait(false);

        return response.Outcome switch
        {
            SlackApiCallOutcome.Ok => ParseConversation(response.Body),
            SlackApiCallOutcome.Rejected => new(false, ErrorClass: response.Error ?? "conversations_info_rejected"),
            SlackApiCallOutcome.Unparseable => new(false, ErrorClass: "unparseable_response"),
            _ => new(false, ErrorClass: "transport_error"),
        };
    }

    private static SlackMemberIdentityResult ParseMember(JsonDocument? body)
    {
        if (body is null)
            return new(false, ErrorClass: "unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("user", out var user)
                || user.ValueKind != JsonValueKind.Object)
            {
                return new(false, ErrorClass: "invalid_identity_response");
            }

            var userId = ReadString(user, "id");
            var teamId = ReadString(user, "team_id");
            if (userId is null || teamId is null)
                return new(false, ErrorClass: "invalid_identity_response");

            // Every membership flag is required: a missing flag is an
            // unverifiable identity, never an implicit "eligible".
            if (!TryReadBool(user, "deleted", out var deleted)
                || !TryReadBool(user, "is_bot", out var isBot)
                || !TryReadBool(user, "is_app_user", out var isAppUser)
                || !TryReadBool(user, "is_restricted", out var isRestricted)
                || !TryReadBool(user, "is_ultra_restricted", out var isUltraRestricted))
            {
                return new(false, ErrorClass: "invalid_identity_response");
            }

            return new SlackMemberIdentityResult(
                Confirmed: true,
                UserId: userId,
                TeamId: teamId,
                Deleted: deleted,
                IsBot: isBot,
                IsAppUser: isAppUser,
                IsRestricted: isRestricted,
                IsUltraRestricted: isUltraRestricted,
                IsStranger: ReadBool(user, "is_stranger"));
        }
    }

    private static SlackConversationMembershipResult ParseConversation(JsonDocument? body)
    {
        if (body is null)
            return new(false, ErrorClass: "unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("channel", out var channel)
                || channel.ValueKind != JsonValueKind.Object
                || !TryReadBool(channel, "is_member", out var isMember))
            {
                return new(false, ErrorClass: "invalid_conversation_response");
            }

            return new SlackConversationMembershipResult(Confirmed: true, IsMember: isMember);
        }
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        if (element.TryGetProperty(propertyName, out var candidate)
            && (candidate.ValueKind == JsonValueKind.True || candidate.ValueKind == JsonValueKind.False))
        {
            value = candidate.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var candidate) && candidate.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var candidate) && candidate.ValueKind == JsonValueKind.String
            ? candidate.GetString()
            : null;
}
