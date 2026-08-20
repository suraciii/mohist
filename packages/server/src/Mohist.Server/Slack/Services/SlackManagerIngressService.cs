using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerIngressService : IScopedService
{
    private readonly SlackWorkspaceEnrollmentStore _enrollments;
    private readonly SlackManagedBotAdmissionService _managedBotAdmission;
    private readonly SlackProviderInboxStore _inbox;
    private readonly SlackOutboxStore _outbox;
    private readonly ManagerClaimService _claims;
    private readonly ManagerActorAccessDecider _access;
    private readonly ISlackManagerConversationProcessor _conversation;

    public SlackManagerIngressService(
        SlackWorkspaceEnrollmentStore enrollments,
        SlackManagedBotAdmissionService managedBotAdmission,
        SlackProviderInboxStore inbox,
        SlackOutboxStore outbox,
        ManagerClaimService claims,
        ManagerActorAccessDecider access,
        ISlackManagerConversationProcessor conversation)
    {
        _enrollments = enrollments;
        _managedBotAdmission = managedBotAdmission;
        _inbox = inbox;
        _outbox = outbox;
        _claims = claims;
        _access = access;
        _conversation = conversation;
    }

    public async Task<SlackManagerIngressResult> AcceptAsync(
        SlackManagerIngressMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var identityError = message.Identity.Validate();
        if (!string.IsNullOrEmpty(identityError))
            throw new ArgumentException(identityError, nameof(message));

        // An explicit unknown classification is authoritative. Do not let a
        // sender id from an untrusted caller turn it into human work.
        if (string.Equals(message.SenderKind?.Trim(), "unknown", StringComparison.OrdinalIgnoreCase))
            return SlackManagerIngressResult.Rejected("manager_sender_required");

        var admission = await _managedBotAdmission.EvaluateAsync(
            message.Identity.WorkspaceTeamId,
            message.SenderKind,
            message.AuthorBot,
            ct);
        var enrollment = admission.ActiveEnrollment
            ?? await _enrollments.GetActiveByTeamAsync(message.Identity.WorkspaceTeamId, ct);
        if (enrollment is null)
            return SlackManagerIngressResult.Rejected("manager_enrollment_not_found");
        if (admission.IsManaged)
            return SlackManagerIngressResult.Ignored();
        if (!message.IsDirectMessage)
            return SlackManagerIngressResult.Rejected("manager_direct_message_required");
        if (!string.Equals(enrollment.ManagerAppId, message.AppId, StringComparison.Ordinal))
            return SlackManagerIngressResult.Rejected("manager_app_not_authorized");
        if (string.IsNullOrWhiteSpace(message.SenderSlackUserId))
            return SlackManagerIngressResult.Rejected("manager_sender_required");
        var senderSlackUserId = message.SenderSlackUserId!;

        var accepted = await _inbox.AcceptAsync(
            new SlackProviderInboxDraft(
                SlackDeliveryOwnerIds.ManagerProjectId,
                enrollment.Id,
                message.Identity,
                senderSlackUserId,
                message.ThreadTs),
            new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.Manager, SessionId: null),
            ct);
        if (accepted.AlreadyExisted)
        {
            var existing = (await _inbox.ListAsync(
                SlackDeliveryOwnerIds.ManagerProjectId,
                enrollment.Id,
                ct)).Entries.FirstOrDefault(entry => entry.Id == accepted.Id);
            if (existing is not null && !existing.IsPending)
                return SlackManagerIngressResult.Duplicate(accepted.Id);
        }

        var route = await _inbox.GetRouteAsync(
            SlackDeliveryOwnerIds.ManagerProjectId,
            accepted.Id,
            ct);

        var actor = await _access.AuthenticateAsync(
            message.Identity.WorkspaceTeamId,
            senderSlackUserId,
            ct);
        var claimCode = ReadClaimCode(message.Text);
        var claimAccepted = false;
        if (!actor.Allowed && claimCode is not null)
        {
            var claim = await _claims.ConsumeAsync(
                message.Identity.WorkspaceTeamId,
                senderSlackUserId,
                claimCode,
                ct);
            if (claim.Outcome == SlackManagerClaimOutcome.Accepted)
            {
                actor = await _access.AuthenticateAsync(
                    message.Identity.WorkspaceTeamId,
                    senderSlackUserId,
                    ct);
                claimAccepted = true;
            }
            else
            {
                await _inbox.MarkDispatchedAsync(SlackDeliveryOwnerIds.ManagerProjectId, accepted.Id, ct);
                return SlackManagerIngressResult.Rejected(ClaimReason(claim.Outcome), accepted.Id);
            }
        }

        if (!actor.Allowed || actor.Actor is null)
        {
            await _inbox.MarkDispatchedAsync(SlackDeliveryOwnerIds.ManagerProjectId, accepted.Id, ct);
            return SlackManagerIngressResult.Rejected("manager_actor_not_authorized", accepted.Id);
        }

        var decision = await _access.AuthorizeAsync(actor.Actor, ct: ct);
        if (!decision.Allowed)
        {
            await _inbox.MarkDispatchedAsync(SlackDeliveryOwnerIds.ManagerProjectId, accepted.Id, ct);
            return SlackManagerIngressResult.Rejected(decision.Reason ?? "manager_actor_not_authorized", accepted.Id);
        }

        if (claimAccepted || claimCode is not null)
        {
            await _inbox.MarkDispatchedAsync(SlackDeliveryOwnerIds.ManagerProjectId, accepted.Id, ct);
            return SlackManagerIngressResult.Accepted(accepted.Id, false);
        }

        if (accepted.AlreadyExisted && !string.IsNullOrWhiteSpace(route.SessionId))
        {
            await _inbox.MarkDispatchedAsync(SlackDeliveryOwnerIds.ManagerProjectId, accepted.Id, ct);
            return SlackManagerIngressResult.Accepted(accepted.Id, false);
        }

        var response = await _conversation.ProcessAsync(
            new SlackManagerConversationRequest(message, actor.Actor, route.SessionId), ct);
        if (!string.IsNullOrWhiteSpace(response.SessionId) && string.IsNullOrWhiteSpace(route.SessionId))
            await _inbox.SetRouteSessionIdAsync(
                SlackDeliveryOwnerIds.ManagerProjectId,
                accepted.Id,
                response.SessionId!,
                ct);
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            var dispatchRef = response.DispatchRef ?? $"manager:{message.Identity.AsKey()}:response";
            var payload = JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                response.Text,
                ClientMessageId: dispatchRef));
            await _outbox.EnqueueRequiredAsync(
                new SlackOutboxDraft(
                    SlackDeliveryOwnerIds.ManagerProjectId,
                    enrollment.Id,
                    message.Identity.WorkspaceTeamId,
                    message.Identity.ConversationId,
                    SlackOutboxKinds.TerminalResult,
                    dispatchRef,
                    payload,
                    message.ThreadTs,
                    SlackDeliveryOwnerKinds.Manager),
                ct);
        }

        await _inbox.MarkDispatchedAsync(SlackDeliveryOwnerIds.ManagerProjectId, accepted.Id, ct);
        return SlackManagerIngressResult.Accepted(accepted.Id, response.Text is not null);
    }

    private static string? ReadClaimCode(string text)
    {
        var value = text.Trim();
        const string prefix = "claim ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var code = value[prefix.Length..].Trim();
        return code.Length == 0 ? null : code;
    }

    private static string ClaimReason(string outcome) => outcome switch
    {
        SlackManagerClaimOutcome.Expired => "manager_claim_expired",
        SlackManagerClaimOutcome.Consumed => "manager_claim_consumed",
        SlackManagerClaimOutcome.NoClaim => "manager_claim_required",
        _ => "manager_claim_invalid",
    };
}

