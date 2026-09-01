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
        payload.ReplyParts is { Count: > 0 } parts
            ? parts.Contains(text, StringComparer.Ordinal)
            : IsSameOrLatestLegacyReplyPart(
                !string.IsNullOrWhiteSpace(payload.FallbackText) ? payload.FallbackText : payload.Text,
                text);

    private static bool IsSameOrLatestLegacyReplyPart(string? combined, string text) =>
        string.Equals(combined, text, StringComparison.Ordinal)
        || combined?.EndsWith("\n\n" + text, StringComparison.Ordinal) == true;

    private static async Task<SlackOutboxRow?> FindReplyTerminalRowAsync(
        MohistDbContext db,
        string projectId,
        string conversationId,
        string? threadTs,
        string? connectionId,
        string? dispatchRef,
        CancellationToken ct)
    {
        var query = db.SlackOutboxRows.Where(row =>
            row.ProjectId == projectId
            && row.ConversationId == conversationId
            && row.Kind == SlackOutboxKinds.TerminalResult
            && (row.State == SlackOutboxStates.Pending
                || dispatchRef != null && (row.State == SlackOutboxStates.Claimed
                    || row.State == SlackOutboxStates.DeliveryUncertain
                    || row.State == SlackOutboxStates.Delivered)));
        if (!string.IsNullOrWhiteSpace(dispatchRef))
            query = query.Where(row => row.DispatchRef == dispatchRef);
        if (!string.IsNullOrWhiteSpace(threadTs))
            query = query.Where(row => row.ThreadTs == threadTs);
        if (!string.IsNullOrWhiteSpace(connectionId))
            query = query.Where(row => row.ConnectionId == connectionId);
        return await query.OrderBy(row => row.Id).FirstOrDefaultAsync(ct);
    }

    private static async Task<(string ConnectionId, string WorkspaceTeamId)?> ResolveReplyConnectionAsync(
        MohistDbContext db,
        string projectId,
        string conversationId,
        string? threadTs,
        string? connectionId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(threadTs))
        {
            var thread = await db.SlackThreadSessionMappings
                .Where(row => row.ProjectId == projectId
                    && row.ConversationId == conversationId
                    && row.ThreadTs == threadTs
                    && (connectionId == null || row.ConnectionId == connectionId))
                .Select(row => new { row.ConnectionId, row.WorkspaceTeamId })
                .FirstOrDefaultAsync(ct);
            if (thread is not null)
                return (thread.ConnectionId, thread.WorkspaceTeamId);
        }

        var dm = await db.SlackDmSessionMappings
            .Where(row => row.ProjectId == projectId
                && row.DmConversationId == conversationId
                && (connectionId == null || row.ConnectionId == connectionId))
            .Select(row => new { row.ConnectionId, row.WorkspaceTeamId })
            .FirstOrDefaultAsync(ct);
        if (dm is not null)
            return (dm.ConnectionId, dm.WorkspaceTeamId);

        if (string.IsNullOrWhiteSpace(threadTs))
        {
            var anyThread = await db.SlackThreadSessionMappings
                .Where(row => row.ProjectId == projectId
                    && row.ConversationId == conversationId
                    && (connectionId == null || row.ConnectionId == connectionId))
                .Select(row => new { row.ConnectionId, row.WorkspaceTeamId })
                .FirstOrDefaultAsync(ct);
            if (anyThread is not null)
                return (anyThread.ConnectionId, anyThread.WorkspaceTeamId);
        }

        return null;
    }

    private async Task<SlackOutboxRow> MergeReplyTerminalAsync(
        MohistDbContext db,
        SlackOutboxRow terminal,
        string redactedText,
        bool idempotentRetry,
        CancellationToken ct)
    {
        var previous = SlackDeliveryPayload.Parse(terminal.PayloadJson);
        var previousText = !string.IsNullOrWhiteSpace(previous.FallbackText)
            ? previous.FallbackText
            : previous.Text;
        var replyParts = previous.ReplyParts is { Count: > 0 }
            ? previous.ReplyParts
            : string.IsNullOrWhiteSpace(previousText) ? [] : [previousText];
        if (idempotentRetry && IsAgentReplyPart(previous, redactedText))
            return terminal;
        if (terminal.State == SlackOutboxStates.Delivered && previous.ProviderMessageIdentity is null)
            return terminal;
        var combined = string.IsNullOrWhiteSpace(previousText)
            ? redactedText
            : previousText + "\n\n" + redactedText;
        var segments = SlackFinalReplyRenderer.SegmentReplyText(combined);
        terminal.PayloadJson = JsonSerializer.Serialize(previous with
        {
            Operation = previous.ProviderMessageIdentity is null
                ? previous.Operation
                : SlackDeliveryOperations.ChatUpdate,
            Text = combined,
            FallbackText = combined,
            Segments = segments.Count > 1 ? segments : null,
            ReplyParts = replyParts.Append(redactedText).ToArray(),
        });
        terminal.State = SlackOutboxStates.Pending;
        terminal.NextAttemptAt = _timeProvider.GetUtcNow();
        terminal.ClaimedAt = null;
        terminal.ClaimedByAdapterId = null;
        terminal.DeliveryUncertainAt = null;
        terminal.DeliveredAt = null;
        terminal.LastError = null;
        terminal.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return terminal;
    }
}
