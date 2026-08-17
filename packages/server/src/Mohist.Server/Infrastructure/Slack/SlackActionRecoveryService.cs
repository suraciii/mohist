using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackActionRecoveryService : IScopedService
{
    private static readonly TimeSpan RecoveryLeaseDuration = TimeSpan.FromSeconds(30);
    private readonly SlackRetryOperationStore _operations;
    private readonly AgentConnectionStore _connections;
    private readonly AgentQuerier _agents;
    private readonly IAgentLauncher _launcher;
    private readonly IGrainFactory _grains;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly SlackOutboxStore _outbox;
    private readonly SlackTurnControlService _controls;
    private readonly TimeProvider _time;
    private readonly ILogger<SlackActionRecoveryService> _log;

    public SlackActionRecoveryService(
        SlackRetryOperationStore operations,
        AgentConnectionStore connections,
        AgentQuerier agents,
        IAgentLauncher launcher,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followups,
        SlackOutboxStore outbox,
        SlackTurnControlService controls,
        TimeProvider time,
        ILogger<SlackActionRecoveryService> log)
    {
        _operations = operations;
        _connections = connections;
        _agents = agents;
        _launcher = launcher;
        _grains = grains;
        _followups = followups;
        _outbox = outbox;
        _controls = controls;
        _time = time;
        _log = log;
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var pending = await _operations.ListDuePendingAsync(_time.GetUtcNow(), ct: ct);
        foreach (var candidate in pending)
        {
            ct.ThrowIfCancellationRequested();
            var lease = await _operations.ClaimRecoveryAsync(
                candidate.ProjectId,
                candidate.ActionKey,
                $"recovery:{Guid.NewGuid():N}",
                RecoveryLeaseDuration,
                ct);
            if (lease is null)
                continue;

            try
            {
                await ResumeAsync(lease, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Slack Retry operation {ActionKey} remains pending after recovery attempt",
                    lease.ActionKey);
            }
        }
    }

    private async Task ResumeAsync(SlackRetryOperationRow operation, CancellationToken ct)
    {
        var connection = await _connections.GetAsync(operation.ProjectId, operation.ConnectionId, ct);
        if (connection is null || connection.DesiredState == Agent.Domain.DesiredStateKind.Disabled)
        {
            await CompleteUnavailableAsync(operation, "The Slack Connection is unavailable.", ct);
            return;
        }

        var source = _grains.GetGrain<IAgentSessionGrain>(operation.SessionId);
        AgentSessionRetrySource? retrySource;
        try
        {
            retrySource = await source.ResolveRetrySourceAsync(operation.FailedInputId, operation.FailedTurnId);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await CompleteUnavailableAsync(operation, "The failed execution is no longer available for retry.", ct);
            return;
        }
        if (retrySource is null
            || retrySource.Turn.Status != AgentTurnStatus.Failed
            || !SlackTurnControlService.IsRetryableFailureCategory(retrySource.Turn.Result?.FailureCategory)
            || !MatchesOperation(retrySource, operation))
        {
            await CompleteUnavailableAsync(operation, "The failed execution is no longer available for retry.", ct);
            return;
        }

        var provenance = retrySource.Input.Provenance;
        if (provenance is null)
        {
            await CompleteUnavailableAsync(operation, "The original Slack provenance is unavailable.", ct);
            return;
        }

        if (string.Equals(operation.AttemptKind, "root", StringComparison.Ordinal))
        {
            var agent = await _agents.GetByIdAsync(operation.ProjectId, connection.AgentId, ct);
            if (agent is null)
            {
                await CompleteUnavailableAsync(operation, "The Agent bound to this Connection is unavailable.", ct);
                return;
            }
            var launch = await _launcher.LaunchConnectionRetryAsync(
                agent,
                retrySource.Input.Text,
                new ConnectionLaunchOrigin(
                    connection.Id,
                    provenance.WorkspaceId,
                    provenance.MemberId,
                    provenance.ConversationId,
                    provenance.MessageId,
                    provenance.ThreadId,
                    provenance.OriginalDirectMessage),
                operation.RetryDispatchKey,
                retrySource.Input.Attachments,
                operation.PreMintedSessionId,
                operation.PreMintedInputId,
                operation.PreMintedTurnId,
                ct);
            var completed = await _operations.CompleteAsync(
                operation.ProjectId,
                operation.ActionKey,
                SlackRetryOperationOutcomes.Accepted,
                null,
                launch.SessionId,
                launch.InputId,
                launch.TurnId,
                ct);
            if (completed is not null)
                await PresentAcceptedAsync(connection, operation, completed, ct);
            return;
        }

        var accepted = await source.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: retrySource.Input.Text,
            Source: "slack-retry",
            IdempotencyKey: operation.RetryDispatchKey,
            Attachments: retrySource.Input.Attachments,
            PreMintedInputId: operation.PreMintedInputId,
            PreMintedTurnId: operation.PreMintedTurnId,
            AssignmentMode: AgentSessionFollowupAssignmentMode.ForceNewTurnForRetry,
            PreMintedOperationId: operation.FollowupOperationId,
            Provenance: provenance));
        await _operations.RecordAdmissionAsync(
            operation.ProjectId,
            operation.ActionKey,
            accepted.InputId,
            accepted.TurnId,
            accepted.OperationId,
            ct);
        if (!await _followups.DispatchAsync(
                operation.ProjectId,
                operation.SessionId,
                operation.FollowupOperationId ?? accepted.OperationId,
                ct))
            return;

        var followupCompleted = await _operations.CompleteAsync(
            operation.ProjectId,
            operation.ActionKey,
            SlackRetryOperationOutcomes.Accepted,
            null,
            operation.SessionId,
            accepted.InputId,
            accepted.TurnId,
            ct);
        if (followupCompleted is not null)
            await PresentAcceptedAsync(connection, operation, followupCompleted, ct);
    }

    private async Task<SlackRetryOperationRow?> CompleteUnavailableAsync(
        SlackRetryOperationRow operation,
        string reason,
        CancellationToken ct)
    {
        var completed = await _operations.CompleteAsync(
            operation.ProjectId,
            operation.ActionKey,
            SlackRetryOperationOutcomes.Unavailable,
            reason,
            null,
            null,
            null,
            ct);
        if (completed is not null)
            await PresentOutcomeAsync(completed, ct);
        return completed;
    }

    private async Task PresentOutcomeAsync(
        SlackRetryOperationRow operation,
        CancellationToken ct)
    {
        var text = operation.ResultReason
            ?? "This Retry action is no longer available.";
        await _outbox.UpsertRequiredAsync(new SlackOutboxDraft(
            operation.ProjectId,
            operation.ConnectionId,
            operation.WorkspaceTeamId,
            operation.ConversationId,
            SlackOutboxKinds.UserAction,
            SlackRetryOperationStore.ResultReference(operation.ActionKey),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.ChatUpdate,
                text,
                ProviderMessageIdentity: new SlackProviderMessageIdentity(
                    operation.ConversationId,
                    operation.MessageTs),
                Blocks: PresentationBlocks(text))),
            operation.ThreadTs), ct);
    }

    private static bool MatchesOperation(
        AgentSessionRetrySource source,
        SlackRetryOperationRow operation)
    {
        var provenance = source.Input.Provenance;
        return provenance is not null
            && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
            && string.Equals(provenance.ConnectionId, operation.ConnectionId, StringComparison.Ordinal)
            && string.Equals(provenance.WorkspaceId, operation.WorkspaceTeamId, StringComparison.Ordinal)
            && string.Equals(provenance.ConversationId, operation.ConversationId, StringComparison.Ordinal)
            && string.Equals(provenance.MessageId, operation.MessageTs, StringComparison.Ordinal)
            && string.Equals(provenance.ThreadId, operation.ThreadTs, StringComparison.Ordinal)
            && string.Equals(provenance.MemberId, operation.ActorSlackUserId, StringComparison.Ordinal)
            && provenance.OriginalDirectMessage == operation.OriginalDirectMessage;
    }

    private static JsonElement PresentationBlocks(string text) =>
        JsonSerializer.SerializeToElement(new object[]
        {
            new
            {
                type = "section",
                text = new { type = "mrkdwn", text },
            },
        });

    private async Task PresentAcceptedAsync(
        Agent.Domain.AgentConnection connection,
        SlackRetryOperationRow operation,
        SlackRetryOperationRow completed,
        CancellationToken ct)
    {
        JsonElement? blocks = null;
        if (completed.ResultSessionId is not null
            && completed.ResultInputId is not null
            && completed.ResultTurnId is not null)
        {
            var stop = await _controls.CreateStopActionAsync(
                connection,
                completed.ResultSessionId,
                completed.ResultTurnId,
                completed.ResultInputId,
                operation.DispatchRef,
                operation.ActorSlackUserId,
                new SlackMessageIdentity(operation.WorkspaceTeamId, operation.ConversationId, operation.MessageTs),
                operation.ThreadTs,
                operation.OriginalDirectMessage,
                ct);
            blocks = stop?.Blocks;
        }

        await _outbox.UpsertRequiredAsync(new SlackOutboxDraft(
            operation.ProjectId,
            operation.ConnectionId,
            operation.WorkspaceTeamId,
            operation.ConversationId,
            SlackOutboxKinds.UserAction,
            SlackRetryOperationStore.ResultReference(operation.ActionKey),
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.ChatUpdate,
                "Retry accepted and queued a fresh attempt.",
                ProviderMessageIdentity: new SlackProviderMessageIdentity(
                    operation.ConversationId,
                    operation.MessageTs),
                Blocks: blocks)),
            operation.ThreadTs), ct);
    }
}