public sealed record SlackManagerIngressMessage(
    string AppId,
    SlackMessageIdentity Identity,
    string? SenderSlackUserId,
    string Text,
    bool IsDirectMessage,
    string? ThreadTs = null,
    string? SenderKind = null,
    SlackBotAuthorMetadata? AuthorBot = null);

public sealed record SlackManagerConversationRequest(
    SlackManagerIngressMessage Message,
    ManagerActorContext Actor,
    string? CurrentSessionId = null);

public sealed record SlackManagerConversationResult(
    string? Text = null,
    string? DispatchRef = null,
    string? SessionId = null);

public interface ISlackManagerConversationProcessor
{
    Task<SlackManagerConversationResult> ProcessAsync(
        SlackManagerConversationRequest request,
        CancellationToken ct = default);
}

public sealed class UnavailableSlackManagerConversationProcessor :
    IScopedService,
    ISlackManagerConversationProcessor
{
    public Task<SlackManagerConversationResult> ProcessAsync(
        SlackManagerConversationRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new SlackManagerConversationResult());
}

public sealed record SlackManagerIngressResult(
    string Decision,
    string? InboxId = null,
    string? Reason = null,
    bool DeliveryIntentCreated = false)
{
    // Keep the existing decision field for direct API callers while exposing
    // the adapter's ingress-result discriminator on the wire.
    public string Kind => Decision;

    public static SlackManagerIngressResult Ignored() =>
        new("ignored");

    public static SlackManagerIngressResult Accepted(string inboxId, bool deliveryIntentCreated) =>
        new("accepted", inboxId, null, deliveryIntentCreated);

    public static SlackManagerIngressResult Duplicate(string inboxId) =>
        new("duplicate", inboxId);

    public static SlackManagerIngressResult Rejected(string reason, string? inboxId = null) =>
        new("rejected", inboxId, reason);
}
