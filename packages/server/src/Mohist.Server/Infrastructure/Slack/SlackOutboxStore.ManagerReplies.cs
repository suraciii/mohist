using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Slack;

public sealed partial class SlackOutboxStore
{
    /// <summary>
    /// Enqueues the reply action for one Manager execution. Manager rows are
    /// selected by the complete immutable origin; a conversation lookup is
    /// deliberately not used. The reply is independent from liveness rows.
    /// Repeated sends return the existing row without appending text or
    /// creating another lifecycle.
    /// </summary>
    public async Task<SlackAgentReplyResult> EnqueueManagerAgentReplyAsync(
        SlackManagerReplyAnchor anchor,
        string redactedText,
        string? imageUrl = null,
        string? fileName = null,
        string? fileContentBase64 = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Source.WorkspaceTeamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Source.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Source.MessageTs);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.ThreadRootMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.EnrollmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.DispatchRef);
        if (string.IsNullOrWhiteSpace(redactedText)
            && imageUrl is null && fileName is null && fileContentBase64 is null)
            throw new ArgumentException("A Manager reply needs text or an attachment.", nameof(redactedText));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var projectId = SlackDeliveryOwnerIds.ManagerProjectId;
        var ownerKind = SlackDeliveryOwnerKinds.Manager;
        var inputDispatchRef = anchor.InputDispatchRef;

        var originAccepted = await db.SlackWorkspaceEnrollments.AnyAsync(enrollment =>
                enrollment.Id == anchor.EnrollmentId
                && enrollment.WorkspaceTeamId == anchor.Source.WorkspaceTeamId
                && enrollment.Lifecycle == SlackEnrollmentLifecycle.Active
                && enrollment.DeletedAt == null, ct)
            && await db.SlackProviderInboxRows.AnyAsync(inbox =>
                inbox.ProjectId == projectId
                && inbox.ConnectionId == anchor.EnrollmentId
                && inbox.SlackMessageIdentity == anchor.Source.AsKey()
                && inbox.WorkspaceTeamId == anchor.Source.WorkspaceTeamId
                && inbox.ConversationId == anchor.Source.ConversationId
                && (inbox.ThreadTs ?? anchor.Source.MessageTs) == anchor.ThreadRootMessageId
                && inbox.SlackUserId == anchor.ActorId
                && inbox.RouteSessionId == anchor.SessionId, ct)
            && await db.SlackDmSessionMappings.AnyAsync(mapping =>
                mapping.ProjectId == projectId
                && mapping.ConnectionId == anchor.EnrollmentId
                && mapping.WorkspaceTeamId == anchor.Source.WorkspaceTeamId
                && mapping.SlackUserId == anchor.ActorId
                && mapping.DmConversationId == anchor.Source.ConversationId
                && mapping.CurrentSessionId == anchor.SessionId, ct);
        if (!originAccepted)
        {
            await transaction.CommitAsync(ct);
            return new SlackAgentReplyResult(Accepted: false);
        }

        var existingTerminal = await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId
                && row.OwnerKind == ownerKind
                && row.ConnectionId == anchor.EnrollmentId
                && row.ConversationId == anchor.Source.ConversationId
                && row.DispatchRef == inputDispatchRef
                && (row.Kind == SlackOutboxKinds.TerminalResult
                    || row.Kind == SlackOutboxKinds.ExplicitFailure))
            .OrderBy(row => row.Id)
            .FirstOrDefaultAsync(ct);
        if (existingTerminal is not null)
        {
            var existingPayload = SlackDeliveryPayload.Parse(existingTerminal.PayloadJson);
            var requestedPayload = BuildManagerReplyPayload(
                redactedText,
                inputDispatchRef,
                existingPayload.ProviderMessageIdentity,
                imageUrl,
                fileName,
                fileContentBase64);
            if (!SameReplyContent(existingPayload, requestedPayload))
            {
                await transaction.CommitAsync(ct);
                return new SlackAgentReplyResult(
                    Accepted: false,
                    ConnectionId: anchor.EnrollmentId,
                    DeliveryId: existingTerminal.Id,
                    DispatchRef: existingTerminal.DispatchRef,
                    MergedIntoExisting: true,
                    ConflictingDuplicate: true,
                    Code: "manager_reply_idempotency_conflict",
                    Message: "A different reply was already submitted for this Manager input.");
            }

            await transaction.CommitAsync(ct);
            return new SlackAgentReplyResult(
                Accepted: true,
                ConnectionId: anchor.EnrollmentId,
                DeliveryId: existingTerminal.Id,
                DispatchRef: existingTerminal.DispatchRef,
                MergedIntoExisting: true);
        }

        var payload = BuildManagerReplyPayload(
            redactedText,
            inputDispatchRef,
            null,
            imageUrl,
            fileName,
            fileContentBase64);
        var now = _timeProvider.GetUtcNow();
        var row = new SlackOutboxRow
        {
            Id = $"slkout_{Guid.NewGuid():N}",
            ProjectId = projectId,
            ConnectionId = anchor.EnrollmentId,
            OwnerKind = ownerKind,
            WorkspaceTeamId = anchor.Source.WorkspaceTeamId,
            ConversationId = anchor.Source.ConversationId,
            ThreadTs = anchor.ThreadRootMessageId,
            Kind = SlackOutboxKinds.TerminalResult,
            State = SlackOutboxStates.Pending,
            DispatchRef = inputDispatchRef,
            PayloadJson = JsonSerializer.Serialize(payload),
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SlackOutboxRows.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDispatchRefConflict(ex))
        {
            db.Entry(row).State = EntityState.Detached;
            var duplicate = await db.SlackOutboxRows.AsNoTracking()
                .Where(candidate => candidate.ProjectId == projectId
                    && candidate.OwnerKind == ownerKind
                    && candidate.ConnectionId == anchor.EnrollmentId
                    && candidate.DispatchRef == inputDispatchRef
                    && candidate.Kind == SlackOutboxKinds.TerminalResult)
                .Select(candidate => new { candidate.Id, candidate.DispatchRef })
                .FirstOrDefaultAsync(ct);
            await transaction.CommitAsync(ct);
            return new SlackAgentReplyResult(
                Accepted: true,
                ConnectionId: anchor.EnrollmentId,
                DeliveryId: duplicate?.Id,
                DispatchRef: duplicate?.DispatchRef ?? inputDispatchRef,
                MergedIntoExisting: true);
        }

        return new SlackAgentReplyResult(
            Accepted: true,
            ConnectionId: anchor.EnrollmentId,
            DeliveryId: row.Id,
            DispatchRef: row.DispatchRef,
            MergedIntoExisting: false);
    }

    private static SlackDeliveryPayload BuildManagerReplyPayload(
        string text,
        string dispatchRef,
        SlackProviderMessageIdentity? providerIdentity,
        string? imageUrl,
        string? fileName,
        string? fileContentBase64)
    {
        if (!string.IsNullOrWhiteSpace(fileContentBase64))
        {
            return new SlackDeliveryPayload(
                SlackDeliveryOperations.UploadFile,
                text,
                ClientMessageId: dispatchRef,
                FileName: fileName,
                FileContentBase64: fileContentBase64);
        }

        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            return new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                text,
                ClientMessageId: dispatchRef,
                ProviderMessageIdentity: providerIdentity,
                Blocks: BuildImageBlocks(text, imageUrl));
        }

        var segments = SlackFinalReplyRenderer.SegmentReplyText(text);
        return new SlackDeliveryPayload(
            providerIdentity is null
                ? SlackDeliveryOperations.PostMessage
                : SlackDeliveryOperations.ChatUpdate,
            text,
            ClientMessageId: dispatchRef,
            ProviderMessageIdentity: providerIdentity,
            FallbackText: text,
            FallbackDispatchRef: $"{dispatchRef}:fallback",
            Segments: segments.Count > 1 ? segments : null,
            ReplyParts: [text]);
    }

    private static bool SameReplyContent(SlackDeliveryPayload existing, SlackDeliveryPayload requested) =>
        string.Equals(existing.Operation, requested.Operation, StringComparison.Ordinal)
        && string.Equals(existing.Text, requested.Text, StringComparison.Ordinal)
        && string.Equals(existing.FileName, requested.FileName, StringComparison.Ordinal)
        && string.Equals(existing.FileContentBase64, requested.FileContentBase64, StringComparison.Ordinal)
        && string.Equals(existing.Blocks?.GetRawText(), requested.Blocks?.GetRawText(), StringComparison.Ordinal);

    private static string ReplyDispatchRef(string logicalDispatchRef) =>
        $"slack-reply:{logicalDispatchRef}:terminal";
}
