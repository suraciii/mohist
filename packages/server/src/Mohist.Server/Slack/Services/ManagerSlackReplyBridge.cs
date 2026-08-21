using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// The Manager reply capability is intentionally separate from the
/// allowlisted management bridge. It accepts only the typed payload carried by
/// <c>mo slack message send</c>, validates the execution's current origin, and
/// hands the Server-created anchor to the Manager-owned outbox route.
/// </summary>
public sealed class ManagerSlackReplyBridge : IScopedService
{
    private readonly ManagerActorAccessDecider _access;
    private readonly SlackOutboxStore _outbox;

    public ManagerSlackReplyBridge(
        ManagerActorAccessDecider access,
        SlackOutboxStore outbox)
    {
        _access = access;
        _outbox = outbox;
    }

    public async Task<ManagerSlackReplyBridgeResult> ExecuteAsync(
        SlackManagerReplyRequest request,
        ManagerExecutionCredentialContext credential,
        CancellationToken ct = default)
    {
        if (credential.Kind != ManagerExecutionLeaseKind.Reply)
            return Forbidden(
                "manager_reply_credential_required",
                "This credential cannot invoke the Manager reply action.");

        var origin = credential.Lease.Origin;
        var actor = await _access.AuthenticateAsync(origin.WorkspaceId, origin.ActorId, ct);
        if (!actor.Allowed
            || actor.Actor is null
            || !string.Equals(actor.Actor.EnrollmentId, origin.EnrollmentId, StringComparison.Ordinal))
        {
            return Forbidden(
                "manager_actor_not_authorized",
                "Manager authorization is no longer active; start a fresh turn.");
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId)
            || string.IsNullOrWhiteSpace(request.ThreadTs))
        {
            return BadRequest(
                "manager_reply_anchor_required",
                "conversationId and reply-to thread are required for a Manager reply.");
        }

        if (!string.Equals(request.ConversationId.Trim(), origin.ConversationId, StringComparison.Ordinal)
            || !string.Equals(request.ThreadTs.Trim(), origin.ThreadRootMessageId, StringComparison.Ordinal)
            || !MatchesOptional(request.WorkspaceTeamId, origin.WorkspaceId)
            || !MatchesOptional(request.ProjectId, SlackDeliveryOwnerIds.ManagerProjectId)
            || !MatchesOptional(request.OwnerKind, SlackDeliveryOwnerKinds.Manager)
            || !MatchesOptional(request.ConnectionId, origin.EnrollmentId)
            || !MatchesOptional(request.ThreadRootMessageId, origin.ThreadRootMessageId)
            || !MatchesOptional(request.TriggeringMessageId, origin.TriggeringMessageId)
            || !MatchesOptional(request.ActorId, origin.ActorId)
            || !MatchesOptional(request.EnrollmentId, origin.EnrollmentId)
            || !MatchesOptional(request.SessionId, origin.SessionId)
            || !MatchesOptional(request.DispatchRef, origin.DispatchRef))
        {
            return Conflict(
                "manager_reply_origin_mismatch",
                "The Manager reply does not match its immutable Slack origin.");
        }

        var hasAttachment = !string.IsNullOrWhiteSpace(request.ImageUrl)
            || !string.IsNullOrWhiteSpace(request.FileContentBase64);
        if (string.IsNullOrWhiteSpace(request.Text) && !hasAttachment)
            return BadRequest("manager_reply_body_required", "text, imageUrl, or a file is required.");
        if (!string.IsNullOrWhiteSpace(request.FileContentBase64)
            && string.IsNullOrWhiteSpace(request.FileName))
            return BadRequest("manager_reply_file_name_required", "fileName is required when a file is attached.");
        if (!string.IsNullOrWhiteSpace(request.FileContentBase64))
        {
            try
            {
                _ = Convert.FromBase64String(request.FileContentBase64);
            }
            catch (FormatException)
            {
                return BadRequest("manager_reply_file_invalid", "fileContentBase64 is not valid base64.");
            }
        }

        var text = SlackMarkdownRenderer.ToMrkdwn(
            SlackFinalReplyRenderer.RedactReplyText(request.Text));
        if (string.IsNullOrWhiteSpace(text) && !hasAttachment)
            return BadRequest("manager_reply_body_required", "text must not be empty.");

        var delivery = await _outbox.EnqueueManagerAgentReplyAsync(
            new SlackManagerReplyAnchor(
                new SlackMessageIdentity(origin.WorkspaceId, origin.ConversationId, origin.TriggeringMessageId),
                origin.ThreadRootMessageId,
                origin.ActorId,
                origin.EnrollmentId,
                origin.SessionId,
                origin.DispatchRef),
            text,
            imageUrl: string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim(),
            fileName: string.IsNullOrWhiteSpace(request.FileName) ? null : request.FileName.Trim(),
            fileContentBase64: string.IsNullOrWhiteSpace(request.FileContentBase64)
                ? null
                : request.FileContentBase64,
            ct);
        if (delivery.ConflictingDuplicate)
            return Conflict(
                delivery.Code ?? "manager_reply_idempotency_conflict",
                delivery.Message ?? "A different reply was already submitted for this Manager input.",
                delivery);
        if (!delivery.Accepted)
            return Conflict(
                delivery.Code ?? "manager_reply_liveness_conflict",
                delivery.Message ?? "The Manager reply could not be attached to its current liveness projection.",
                delivery);

        return ManagerSlackReplyBridgeResult.AcceptedResult(delivery);
    }

    private static bool MatchesOptional(string? supplied, string expected) =>
        string.IsNullOrWhiteSpace(supplied)
            || string.Equals(supplied.Trim(), expected, StringComparison.Ordinal);

    private static ManagerSlackReplyBridgeResult BadRequest(string code, string message) =>
        new(false, StatusCodes.Status400BadRequest, code, message);

    private static ManagerSlackReplyBridgeResult Conflict(
        string code,
        string message,
        SlackAgentReplyResult? delivery = null) =>
        new(false, StatusCodes.Status409Conflict, code, message, delivery);

    private static ManagerSlackReplyBridgeResult Forbidden(string code, string message) =>
        new(false, StatusCodes.Status403Forbidden, code, message);
}

public sealed record SlackManagerReplyRequest(
    string? ConversationId,
    string? ThreadTs,
    string? Text,
    string? ImageUrl,
    string? FileName,
    string? FileContentBase64,
    string? WorkspaceTeamId = null,
    string? ProjectId = null,
    string? OwnerKind = null,
    string? ConnectionId = null,
    string? ThreadRootMessageId = null,
    string? TriggeringMessageId = null,
    string? ActorId = null,
    string? EnrollmentId = null,
    string? SessionId = null,
    string? DispatchRef = null);

public sealed record ManagerSlackReplyBridgeResult(
    bool Accepted,
    int StatusCode,
    string Code,
    string Message,
    SlackAgentReplyResult? Delivery = null)
{
    public static ManagerSlackReplyBridgeResult AcceptedResult(SlackAgentReplyResult delivery) =>
        new(true, StatusCodes.Status200OK, "accepted", "Manager reply accepted.", delivery);
}
