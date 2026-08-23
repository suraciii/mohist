using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// Accepts a signed Slack chooser action. The claim row is the decision
/// fence: all mutable checks happen before its Pending-to-Decided CAS, and
/// dispatch only happens after that complete decision is durable.
/// </summary>
internal sealed class SlackAgentSelectionService : IScopedService
{
    private readonly ISlackActionSigner _signing;
    private readonly SlackAmbiguousPromptStore _prompts;
    private readonly SlackOutboxStore _outbox;
    private readonly SlackProviderInboxStore _inbox;
    private readonly AgentConnectionStore _connections;
    private readonly SlackConnectionAccessDecider _access;
    private readonly ISlackLeaseStore _leaseStore;
    private readonly SlackAdapterLeaseService _leases;
    private readonly AgentQuerier _agents;
    private readonly SlackAdmissionService _admission;
    private readonly SlackThreadSessionMappingStore _threadMappings;
    private readonly SlackChannelLaunchService _launch;
    private readonly SlackAttachmentInputBinder _attachments;
    private readonly IGrainFactory _grains;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly TimeProvider _time;

    public SlackAgentSelectionService(
        ISlackActionSigner signing,
        SlackAmbiguousPromptStore prompts,
        SlackOutboxStore outbox,
        SlackProviderInboxStore inbox,
        AgentConnectionStore connections,
        SlackConnectionAccessDecider access,
        ISlackLeaseStore leaseStore,
        SlackAdapterLeaseService leases,
        AgentQuerier agents,
        SlackAdmissionService admission,
        SlackThreadSessionMappingStore threadMappings,
        SlackChannelLaunchService launch,
        SlackAttachmentInputBinder attachments,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followups,
        TimeProvider time)
    {
        _signing = signing;
        _prompts = prompts;
        _outbox = outbox;
        _inbox = inbox;
        _connections = connections;
        _access = access;
        _leaseStore = leaseStore;
        _leases = leases;
        _agents = agents;
        _admission = admission;
        _threadMappings = threadMappings;
        _launch = launch;
        _attachments = attachments;
        _grains = grains;
        _followups = followups;
        _time = time;
    }

