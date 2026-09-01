using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static void MapAgentReplyRoute(RouteGroupBuilder management)
    {
        management.MapPost("/reply", async (
            HttpContext context,
            SlackReplyBody body,
            SlackOutboxStore outbox,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ConversationId))
                return ApiResults.BadRequest("conversationId is required.");
            if (string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.ConnectionId)
                || string.IsNullOrWhiteSpace(body.ThreadTs)
                || string.IsNullOrWhiteSpace(body.TriggeringMessageId)
                || string.IsNullOrWhiteSpace(body.SessionId)
                || string.IsNullOrWhiteSpace(body.DispatchRef))
                return ApiResults.BadRequest(
                    "workspaceTeamId, connectionId, threadTs, triggeringMessageId, sessionId, and dispatchRef are required.",
                    "slack_reply_anchor_required");

            var hasAttachment = !string.IsNullOrWhiteSpace(body.ImageUrl)
                || !string.IsNullOrWhiteSpace(body.FileContentBase64);
            if (string.IsNullOrWhiteSpace(body.Text) && !hasAttachment)
                return ApiResults.BadRequest("text, imageUrl, or a file is required.");
            if (!string.IsNullOrWhiteSpace(body.FileContentBase64)
                && string.IsNullOrWhiteSpace(body.FileName))
                return ApiResults.BadRequest("fileName is required when a file is attached.");
            if (!string.IsNullOrWhiteSpace(body.FileContentBase64))
            {
                try
                {
                    _ = Convert.FromBase64String(body.FileContentBase64);
                }
                catch (FormatException)
                {
                    return ApiResults.BadRequest("fileContentBase64 is not valid base64.");
                }
            }

            var projectId = context.GetResolvedProject().Id;
            var anchor = new SlackReplyAnchorValidationRequest(
                projectId,
                body.WorkspaceTeamId.Trim(),
                body.ConversationId.Trim(),
                body.ThreadTs.Trim(),
                body.TriggeringMessageId.Trim(),
                body.ConnectionId.Trim(),
                body.SessionId.Trim(),
                body.DispatchRef.Trim());
            var validation = await grains.GetGrain<IAgentSessionGrain>(anchor.SessionId)
                .ValidateSlackReplyAnchorAsync(anchor);
            if (!validation.Valid)
            {
                return ApiResults.Fail(
                    "The Slack reply anchor does not match the active Session turn.",
                    409,
                    "slack_reply_anchor_mismatch");
            }
            var idempotentRetryOnly = !validation.TurnActive;
            var text = SlackMarkdownRenderer.ToMrkdwn(SlackFinalReplyRenderer.RedactReplyText(body.Text));
            if (string.IsNullOrWhiteSpace(text) && !hasAttachment)
                return ApiResults.BadRequest("text must not be empty.");

            var result = await outbox.EnqueueAgentReplyAsync(
                projectId,
                body.ConversationId.Trim(),
                string.IsNullOrWhiteSpace(body.ThreadTs) ? null : body.ThreadTs.Trim(),
                text,
                connectionId: string.IsNullOrWhiteSpace(body.ConnectionId) ? null : body.ConnectionId.Trim(),
                replyDispatchRef: string.IsNullOrWhiteSpace(body.DispatchRef) ? null : body.DispatchRef.Trim(),
                imageUrl: string.IsNullOrWhiteSpace(body.ImageUrl) ? null : body.ImageUrl.Trim(),
                fileName: string.IsNullOrWhiteSpace(body.FileName) ? null : body.FileName.Trim(),
                fileContentBase64: string.IsNullOrWhiteSpace(body.FileContentBase64) ? null : body.FileContentBase64,
                idempotentRetryOnly: idempotentRetryOnly,
                ct);
            if (result.ConflictingDuplicate)
                return ApiResults.Fail(
                    result.Message ?? "A different Slack reply already exists for this turn.",
                    409,
                    result.Code ?? "slack_reply_idempotency_conflict");
            if (!result.Accepted)
                return ApiResults.Fail(
                    "No active Slack conversation matches this conversation and reply target.",
                    404,
                    "slack_reply_no_conversation");
            return ApiResults.Ok(new
            {
                accepted = true,
                connectionId = result.ConnectionId,
                deliveryId = result.DeliveryId,
                dispatchRef = result.DispatchRef,
                merged = result.MergedIntoExisting,
            });
        });
    }
}
