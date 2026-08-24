using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;

namespace Mohist.Server.Infrastructure.Slack;

public sealed partial class SlackOutboxStore
{
    private static async Task<SlackOutboxRow?> FindReplyProgressRowAsync(
        MohistDbContext db,
        string projectId,
        string conversationId,
        string? threadTs,
        string? connectionId,
        string? triggeringMessageId,
        CancellationToken ct)
    {
        var query = db.SlackOutboxRows.Where(row =>
            row.ProjectId == projectId
            && row.ConversationId == conversationId
            && row.Kind == SlackOutboxKinds.ReplaceableProgress
            && row.State == SlackOutboxStates.Pending);
        if (string.IsNullOrWhiteSpace(triggeringMessageId)
            && !string.IsNullOrWhiteSpace(threadTs))
            query = query.Where(row => row.ThreadTs == threadTs);
        if (!string.IsNullOrWhiteSpace(connectionId))
            query = query.Where(row => row.ConnectionId == connectionId);
        var ordered = query.OrderBy(row => row.Id);
        if (string.IsNullOrWhiteSpace(triggeringMessageId))
            return await ordered.FirstOrDefaultAsync(ct);
        var candidates = await ordered.ToListAsync(ct);
        return candidates.FirstOrDefault(row =>
            SlackDeliveryPayload.Parse(row.PayloadJson).StatusDispatchRef
                == SlackStatusProjection.DispatchRef(
                    new SlackMessageIdentity(
                        row.WorkspaceTeamId,
                        conversationId,
                        triggeringMessageId),
                    "status"));
    }

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
            && row.State == SlackOutboxStates.Pending);
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
        if (idempotentRetry && string.Equals(previousText, redactedText, StringComparison.Ordinal))
            return terminal;
        var combined = string.IsNullOrWhiteSpace(previousText)
            ? redactedText
            : previousText + "\n\n" + redactedText;
        var segments = SlackFinalReplyRenderer.SegmentReplyText(combined);
        terminal.PayloadJson = JsonSerializer.Serialize(previous with
        {
            Text = combined,
            Segments = segments.Count > 1 ? segments : null,
        });
        terminal.State = SlackOutboxStates.Pending;
        terminal.NextAttemptAt = _timeProvider.GetUtcNow();
        terminal.ClaimedAt = null;
        terminal.ClaimedByAdapterId = null;
        terminal.DeliveryUncertainAt = null;
        terminal.LastError = null;
        terminal.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return terminal;
    }
}