    public async Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection postingConnection,
        SlackInteractionRequest request,
        SlackLeaseContext promptOwnerLease,
        CancellationToken ct = default)
    {
        if (!string.Equals(request.EventType, "block_actions", StringComparison.Ordinal)
            || !string.Equals(request.ActionId, SlackSelectionActionPayload.ActionId, StringComparison.Ordinal))
            return Rejected("unsupported_action", "This action is not supported.");

        var payload = await VerifyAsync(postingConnection, request.ActionValue, ct);
        if (payload is null)
            return Rejected("invalid_action", "This Agent selection action is invalid.");
        if (payload.ExpiresAt <= _time.GetUtcNow())
            return Rejected("expired", "This Agent selection has expired. Please re-mention a single Bot.");

        var claim = await _prompts.FindAsync(
            payload.WorkspaceTeamId,
            payload.ConversationId,
            payload.OriginalMessageTs,
            ct);
        if (!await MatchesContextAsync(
                projectId,
                postingConnection,
                request,
                payload,
                claim,
                ct))
            return Rejected("stale_action", "This Agent selection is stale and no longer matches its chooser message.");

        if (!string.Equals(payload.ActorSlackUserId, request.ActorSlackUserId, StringComparison.Ordinal))
            return Rejected("unauthorized", "This Agent selection belongs to a different Slack member.");

        // A recorded decision is authoritative. Do not re-run mutable policy,
        // lease, binding, or readiness checks for a replay.
        if (claim!.SelectionState is SlackSelectionStates.Decided
            or SlackSelectionStates.Completed
            or SlackSelectionStates.Settled)
            return DecisionView(claim);

        var promptOwnerAccess = await _access.EvaluateAsync(
            postingConnection,
            request.ActorSlackUserId,
            request.TeamId,
            request.ConversationId,
            IsDirectMessage(request.ConversationId),
            promptOwnerLease,
            ct);
        if (!promptOwnerAccess.Allowed)
            return Rejected("unauthorized", promptOwnerAccess.Reason);

        var selected = await _connections.GetAsync(
            payload.ChosenProjectId,
            payload.ChosenConnectionId,
            ct);
        // GetAsync intentionally includes soft-deleted rows. Deletion must be
        // checked before looking at stale leases or bindings left by cleanup.
        if (selected is null || selected.DeletedAt is not null)
            return Rejected("unavailable", "The selected Connection is unavailable.");

        var selectedCandidate = DeserializeCandidates(claim!.CandidateReferencesJson)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.ProjectId, payload.ChosenProjectId, StringComparison.Ordinal)
                && string.Equals(candidate.ConnectionId, payload.ChosenConnectionId, StringComparison.Ordinal));
        if (selectedCandidate is null)
            return Rejected("stale_action", "The selected Agent is no longer one of the durable chooser candidates.");
        if (!AgentConnectionStore.HasBoundIdentity(selected)
            || !string.Equals(selected.WorkspaceTeamId, payload.WorkspaceTeamId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(selectedCandidate.BotUserId)
                && !string.Equals(selected.BotUserId, selectedCandidate.BotUserId, StringComparison.Ordinal)))
            return Rejected("no_longer_valid", "The selected Agent is no longer bound to this Slack workspace.");

        var selectedLease = await ResolveSelectedLeaseAsync(
            request,
            selected,
            promptOwnerLease.OperatorId,
            ct);
        if (selectedLease is null)
            return Rejected("unavailable", "The selected Connection has no valid runtime lease.");

        var sameConnection = string.Equals(selected.ProjectId, projectId, StringComparison.Ordinal)
            && string.Equals(selected.Id, postingConnection.Id, StringComparison.Ordinal);
        if (!sameConnection)
        {
            var selectedAccess = await _access.EvaluateAsync(
                selected,
                request.ActorSlackUserId,
                request.TeamId,
                request.ConversationId,
                IsDirectMessage(request.ConversationId),
                selectedLease.Context,
                ct);
            if (!selectedAccess.Allowed)
                return Rejected("unauthorized", selectedAccess.Reason);
        }

        if (selected.DesiredState == DesiredStateKind.Disabled)
            return Rejected("connection_disabled", "This Slack Connection is disabled.");

        var binding = string.IsNullOrWhiteSpace(claim.ThreadTs)
            ? null
            : await _threadMappings.GetSessionIdAsync(
                selected.ProjectId,
                payload.WorkspaceTeamId,
                selected.Id,
                payload.ConversationId,
                claim.ThreadTs!,
                ct);

        var dispatchKind = claim.AmbiguityKind switch
        {
            SlackAmbiguityKinds.RootMultiMention => SlackSelectionDispatchKinds.RootLaunch,
            SlackAmbiguityKinds.ThreadMultiMention => binding is null
                ? SlackSelectionDispatchKinds.ThreadLaunch
                : SlackSelectionDispatchKinds.ThreadFollowup,
            SlackAmbiguityKinds.MultiBoundThreadReply when binding is not null => SlackSelectionDispatchKinds.ThreadFollowup,
            SlackAmbiguityKinds.MultiBoundThreadReply => null,
            _ => null,
        };
        if (dispatchKind is null)
            return Rejected("no_longer_valid", "The selected Agent is no longer bound to this thread.");

        var identity = new SlackMessageIdentity(
            payload.WorkspaceTeamId,
            payload.ConversationId,
            payload.OriginalMessageTs);
        var ids = PreAllocateIds(dispatchKind, payload.ChosenProjectId, identity, binding);

        if (dispatchKind is SlackSelectionDispatchKinds.RootLaunch or SlackSelectionDispatchKinds.ThreadLaunch)
        {
            var agent = await _agents.GetByIdAsync(selected.ProjectId, selected.AgentId, ct);
            if (agent is null)
                return Rejected("no_longer_valid", "The selected Agent is no longer available.");

            var admission = await _admission.AdmitNewWorkAsync(
                selected.ProjectId,
                selected,
                agent,
                identity,
                claim.ThreadTs,
                ct);
            if (!admission.Admitted)
                return new SlackTurnControlResult(
                    admission.Kind,
                    admission.Reason ?? SlackAdmissionMessages.AgentNotReady,
                    BuildPresentationBlocks(admission.Reason ?? SlackAdmissionMessages.AgentNotReady));
        }
        else if (binding is null)
        {
            return Rejected("no_longer_valid", "The selected Agent is no longer bound to this thread.");
        }

        var decided = await _prompts.TryDecideAsync(
            payload.WorkspaceTeamId,
            payload.ConversationId,
            payload.OriginalMessageTs,
            payload.ChosenProjectId,
            payload.ChosenConnectionId,
            dispatchKind,
            ids.SessionId,
            ids.InputId,
            ids.TurnId,
            ct);
        if (!decided.Decided)
            return DecisionView(decided.Snapshot);

        var dispatchResult = await DispatchAsync(
            selected,
            identity,
            claim,
            dispatchKind,
            binding,
            ids,
            ct);
        if (dispatchResult.State is "accepted" or "already_accepted")
        {
            await _prompts.MarkCompletedAsync(claim.Id, dispatchResult.State, ct);
        }
        return dispatchResult;
    }

    /// <summary>
    /// Replays a committed selection without consulting the original
    /// interaction. The claim is the authority: this path never re-runs
    /// authorization, resolves the prompt-owner Project, or reclassifies the
    /// dispatch kind from current bindings.
    /// </summary>
    internal async Task<SlackSelectionRecoveryResult> RecoverAsync(
        SlackAmbiguousPromptSnapshot claim,
        CancellationToken ct = default)
    {
        if (claim.SelectionState != SlackSelectionStates.Decided
            || string.IsNullOrWhiteSpace(claim.ChosenProjectId)
            || string.IsNullOrWhiteSpace(claim.ChosenConnectionId)
            || string.IsNullOrWhiteSpace(claim.DispatchKind)
            || string.IsNullOrWhiteSpace(claim.SelectionSessionId)
            || string.IsNullOrWhiteSpace(claim.SelectionInputId)
            || string.IsNullOrWhiteSpace(claim.SelectionTurnId))
            return SlackSelectionRecoveryResult.Terminal("selection_record_incomplete");

        var selected = await _connections.GetAsync(
            claim.ChosenProjectId,
            claim.ChosenConnectionId,
            ct);
        if (selected is null || selected.DeletedAt is not null)
            return SlackSelectionRecoveryResult.Terminal("selected_connection_unavailable");
        if (selected.DesiredState == DesiredStateKind.Disabled)
            return SlackSelectionRecoveryResult.Terminal("selected_connection_disabled");

        var identity = new SlackMessageIdentity(
            claim.WorkspaceTeamId,
            claim.ConversationId,
            claim.MessageTs);
        // The selected session id is part of the durable decision. Recovery
        // must not reclassify a follow-up from a mutable binding lookup.
        var binding = claim.DispatchKind == SlackSelectionDispatchKinds.ThreadFollowup
            ? claim.SelectionSessionId
            : null;
        if (claim.DispatchKind == SlackSelectionDispatchKinds.ThreadFollowup
            && string.IsNullOrWhiteSpace(binding))
            return SlackSelectionRecoveryResult.Terminal("selected_thread_binding_missing");

        var agent = await _agents.GetByIdAsync(
            claim.ChosenProjectId,
            selected.AgentId,
            ct);
        if (agent is null)
            return SlackSelectionRecoveryResult.Terminal("selected_agent_unavailable");

        var result = await DispatchAsync(
            selected,
            identity,
            claim,
            claim.DispatchKind,
            binding,
            new SelectionExecutionIds(
                claim.SelectionSessionId,
                claim.SelectionInputId,
                claim.SelectionTurnId),
            ct);
        if (result.State is "accepted" or "already_accepted")
            return SlackSelectionRecoveryResult.Success(result.State);
        if (result.State is "connection_disabled" or "no_longer_valid")
            return SlackSelectionRecoveryResult.Terminal(result.State);
        throw new InvalidOperationException(
            $"Committed Slack Agent selection {claim.Id} returned non-terminal state '{result.State}'.");
    }

    private async Task<SlackTurnControlResult> DispatchAsync(
        AgentConnection selected,
        SlackMessageIdentity identity,
        SlackAmbiguousPromptSnapshot claim,
        string dispatchKind,
        string? binding,
        SelectionExecutionIds ids,
        CancellationToken ct)
    {
        if (dispatchKind is SlackSelectionDispatchKinds.RootLaunch or SlackSelectionDispatchKinds.ThreadLaunch)
        {
            var threadAnchor = dispatchKind == SlackSelectionDispatchKinds.RootLaunch
                ? identity.MessageTs
                : claim.ThreadTs!;
            var result = await _launch.LaunchAsync(new SlackChannelLaunchRequest(
                selected.ProjectId,
                selected,
                identity,
                claim.SenderSlackUserId,
                claim.TaskText,
                DeserializeFiles(claim.FilesJson),
                threadAnchor,
                claim.ThreadTs,
                new SlackChannelLaunchServiceLaunchIds(ids.SessionId, ids.InputId, ids.TurnId),
                StartupContext: null,
                _threadMappings), ct);
            if (result.ResponseOwner == SlackIngressResponseOwners.Server)
                return new SlackTurnControlResult(
                    result.Kind,
                    result.Reason ?? "The selected Agent is not ready to execute this work.",
                    BuildPresentationBlocks(result.Reason ?? "The selected Agent is not ready to execute this work."));
            if (result.Kind == "connection_disabled")
                return Rejected("connection_disabled", result.Reason ?? "This Slack Connection is disabled.");
            if (result.ResponseOwner == SlackIngressResponseOwners.Adapter
                || result.Kind is "backpressured" or "slack_thread_launch_in_progress")
                return new SlackTurnControlResult(
                    result.Kind,
                    result.Reason ?? "The selected Agent is temporarily busy. Please retry shortly.",
                    BuildPresentationBlocks(result.Reason ?? "The selected Agent is temporarily busy. Please retry shortly."));
            if (result.Kind == "agent_not_found")
                return Rejected("no_longer_valid", "The selected Agent is no longer available.");
            return Confirmed("accepted", "Agent selection accepted and work is being started.");
        }

        var files = DeserializeFiles(claim.FilesJson);
        var provenance = new AgentSessionInputProvenance(
            ProviderKind: "slack",
            WorkspaceId: identity.WorkspaceTeamId,
            ConversationId: identity.ConversationId,
            ThreadId: claim.ThreadTs,
            MemberId: claim.SenderSlackUserId,
            MessageId: identity.MessageTs,
            ConnectionId: selected.Id,
            BoundThreadRootMessageId: claim.ThreadTs);
        var bindingSession = binding!;
        var attachmentBinding = await _attachments.PrepareAsync(
            selected.ProjectId,
            selected,
            identity,
            ids.SessionId,
            ids.InputId,
            files,
            ct);
        if (string.IsNullOrWhiteSpace(claim.TaskText) && attachmentBinding.AcceptedCount == 0)
        {
            await _attachments.RollbackAsync(
                selected.ProjectId,
                bindingSession,
                ids.InputId,
                attachmentBinding,
                CancellationToken.None);
            return Rejected("no_longer_valid", "The original message did not contain executable work.");
        }

        var inbox = await _inbox.AcceptAsync(new SlackProviderInboxDraft(
            selected.ProjectId,
            selected.Id,
            identity,
            claim.SenderSlackUserId,
            claim.ThreadTs),
            new SlackProviderInboxRouteDraft(
                SlackProviderInboxRouteKinds.FollowupThread,
                bindingSession),
            ct);
        var session = _grains.GetGrain<IAgentSessionGrain>(bindingSession);
        AgentSessionFollowupAcceptResult accepted;
        try
        {
            accepted = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: claim.TaskText,
                Source: "agent-session-followup",
                IdempotencyKey: SelectionFollowupIdempotencyKey(identity),
                Attachments: attachmentBinding.AcceptedDescriptors,
                PreMintedInputId: ids.InputId,
                PreMintedTurnId: ids.TurnId,
                AttachmentResults: attachmentBinding.Results,
                Provenance: provenance));
        }
        catch
        {
            await _attachments.RollbackAsync(
                selected.ProjectId,
                bindingSession,
                ids.InputId,
                attachmentBinding,
                CancellationToken.None);
            throw;
        }

        await _followups.DispatchNextAsync(selected.ProjectId, bindingSession, ct);
        if (!inbox.AlreadyExisted)
            await _inbox.MarkDispatchedAsync(selected.ProjectId, inbox.Id, ct);

        return Confirmed(
            accepted.AlreadyAccepted ? "already_accepted" : "accepted",
            accepted.AlreadyAccepted
                ? "This Agent selection was already accepted."
                : "Agent selection accepted and the existing thread is continuing.");
    }

    private async Task<SelectedLease?> ResolveSelectedLeaseAsync(
        SlackInteractionRequest request,
        AgentConnection selected,
        string operatorId,
        CancellationToken ct)
    {
        var target = new SlackLeaseTargetRef.Connection(selected.ProjectId, selected.Id);
        var active = await _leaseStore.GetActiveAsync(target.TargetKey, ct);
        if (active is null || active.Kind != SlackLeaseKind.Runtime || active.ExpiresAt <= _time.GetUtcNow())
            return null;
        if (!await _leases.ValidateRuntimeLeaseAsync(
                operatorId,
                target,
                active.LeaseId,
                active.AdapterId,
                ct))
            return null;

        return new SelectedLease(
            active,
            new SlackLeaseContext(
                operatorId,
                active.LeaseId,
                active.AdapterId,
                (targetRef, leaseCt) => _leases.ResolveRuntimeLeaseBotTokenAsync(
                    operatorId,
                    targetRef,
                    active.LeaseId,
                    active.AdapterId,
                    leaseCt)));
    }

    private async Task<bool> MatchesContextAsync(
        string projectId,
        AgentConnection postingConnection,
        SlackInteractionRequest request,
        SlackSelectionActionPayload payload,
        SlackAmbiguousPromptSnapshot? claim,
        CancellationToken ct)
    {
        if (claim is null
            || !string.Equals(payload.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(payload.ConnectionId, postingConnection.Id, StringComparison.Ordinal)
            || !string.Equals(payload.WorkspaceTeamId, postingConnection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(request.TeamId, payload.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(request.ConversationId, payload.ConversationId, StringComparison.Ordinal)
            || !string.Equals(request.ThreadTs, payload.ThreadTs, StringComparison.Ordinal)
            || !string.Equals(claim.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(claim.WinningConnectionId, postingConnection.Id, StringComparison.Ordinal)
            || !string.Equals(claim.AmbiguityKind, payload.AmbiguityKind, StringComparison.Ordinal)
            || !string.Equals(claim.ThreadTs, payload.ThreadTs, StringComparison.Ordinal)
            || !CandidateSnapshotsEqual(claim.CandidateReferencesJson, payload.CandidateReferences)
            || !CandidateSnapshotsContain(
                claim.CandidateReferencesJson,
                payload.ChosenProjectId,
                payload.ChosenConnectionId))
            return false;

        var chooser = await _outbox.FindByDispatchRefAsync(
            projectId,
            postingConnection.Id,
            SlackOutboxKinds.UserAction,
            SlackAmbiguousPromptStore.PromptDispatchRef(
                payload.WorkspaceTeamId,
                payload.ConversationId,
                payload.OriginalMessageTs),
            ct);
        if (chooser is null)
            return false;
        var delivery = SlackDeliveryPayload.Parse(chooser.PayloadJson);
        return delivery.ProviderMessageIdentity is { } providerIdentity
            && string.Equals(providerIdentity.ConversationId, request.ConversationId, StringComparison.Ordinal)
            && string.Equals(providerIdentity.MessageTs, request.MessageTs, StringComparison.Ordinal);
    }

    private async Task<SlackSelectionActionPayload?> VerifyAsync(
        AgentConnection connection,
        string actionValue,
        CancellationToken ct)
    {
        SlackSelectionActionPayload? payload;
        try
        {
            payload = JSON.Deserialize<SlackSelectionActionPayload>(actionValue);
        }
        catch (JsonException)
        {
            return null;
        }

        if (!SlackSelectionActionPayload.IsStructurallyValid(payload))
            return null;
        return await _signing.VerifyAsync(
            connection,
            SlackSelectionActionPayload.Canonical(payload! with { Signature = null }),
            payload!.Signature!,
            ct)
            ? payload
            : null;
    }

    private static bool CandidateSnapshotsContain(
        string durableJson,
        string projectId,
        string connectionId)
    {
        try
        {
            return DeserializeCandidates(durableJson).Any(candidate =>
                string.Equals(candidate.ProjectId, projectId, StringComparison.Ordinal)
                && string.Equals(candidate.ConnectionId, connectionId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CandidateSnapshotsEqual(
        string durableJson,
        IReadOnlyList<SlackSelectionCandidateReference> signed)
    {
        try
        {
            var durable = DeserializeCandidates(durableJson);
            return durable.SequenceEqual(signed);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsDirectMessage(string conversationId) =>
        conversationId.StartsWith("D", StringComparison.Ordinal);

    private static IReadOnlyList<SlackSelectionCandidateReference> DeserializeCandidates(string json) =>
        JSON.Deserialize<List<SlackSelectionCandidateReference>>(json) ?? [];

    private static IReadOnlyList<SlackIngressFile> DeserializeFiles(string json) =>
        JSON.Deserialize<List<SlackIngressFile>>(json) ?? [];

    private static SelectionExecutionIds PreAllocateIds(
        string dispatchKind,
        string projectId,
        SlackMessageIdentity identity,
        string? boundSessionId)
    {
        if (dispatchKind == SlackSelectionDispatchKinds.ThreadFollowup)
        {
            var input = AgentLaunchCoordinatorCodec.StableToken(
                $"{boundSessionId}\n{SelectionFollowupIdempotencyKey(identity)}\nselection-followup-input");
            var turn = AgentLaunchCoordinatorCodec.StableToken(
                $"{boundSessionId}\n{SelectionFollowupIdempotencyKey(identity)}\nselection-followup-turn");
            return new(boundSessionId!, input, turn);
        }

        var ids = SlackChannelLaunchService.PreMintSlackLaunchIds(projectId, identity);
        return new(ids.SessionId, ids.InputId, ids.TurnId);
    }

    private static string SelectionFollowupIdempotencyKey(SlackMessageIdentity identity) =>
        $"slack-selection:{identity.WorkspaceTeamId}:{identity.ConversationId}:{identity.MessageTs}";

    private static SlackTurnControlResult DecisionView(SlackAmbiguousPromptSnapshot snapshot) =>
        Confirmed(
            "decided",
            $"This Agent selection was already decided for {snapshot.ChosenConnectionId ?? "another Agent"}.");

    private static SlackTurnControlResult Rejected(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static SlackTurnControlResult Confirmed(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static JsonElement BuildPresentationBlocks(string text) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "section", text = new { type = "mrkdwn", text } },
        });

    private sealed record SelectedLease(SlackLeaseRecord Record, SlackLeaseContext Context);

    private sealed record SelectionExecutionIds(string SessionId, string InputId, string TurnId);

}

internal sealed record SlackSelectionRecoveryResult(
    bool Completed,
    bool Settled,
    string State,
    string? Reason = null)
{
    public static SlackSelectionRecoveryResult Success(string state) =>
        new(true, false, state);

    public static SlackSelectionRecoveryResult Terminal(string reason) =>
        new(false, true, "settled", reason);
}
