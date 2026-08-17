using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

public sealed class SlackTurnControlService : IScopedService
{
    public const string StopActionId = "mohist_stop_turn";
    public const string RetryActionId = "mohist_retry_turn";
    public const string SelectionActionId = "mohist_select_connection";
    private static readonly TimeSpan StopActionLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDispatchLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> RetryableFailureCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "runner-unavailable",
        "runner-lost",
        "report-timeout",
        "timeout",
        "deadline-exceeded",
        "probe_timeout",
        "opencode-transport-failed",
        "unavailable-runtime",
        "rate_limited",
        "retry-safe",
    };

    private readonly ISecretStore _secrets;
    private readonly IGrainFactory _grains;
    private readonly AgentSessionQuerier _sessions;
    private readonly SlackProviderInboxStore _inbox;
    private readonly ISessionStopDelivery _stopDelivery;
    private readonly AgentQuerier _agents;
    private readonly IAgentLauncher _launcher;
    private readonly AgentSessionFollowupDispatcher _followupDispatcher;
    private readonly SlackRetryOperationStore _retryOperations;
    private readonly SlackConnectionAccessDecider _accessDecider;
    private readonly TimeProvider _time;

    public SlackTurnControlService(
        ISecretStore secrets,
        IGrainFactory grains,
        AgentSessionQuerier sessions,
        SlackProviderInboxStore inbox,
        ISessionStopDelivery stopDelivery,
        AgentQuerier agents,
        IAgentLauncher launcher,
        AgentSessionFollowupDispatcher followupDispatcher,
        SlackRetryOperationStore retryOperations,
        SlackConnectionAccessDecider accessDecider,
        TimeProvider time)
    {
        _secrets = secrets;
        _grains = grains;
        _sessions = sessions;
        _inbox = inbox;
        _stopDelivery = stopDelivery;
        _agents = agents;
        _launcher = launcher;
        _followupDispatcher = followupDispatcher;
        _retryOperations = retryOperations;
        _accessDecider = accessDecider;
        _time = time;
    }

    public Task<SlackStopAction?> CreateStopActionAsync(
        AgentConnection connection,
        string sessionId,
        string turnId,
        string inputId,
        string dispatchRef,
        string actorSlackUserId,
        SlackMessageIdentity source,
        string? threadTs,
        CancellationToken ct = default) =>
        CreateStopActionAsync(
            connection,
            sessionId,
            turnId,
            inputId,
            dispatchRef,
            actorSlackUserId,
            source,
            threadTs,
            originalDirectMessage: false,
            ct);

    public async Task<SlackStopAction?> CreateStopActionAsync(
        AgentConnection connection,
        string sessionId,
        string turnId,
        string inputId,
        string dispatchRef,
        string actorSlackUserId,
        SlackMessageIdentity source,
        string? threadTs,
        bool originalDirectMessage,
        CancellationToken ct = default)
    {
        var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var turn = await session.ResolveTurnControlAsync(turnId);
        if (turn?.Classification is not (AgentTurnControlClassification.Queued or AgentTurnControlClassification.Executing))
            return null;

        var initial = await session.GetInitialLaunchAsync();
        var provenance = initial?.Input?.Provenance;
        var initiator = provenance?.MemberId;
        if (!IsBoundToConnection(provenance, connection.Id)
            || !CanControl(connection, initiator, actorSlackUserId)
            || string.IsNullOrWhiteSpace(inputId)
            || string.IsNullOrWhiteSpace(dispatchRef))
            return null;

        var expiresAt = _time.GetUtcNow().Add(StopActionLifetime);
        var payload = new SlackStopActionPayload(
            Version: SlackActionCodec.Version,
            Action: SlackActionCodec.StopAction,
            ConnectionId: connection.Id,
            SessionId: sessionId,
            TurnId: turnId,
            InputId: inputId,
            DispatchRef: dispatchRef,
            ActorSlackUserId: actorSlackUserId,
            InitiatorSlackUserId: initiator!,
            ConversationId: source.ConversationId,
            MessageTs: source.MessageTs,
            ThreadTs: threadTs,
            Nonce: Guid.NewGuid().ToString("N"),
            ExpiresAt: expiresAt,
            Signature: null)
        {
            WorkspaceTeamId = source.WorkspaceTeamId,
            OriginalDirectMessage = originalDirectMessage,
        };
        var value = await CreateSignedActionValueAsync(connection, payload, ct);
        if (value is null)
            return null;

        return new SlackStopAction(StopActionId, value, expiresAt, BuildStopBlocks(value));
    }

    public async Task<SlackRetryAction?> CreateRetryActionAsync(
        AgentConnection connection,
        string sessionId,
        string inputId,
        string turnId,
        string dispatchRef,
        string actorSlackUserId,
        SlackMessageIdentity source,
        string? threadTs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(inputId)
            || string.IsNullOrWhiteSpace(turnId)
            || string.IsNullOrWhiteSpace(dispatchRef)
            || string.IsNullOrWhiteSpace(actorSlackUserId))
            return null;

        AgentSessionRetrySource? authoritative;
        try
        {
            authoritative = await ResolveRetrySourceAsync(sessionId, inputId, turnId);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        if (!IsRetryable(authoritative, connection.Id, actorSlackUserId, source, threadTs))
            return null;

        var expiresAt = _time.GetUtcNow().Add(StopActionLifetime);
        var payload = new SlackRetryActionPayload(
            Version: SlackActionCodec.Version,
            Action: SlackActionCodec.RetryAction,
            ConnectionId: connection.Id,
            SessionId: sessionId,
            TurnId: turnId,
            InputId: inputId,
            DispatchRef: dispatchRef,
            WorkspaceTeamId: source.WorkspaceTeamId,
            ConversationId: source.ConversationId,
            MessageTs: source.MessageTs,
            ThreadTs: threadTs,
            OriginalDirectMessage: authoritative!.Input.Provenance!.OriginalDirectMessage,
            ActorSlackUserId: actorSlackUserId,
            Nonce: Guid.NewGuid().ToString("N"),
            ExpiresAt: expiresAt,
            Signature: null);
        var value = await CreateSignedActionValueAsync(connection, payload, ct);
        return value is null
            ? null
            : new SlackRetryAction(RetryActionId, value, expiresAt, BuildRetryBlocks(value));
    }

    public static bool IsRetryableFailureCategory(string? category) =>
        !string.IsNullOrWhiteSpace(category)
        && RetryableFailureCategories.Contains(category.Trim());

    public Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        CancellationToken ct = default) =>
        HandleAsync(projectId, connection, request, interactionLeaseContext: null, ct);

    public async Task<SlackTurnControlResult> HandleAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        SlackInteractionLeaseContext? interactionLeaseContext,
        CancellationToken ct = default)
    {
        if (!string.Equals(request.EventType, "block_actions", StringComparison.Ordinal))
            return Rejected("unsupported_action", "This action is not supported.");

        if (string.Equals(request.ActionId, RetryActionId, StringComparison.Ordinal))
            return await HandleRetryAsync(projectId, connection, request, interactionLeaseContext, ct);

        if (!string.Equals(request.ActionId, StopActionId, StringComparison.Ordinal))
            return Rejected("unsupported_action", "This action is not supported.");

        var payload = await VerifySignedActionAsync<SlackStopActionPayload>(connection, request.ActionValue, ct);
        if (payload is null || !string.Equals(payload.Action, SlackActionCodec.StopAction, StringComparison.Ordinal))
            return Rejected("invalid_action", "This Stop action is invalid.");
        if (payload.ExpiresAt <= _time.GetUtcNow())
            return Rejected("expired", "This Stop action has expired.");
        if (!string.Equals(payload.ConnectionId, connection.Id, StringComparison.Ordinal)
            || !string.Equals(payload.WorkspaceTeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(request.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(payload.ConversationId, request.ConversationId, StringComparison.Ordinal)
            || !string.Equals(payload.ThreadTs, request.ThreadTs, StringComparison.Ordinal))
            return Rejected("stale_action", "This Stop action no longer matches the active Slack Connection.");
        if (!string.Equals(payload.ActorSlackUserId, request.ActorSlackUserId, StringComparison.Ordinal))
            return Rejected("unauthorized", "This Stop action belongs to a different Slack member.");

        var session = _grains.GetGrain<IAgentSessionGrain>(payload.SessionId);
        var initial = await session.GetInitialLaunchAsync();
        var provenance = initial?.Input?.Provenance;
        var initiator = provenance?.MemberId;
        if (!IsBoundToConnection(provenance, connection.Id)
            || !string.Equals(initiator, payload.InitiatorSlackUserId, StringComparison.Ordinal)
            || !CanControl(connection, initiator, request.ActorSlackUserId))
            return Rejected("unauthorized", "Only the Connection Owner or the session initiator may stop this Turn.");

        var accepted = await _inbox.AcceptAsync(
            new SlackProviderInboxDraft(
                projectId,
                connection.Id,
                new SlackMessageIdentity(request.TeamId, request.ConversationId, $"action:{payload.Nonce}"),
                request.ActorSlackUserId,
                request.ThreadTs),
            new SlackProviderInboxRouteDraft(
                SlackProviderInboxRouteKinds.Stop,
                payload.SessionId,
                payload.TurnId),
            ct);
        if (accepted.AlreadyExisted)
            return Rejected("replayed", "This Stop action was already used.");

        var turn = await session.ResolveTurnControlAsync(payload.TurnId);
        var turnRecord = (await session.ListTurnsAsync()).SingleOrDefault(candidate =>
            string.Equals(candidate.Id, payload.TurnId, StringComparison.Ordinal));
        if (turn is null
            || turn.Classification is not (AgentTurnControlClassification.Queued or AgentTurnControlClassification.Executing)
            || turnRecord is null
            || !turnRecord.InputIds.Contains(payload.InputId, StringComparer.Ordinal))
        {
            await _inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            return Rejected("stale_action", "That Turn is no longer available.");
        }

        var target = await _sessions.ResolveStopTargetAsync(projectId, payload.SessionId, ct);
        if (target is null)
        {
            await _inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            return Rejected("stale_action", "That Turn is no longer available.");
        }

        var control = await AgentSessionStopOperations.StopAsync(
            projectId,
            _grains,
            _stopDelivery,
            target,
            payload.TurnId,
            ct);
        await _inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
        return control.Kind switch
        {
            TurnControlResultKind.Cancelled => Confirmed("cancelled", "Work cancelled."),
            TurnControlResultKind.Stopped => Confirmed("stopped", "Work stopped."),
            TurnControlResultKind.StopRequested => Confirmed("stop_requested", "Stop requested. Waiting for the runtime to confirm."),
            TurnControlResultKind.Unknown => Confirmed("unknown", "The runtime could not confirm whether work stopped."),
            TurnControlResultKind.NotCancellable => Confirmed("not_cancellable", "The runtime cannot stop this work."),
            TurnControlResultKind.RunnerUnavailable => Confirmed("runner_unavailable", "The runtime is unavailable; Stop was not confirmed."),
            TurnControlResultKind.Blocked => Confirmed("blocked", "Stop recovery deadline was exhausted."),
            _ => Rejected("stale_action", "That Turn is no longer executing."),
        };
    }

    private async Task<SlackTurnControlResult> HandleRetryAsync(
        string projectId,
        AgentConnection connection,
        SlackInteractionRequest request,
        SlackInteractionLeaseContext? interactionLeaseContext,
        CancellationToken ct)
    {
        var payload = await VerifySignedActionAsync<SlackRetryActionPayload>(connection, request.ActionValue, ct);
        if (payload is null
            || !string.Equals(payload.Action, SlackActionCodec.RetryAction, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.ConnectionId)
            || string.IsNullOrWhiteSpace(payload.SessionId)
            || string.IsNullOrWhiteSpace(payload.InputId)
            || string.IsNullOrWhiteSpace(payload.TurnId)
            || string.IsNullOrWhiteSpace(payload.DispatchRef)
            || string.IsNullOrWhiteSpace(payload.WorkspaceTeamId)
            || string.IsNullOrWhiteSpace(payload.ConversationId)
            || string.IsNullOrWhiteSpace(payload.MessageTs)
            || string.IsNullOrWhiteSpace(payload.ActorSlackUserId))
            return Rejected("invalid", "This Retry action is invalid.");
        var actionKey = SlackRetryOperationStore.ActionKey(request.ActionValue);
        var resultReference = SlackRetryOperationStore.ResultReference(actionKey);
        if (payload.ExpiresAt <= _time.GetUtcNow())
            return RetryRejected("expired", "This Retry action has expired.", resultReference);
        if (!string.Equals(payload.ConnectionId, connection.Id, StringComparison.Ordinal)
            || !string.Equals(payload.WorkspaceTeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(request.TeamId, payload.WorkspaceTeamId, StringComparison.Ordinal)
            || !string.Equals(payload.ConversationId, request.ConversationId, StringComparison.Ordinal)
            || !string.Equals(payload.MessageTs, request.MessageTs, StringComparison.Ordinal)
            || !string.Equals(payload.ThreadTs, request.ThreadTs, StringComparison.Ordinal))
            return RetryRejected("stale", "This Retry action no longer matches the Slack message where it was issued.", resultReference);
        if (!string.Equals(payload.ActorSlackUserId, request.ActorSlackUserId, StringComparison.Ordinal))
            return RetryRejected("unauthorized", "This Retry action belongs to a different Slack member.", resultReference);
        if (interactionLeaseContext is null)
            return RetryRejected("unauthorized", "The current Slack authorization could not be confirmed.", resultReference);

        var authorization = await _accessDecider.EvaluateAsync(
            connection,
            request.ActorSlackUserId,
            request.TeamId,
            request.ConversationId,
            payload.OriginalDirectMessage,
            interactionLeaseContext.Receiving,
            ct);
        if (!authorization.Allowed)
            return RetryRejected("unauthorized", authorization.Reason, resultReference);

        var existingOperation = await _retryOperations.GetAsync(projectId, actionKey, ct);
        if (existingOperation?.Outcome is not null)
            return await RenderStoredRetryResultAsync(
                connection,
                payload,
                existingOperation,
                resultReference,
                ct,
                replayed: true);

        AgentSessionRetrySource? authoritative;
        try
        {
            authoritative = await ResolveRetrySourceAsync(payload.SessionId, payload.InputId, payload.TurnId);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return await RecordRetryTerminalOutcomeAsync(
                projectId,
                connection,
                payload,
                request,
                actionKey,
                resultReference,
                SlackRetryOperationOutcomes.Unavailable,
                "That failed execution is no longer available for retry.",
                ct);
        }
        var requestSource = new SlackMessageIdentity(request.TeamId, request.ConversationId, request.MessageTs);
        if (authoritative is null)
            return await RecordRetryTerminalOutcomeAsync(
                projectId,
                connection,
                payload,
                request,
                actionKey,
                resultReference,
                SlackRetryOperationOutcomes.Unavailable,
                "That failed execution is no longer available for retry.",
                ct);
        if (authoritative.Turn.Status != AgentTurnStatus.Failed
            || !IsRetryableFailureCategory(authoritative.Turn.Result?.FailureCategory))
            return await RecordRetryTerminalOutcomeAsync(
                projectId,
                connection,
                payload,
                request,
                actionKey,
                resultReference,
                SlackRetryOperationOutcomes.Unavailable,
                "That failed execution is no longer available for retry.",
                ct);
        var provenance = authoritative.Input.Provenance;
        if (provenance is null)
            return await RecordRetryTerminalOutcomeAsync(
                projectId,
                connection,
                payload,
                request,
                actionKey,
                resultReference,
                SlackRetryOperationOutcomes.Unavailable,
                "The original Slack provenance is unavailable.",
                ct);
        if (!IsRetryable(authoritative, connection.Id, payload.ActorSlackUserId, requestSource, request.ThreadTs)
            || provenance.OriginalDirectMessage != payload.OriginalDirectMessage)
            return await RecordRetryTerminalOutcomeAsync(
                projectId,
                connection,
                payload,
                request,
                actionKey,
                resultReference,
                SlackRetryOperationOutcomes.Stale,
                "This Retry action no longer matches the failed execution.",
                ct);

        var retryKey = SlackRetryOperationStore.RetryDispatchKey(projectId, actionKey);
        var isRoot = !string.IsNullOrWhiteSpace(authoritative.Turn.JobId);
        var preMintedSessionId = isRoot
            ? $"agent-session-{AgentLaunchCoordinatorCodec.StableToken($"{projectId}\n{retryKey}\nsession")}"
            : null;
        var preMintedInputId = AgentLaunchCoordinatorCodec.StableToken($"{projectId}\n{retryKey}\ninput");
        var preMintedTurnId = AgentLaunchCoordinatorCodec.StableToken($"{projectId}\n{retryKey}\nturn");
        var followupOperationId = isRoot
            ? null
            : $"followup:{AgentLaunchCoordinatorCodec.StableToken($"{projectId}\n{retryKey}\noperation")}";
        var operationResult = await _retryOperations.CreateOrLoadAsync(
            new SlackRetryOperationDraft(
                projectId,
                actionKey,
                connection.Id,
                authoritative.SessionId,
                payload.InputId,
                payload.TurnId,
                payload.DispatchRef,
                requestSource,
                request.ThreadTs,
                payload.OriginalDirectMessage,
                payload.ActorSlackUserId,
                retryKey,
                isRoot ? "root" : "followup",
                preMintedSessionId,
                preMintedInputId,
                preMintedTurnId,
                followupOperationId),
            ct);

        if (operationResult.Operation.Outcome is not null)
            return await RenderStoredRetryResultAsync(
                connection,
                payload,
                operationResult.Operation,
                resultReference,
                ct,
                replayed: true);

        var claimId = $"interaction:{Guid.NewGuid():N}";
        var operation = await _retryOperations.ClaimDispatchAsync(
            projectId,
            actionKey,
            claimId,
            RetryDispatchLeaseDuration,
            ct);
        if (operation is null)
        {
            var current = await _retryOperations.GetAsync(projectId, actionKey, ct);
            return current?.Outcome is not null
                ? await RenderStoredRetryResultAsync(
                    connection,
                    payload,
                    current,
                    resultReference,
                    ct,
                    replayed: true)
                : RetryAccepted(
                    resultReference,
                    "Retry was accepted and is waiting for the existing dispatch to finish.",
                    null);
        }
        if (isRoot)
        {
            var agent = await _agents.GetByIdAsync(projectId, connection.AgentId, ct);
            if (agent is null)
                return await CompleteRetryUnavailableAsync(operation, "The Agent bound to this Connection is unavailable.", resultReference, ct);

            try
            {
                var launch = await _launcher.LaunchConnectionRetryAsync(
                    agent,
                    authoritative.Input.Text,
                    new ConnectionLaunchOrigin(
                        connection.Id,
                        provenance.WorkspaceId,
                        provenance.MemberId,
                        provenance.ConversationId,
                        provenance.MessageId,
                        provenance.ThreadId,
                        provenance.OriginalDirectMessage),
                    operation.RetryDispatchKey,
                    authoritative.Input.Attachments,
                    operation.PreMintedSessionId,
                    operation.PreMintedInputId,
                    operation.PreMintedTurnId,
                    ct);
                var completed = await _retryOperations.CompleteAsync(
                    projectId,
                    actionKey,
                    SlackRetryOperationOutcomes.Accepted,
                    null,
                    launch.SessionId,
                    launch.InputId,
                    launch.TurnId,
                    ct);
                return completed?.Outcome is null
                    ? RetryAccepted(
                        resultReference,
                        "Retry accepted and is waiting for the runtime launch to recover.",
                        null)
                    : await RenderStoredRetryResultAsync(connection, payload, completed, resultReference, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return RetryAccepted(resultReference, "Retry accepted and is waiting for the runtime launch to recover.", null);
            }
        }

        var session = _grains.GetGrain<IAgentSessionGrain>(authoritative.SessionId);
        AgentSessionFollowupAcceptResult accepted;
        try
        {
            accepted = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: authoritative.Input.Text,
                Source: "slack-retry",
                IdempotencyKey: operation.RetryDispatchKey,
                Attachments: authoritative.Input.Attachments,
                PreMintedInputId: operation.PreMintedInputId,
                PreMintedTurnId: operation.PreMintedTurnId,
                AssignmentMode: AgentSessionFollowupAssignmentMode.ForceNewTurnForRetry,
                PreMintedOperationId: operation.FollowupOperationId,
                Provenance: provenance));
            operation = await _retryOperations.RecordAdmissionAsync(
                projectId,
                actionKey,
                accepted.InputId,
                accepted.TurnId,
                accepted.OperationId,
                ct) ?? operation;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return await CompleteRetryUnavailableAsync(
                operation,
                "The failed threaded turn is no longer available for retry.",
                resultReference,
                ct);
        }

        var dispatched = await _followupDispatcher.DispatchAsync(
            projectId,
            authoritative.SessionId,
            operation.FollowupOperationId ?? accepted.OperationId,
            ct);
        if (!dispatched)
        {
            await _retryOperations.ReleaseDispatchClaimAsync(projectId, actionKey, claimId, ct);
            return RetryAccepted(
                resultReference,
                "Retry accepted and is waiting for the current turn to finish.",
                null);
        }

        var completedFollowup = await _retryOperations.CompleteAsync(
            projectId,
            actionKey,
            SlackRetryOperationOutcomes.Accepted,
            null,
            authoritative.SessionId,
            accepted.InputId,
            accepted.TurnId,
            ct);
        return completedFollowup?.Outcome is null
            ? RetryAccepted(
                resultReference,
                "Retry accepted and is waiting for the runtime dispatch to recover.",
                null)
            : await RenderStoredRetryResultAsync(connection, payload, completedFollowup, resultReference, ct);
    }

    private async Task<SlackTurnControlResult> RecordRetryTerminalOutcomeAsync(
        string projectId,
        AgentConnection connection,
        SlackRetryActionPayload payload,
        SlackInteractionRequest request,
        string actionKey,
        string resultReference,
        string outcome,
        string reason,
        CancellationToken ct)
    {
        var operationResult = await _retryOperations.CreateOrLoadAsync(
            new SlackRetryOperationDraft(
                projectId,
                actionKey,
                connection.Id,
                payload.SessionId,
                payload.InputId,
                payload.TurnId,
                payload.DispatchRef,
                new SlackMessageIdentity(request.TeamId, request.ConversationId, request.MessageTs),
                request.ThreadTs,
                payload.OriginalDirectMessage,
                payload.ActorSlackUserId,
                SlackRetryOperationStore.RetryDispatchKey(projectId, actionKey),
                "terminal",
                null,
                null,
                null,
                null),
            ct);
        var operation = operationResult.Operation;
        if (operation.Outcome is null)
        {
            operation = await _retryOperations.CompleteAsync(
                projectId,
                actionKey,
                outcome,
                reason,
                null,
                null,
                null,
                ct) ?? operation;
        }
        return RetryRejected(
            operation.Outcome ?? outcome,
            operation.ResultReason ?? reason,
            resultReference);
    }

    private async Task<SlackTurnControlResult> RenderStoredRetryResultAsync(
        AgentConnection connection,
        SlackRetryActionPayload payload,
        SlackRetryOperationRow operation,
        string resultReference,
        CancellationToken ct,
        bool replayed = false)
    {
        if (!string.Equals(operation.Outcome, SlackRetryOperationOutcomes.Accepted, StringComparison.Ordinal))
            return RetryRejected(
                operation.Outcome ?? "unavailable",
                operation.ResultReason ?? "This Retry action is no longer available.",
                resultReference);

        JsonElement? blocks = null;
        if (!string.IsNullOrWhiteSpace(operation.ResultSessionId)
            && !string.IsNullOrWhiteSpace(operation.ResultTurnId)
            && !string.IsNullOrWhiteSpace(operation.ResultInputId))
        {
            var stop = await CreateStopActionAsync(
                connection,
                operation.ResultSessionId,
                operation.ResultTurnId,
                operation.ResultInputId,
                payload.DispatchRef,
                payload.ActorSlackUserId,
                new SlackMessageIdentity(payload.WorkspaceTeamId, payload.ConversationId, payload.MessageTs),
                payload.ThreadTs,
                payload.OriginalDirectMessage,
                ct);
            blocks = stop?.Blocks;
        }
        var text = replayed
            ? "Retry was already applied; the fresh attempt is still the current work."
            : "Retry accepted and queued a fresh attempt.";
        return new SlackTurnControlResult(
            replayed ? SlackRetryOperationOutcomes.AlreadyApplied : "accepted",
            text,
            blocks ?? BuildPresentationBlocks(text),
            resultReference);
    }

    private async Task<SlackTurnControlResult> CompleteRetryUnavailableAsync(
        SlackRetryOperationRow operation,
        string text,
        string resultReference,
        CancellationToken ct)
    {
        var completed = await _retryOperations.CompleteAsync(
            operation.ProjectId,
            operation.ActionKey,
            SlackRetryOperationOutcomes.Unavailable,
            text,
            null,
            null,
            null,
            ct) ?? operation;
        return RetryRejected(
            completed.Outcome ?? SlackRetryOperationOutcomes.Unavailable,
            completed.ResultReason ?? text,
            resultReference);
    }

    private async Task<AgentSessionRetrySource?> ResolveRetrySourceAsync(
        string sessionId,
        string inputId,
        string turnId)
    {
        var session = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        return await session.ResolveRetrySourceAsync(inputId, turnId);
    }

    private static bool IsRetryable(
        AgentSessionRetrySource? source,
        string connectionId,
        string actorSlackUserId,
        SlackMessageIdentity requestSource,
        string? requestThreadTs)
    {
        var provenance = source?.Input.Provenance;
        return source is not null
            && source.Turn.Status == AgentTurnStatus.Failed
            && IsRetryableFailureCategory(source.Turn.Result?.FailureCategory)
            && provenance is not null
            && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            && string.Equals(provenance.ConnectionId, connectionId, StringComparison.Ordinal)
            && string.Equals(provenance.MemberId, actorSlackUserId, StringComparison.Ordinal)
            && string.Equals(provenance.WorkspaceId, requestSource.WorkspaceTeamId, StringComparison.Ordinal)
            && string.Equals(provenance.ConversationId, requestSource.ConversationId, StringComparison.Ordinal)
            && string.Equals(provenance.MessageId, requestSource.MessageTs, StringComparison.Ordinal)
            && string.Equals(provenance.ThreadId, requestThreadTs, StringComparison.Ordinal);
    }

    private static SlackTurnControlResult RetryAccepted(
        string resultReference,
        string text,
        JsonElement? blocks) =>
        new("accepted", text, blocks ?? BuildPresentationBlocks(text), resultReference);

    private static SlackTurnControlResult RetryRejected(
        string state,
        string text,
        string resultReference) =>
        new(state, text, BuildPresentationBlocks(text), resultReference);

    public async Task<string?> CreateSignedActionValueAsync(
        AgentConnection connection,
        ISlackActionPayload payload,
        CancellationToken ct = default)
    {
        var key = await LoadSigningKeyAsync(connection, ct);
        if (key is null)
            return null;
        var signature = SlackActionCodec.Sign(payload, key);
        return SlackActionCodec.SerializeWithSignature(payload, signature);
    }

    public async Task<T?> VerifySignedActionAsync<T>(
        AgentConnection connection,
        string actionValue,
        CancellationToken ct = default)
        where T : class, ISlackActionPayload
    {
        var key = await LoadSigningKeyAsync(connection, ct);
        return key is not null
            && SlackActionCodec.TryVerify(actionValue, key, out T? payload)
            ? payload
            : null;
    }

    private async Task<byte[]?> LoadSigningKeyAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            var token = await _secrets.LoadAsync(
                new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
            return token is { Length: > 0 } ? token : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsBoundToConnection(AgentSessionInputProvenance? provenance, string connectionId) =>
        string.Equals(provenance?.ProviderKind, "slack", StringComparison.Ordinal)
        && string.Equals(provenance?.ConnectionId, connectionId, StringComparison.Ordinal);

    private static bool CanControl(AgentConnection connection, string? initiator, string actorSlackUserId) =>
        string.Equals(connection.OwnerSlackUserId, actorSlackUserId, StringComparison.Ordinal)
        || string.Equals(initiator, actorSlackUserId, StringComparison.Ordinal);

    private static SlackTurnControlResult Rejected(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static SlackTurnControlResult Confirmed(string state, string text) =>
        new(state, text, BuildPresentationBlocks(text));

    private static JsonElement BuildStopBlocks(string value) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "actions",
                block_id = "mohist-turn-control",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Stop" },
                        style = "danger",
                        action_id = StopActionId,
                        value,
                    },
                },
            },
        });

    private static JsonElement BuildRetryBlocks(string value) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "actions",
                block_id = "mohist-turn-control",
                elements = new object[]
                {
                    new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = "Retry" },
                        action_id = RetryActionId,
                        value,
                    },
                },
            },
        });

    private static JsonElement BuildPresentationBlocks(string text) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text },
            },
        });
}

public sealed record SlackRetryAction(
    string ActionId,
    string ActionValue,
    DateTimeOffset ExpiresAt,
    JsonElement Blocks);

public sealed record SlackTurnControlResult(
    string State,
    string Text,
    JsonElement Blocks,
    string? ResultReference = null);
