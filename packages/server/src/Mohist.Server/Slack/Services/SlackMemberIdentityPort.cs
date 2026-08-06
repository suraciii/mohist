namespace Mohist.Server.Slack.Services;

/// <summary>
/// Data-plane identity port: provider-confirmed Slack facts the access
/// decider needs for the live member gate. Queries are made with the
/// Connection's verified Agent App Bot token (resolved through the runtime
/// lease seam), never with the Configuration credential. Results carry only
/// non-secret facts plus an error class; no token ever appears in a result.
/// </summary>
public interface ISlackMemberIdentityPort
{
    /// <summary>
    /// <c>users.info</c>: the stable identity of one Slack user plus their
    /// current membership flags. <see cref="SlackMemberIdentityResult.Confirmed"/>
    /// is true only when Slack returned a parseable user object with the
    /// requested stable id and every required membership flag.
    /// </summary>
    Task<SlackMemberIdentityResult> LookupMemberAsync(
        SlackMemberIdentityRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// <c>conversations.info</c>: whether the authenticated Bot is a member
    /// of the conversation. <see cref="SlackConversationMembershipResult.Confirmed"/>
    /// is true only when Slack returned a parseable channel object with the
    /// <c>is_member</c> flag.
    /// </summary>
    Task<SlackConversationMembershipResult> LookupConversationAsync(
        SlackConversationMembershipRequest request,
        CancellationToken ct = default);
}

public sealed record SlackMemberIdentityRequest(string BotToken, string SlackUserId);

public sealed record SlackMemberIdentityResult(
    bool Confirmed,
    string? UserId = null,
    string? TeamId = null,
    bool Deleted = false,
    bool IsBot = false,
    bool IsAppUser = false,
    bool IsRestricted = false,
    bool IsUltraRestricted = false,
    bool IsStranger = false,
    string? ErrorClass = null);

public sealed record SlackConversationMembershipRequest(string BotToken, string ConversationId);

public sealed record SlackConversationMembershipResult(
    bool Confirmed,
    bool IsMember = false,
    string? ErrorClass = null);

public sealed class FakeSlackMemberIdentityPort : ISlackMemberIdentityPort
{
    public List<SlackMemberIdentityRequest> MemberRequests { get; } = [];
    public List<SlackConversationMembershipRequest> ConversationRequests { get; } = [];
    public SlackMemberIdentityResult MemberResult { get; set; } = new(false);
    public SlackConversationMembershipResult ConversationResult { get; set; } = new(false);

    public Task<SlackMemberIdentityResult> LookupMemberAsync(
        SlackMemberIdentityRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SlackUserId);
        MemberRequests.Add(request);
        return Task.FromResult(MemberResult);
    }

    public Task<SlackConversationMembershipResult> LookupConversationAsync(
        SlackConversationMembershipRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ConversationRequests.Add(request);
        return Task.FromResult(ConversationResult);
    }
}
