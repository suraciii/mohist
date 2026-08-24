using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static void MapAgentReplyRoute(RouteGroupBuilder management)
    {
        management.MapPost("/reply", async (
            HttpContext context,
            SlackReplyBody body,
            SlackOutboxStore outbox,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ConversationId))
                return ApiResults.BadRequest("conversationId is required.");
            if (!string.IsNullOrWhiteSpace(body.DispatchRef)
                && (string.IsNullOrWhiteSpace(body.ConnectionId)
                    || string.IsNullOrWhiteSpace(body.TriggeringMessageId)))
                return ApiResults.BadRequest(
                    "connectionId and triggeringMessageId are required when dispatchRef is supplied.");

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
            var text = SlackMarkdownRenderer.ToMrkdwn(SlackFinalReplyRenderer.RedactReplyText(body.Text));
            if (string.IsNullOrWhiteSpace(text) && !hasAttachment)
                return ApiResults.BadRequest("text must not be empty.");

            var result = await outbox.EnqueueAgentReplyAsync(
                projectId,
                body.ConversationId.Trim(),
                string.IsNullOrWhiteSpace(body.ThreadTs) ? null : body.ThreadTs.Trim(),
                text,
                connectionId: string.IsNullOrWhiteSpace(body.ConnectionId) ? null : body.ConnectionId.Trim(),
                triggeringMessageId: string.IsNullOrWhiteSpace(body.TriggeringMessageId)
                    ? null
                    : body.TriggeringMessageId.Trim(),
                replyDispatchRef: string.IsNullOrWhiteSpace(body.DispatchRef) ? null : body.DispatchRef.Trim(),
                imageUrl: string.IsNullOrWhiteSpace(body.ImageUrl) ? null : body.ImageUrl.Trim(),
                fileName: string.IsNullOrWhiteSpace(body.FileName) ? null : body.FileName.Trim(),
                fileContentBase64: string.IsNullOrWhiteSpace(body.FileContentBase64) ? null : body.FileContentBase64,
                ct);
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
