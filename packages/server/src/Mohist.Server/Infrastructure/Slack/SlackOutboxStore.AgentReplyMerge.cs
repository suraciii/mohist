using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;

namespace Mohist.Server.Infrastructure.Slack;

public sealed partial class SlackOutboxStore
{
    private async Task<SlackOutboxRow?> FindCanonicalAgentReplyAsync(
        string connectionId,
        string dispatchRef,
        CancellationToken ct)
    {
        await using var fresh = await _dbFactory.CreateDbContextAsync(ct);
        return await fresh.SlackOutboxRows.AsNoTracking().FirstOrDefaultAsync(row =>
            row.OwnerKind == SlackDeliveryOwnerKinds.Connection
            && row.ConnectionId == connectionId
            && row.Kind == SlackOutboxKinds.TerminalResult
            && row.DispatchRef == dispatchRef, ct);
    }

    private static SlackAgentReplyResult ConflictingAgentReply(
        string connectionId,
        string? deliveryId = null,
        string? dispatchRef = null,
        string? message = null) =>
        new(
            Accepted: false,
            ConnectionId: connectionId,
            DeliveryId: deliveryId,
            DispatchRef: dispatchRef,
            MergedIntoExisting: true,
            ConflictingDuplicate: true,
            Code: "slack_reply_idempotency_conflict",
            Message: message ?? "A different Slack reply already exists for this turn.");

    private static bool IsAgentReplyPart(SlackDeliveryPayload payload, string text) =>
        payload.ReplyParts?.Contains(text, StringComparer.Ordinal) == true;

    private static async Task<SlackOutboxRow?> FindReplyTerminalRowAsync(
        MohistDbContext db,
        string projectId,
        string conversationId,
        string threadTs,
        string connectionId,
        string dispatchRef,
        CancellationToken ct)
    {
        return await db.SlackOutboxRows.FirstOrDefaultAsync(row =>
            row.ProjectId == projectId
            && row.ConversationId == conversationId
            && row.ThreadTs == threadTs
            && row.ConnectionId == connectionId
            && row.DispatchRef == dispatchRef
            && row.Kind == SlackOutboxKinds.TerminalResult
            && (row.State == SlackOutboxStates.Pending
                || row.State == SlackOutboxStates.Claimed
                    || row.State == SlackOutboxStates.DeliveryUncertain
                    || row.State == SlackOutboxStates.Delivered), ct);
    }

    private static async Task<(string ConnectionId, string WorkspaceTeamId)?> ResolveReplyConnectionAsync(
        MohistDbContext db,
        string projectId,
        string connectionId,
        CancellationToken ct)
    {
        var connection = await db.AgentConnections
            .Where(row => row.ProjectId == projectId
                && row.Id == connectionId
                && row.DeletedAt == null)
            .Select(row => new { ConnectionId = row.Id, row.WorkspaceTeamId })
            .FirstOrDefaultAsync(ct);
        return connection is null
            ? null
            : (connection.ConnectionId, connection.WorkspaceTeamId);
    }

    private async Task<bool> TryMergeReplyTerminalAsync(
        MohistDbContext db,
        SlackOutboxRow terminal,
        string redactedText,
        CancellationToken ct)
    {
        var previous = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        var previousText = !string.IsNullOrWhiteSpace(previous.FallbackText)
            ? previous.FallbackText
            : previous.Text;
        var replyParts = previous.ReplyParts is { Count: > 0 }
            ? previous.ReplyParts
            : string.IsNullOrWhiteSpace(previousText) ? [] : [previousText];
        if (terminal.State == SlackOutboxStates.Delivered && previous.ProviderMessageIdentity is null)
            return true;
        var combined = string.IsNullOrWhiteSpace(previousText)
            ? redactedText
            : previousText + "\n\n" + redactedText;
        var segments = SlackFinalReplyRenderer.SegmentReplyText(combined);
        var payloadJson = JsonSerializer.Serialize(previous with
        {
            Operation = previous.ProviderMessageIdentity is null
                ? previous.Operation
                : SlackDeliveryOperations.ChatUpdate,
            Text = combined,
            FallbackText = combined,
            Segments = segments.Count > 1 ? segments : null,
            ReplyParts = replyParts.Append(redactedText).ToArray(),
        });
        var now = _timeProvider.GetUtcNow();
        var changed = await db.SlackOutboxRows
            .Where(row => row.Id == terminal.Id
                && row.State == terminal.State
                && row.PayloadJson == terminal.PayloadJson
                && row.UpdatedAt == terminal.UpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.PayloadJson, payloadJson)
                .SetProperty(row => row.State, SlackOutboxStates.Pending)
                .SetProperty(row => row.NextAttemptAt, now)
                .SetProperty(row => row.ClaimedAt, (DateTimeOffset?)null)
                .SetProperty(row => row.ClaimedByAdapterId, (string?)null)
                .SetProperty(row => row.DeliveryUncertainAt, (DateTimeOffset?)null)
                .SetProperty(row => row.DeliveredAt, (DateTimeOffset?)null)
                .SetProperty(row => row.LastError, (string?)null)
                .SetProperty(row => row.UpdatedAt, now), ct);
        return changed == 1;
    }
}
