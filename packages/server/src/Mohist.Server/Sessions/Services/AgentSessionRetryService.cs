using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Orleans;

namespace Mohist.Server.Sessions.Services;

public enum AgentSessionRetryOutcome
{
    Accepted,
    AcceptedPending,
    Finished,
    NoLongerRetryable,
}

public sealed record AgentSessionRetryResult(
    AgentSessionRetryOutcome Outcome,
    string? OperationId = null,
    string? ResultText = null,
    string? SessionId = null,
    string? JobKey = null,
    string? InputId = null,
    string? TurnId = null)
{
    public bool IsAccepted => Outcome is AgentSessionRetryOutcome.Accepted
        or AgentSessionRetryOutcome.AcceptedPending
        or AgentSessionRetryOutcome.Finished;
}

public sealed record AgentSessionRetryCommand(
    string ProjectId,
    string SessionId,
    string TurnId,
    string IdempotencyKey);

/// <summary>
/// Provider-independent retry application service. It validates the current
/// immutable failure facts, commits the retry receipt, and only then enters an
/// existing launch pipeline.
/// </summary>
public sealed class AgentSessionRetryService : IScopedService
{
    private const string AcceptedText = "Retry attempt accepted.";
    private const string PendingText = "Retry attempt accepted and is pending dispatch.";

    private readonly AgentSessionQuery _sessions;
    private readonly AgentQuerier _agents;
    private readonly AgentRetryOperationStore _operations;
    private readonly IAgentLauncher _launcher;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly IGrainFactory _grains;

    public AgentSessionRetryService(
        AgentSessionQuery sessions,
        AgentQuerier agents,
        AgentRetryOperationStore operations,
        IAgentLauncher launcher,
        AgentSessionFollowupDispatcher followups,
        IGrainFactory grains)
    {
        _sessions = sessions;
        _agents = agents;
        _operations = operations;
        _launcher = launcher;
        _followups = followups;
        _grains = grains;
    }

    public Task<AgentSessionRetryResult> RetryAsync(
        string projectId,
        string sessionId,
        string turnId,
        string idempotencyKey,
        CancellationToken ct = default) =>
        RetryAsync(new AgentSessionRetryCommand(projectId, sessionId, turnId, idempotencyKey), ct);

    public async Task<AgentSessionRetryResult> RetryAsync(
        AgentSessionRetryCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey);

        // A committed receipt is authoritative for redelivery. Read it before
        // re-evaluating the target so a later click still returns the recorded
        // result even if the failed Session has since been compacted or its
        // facts have been superseded.
        var existing = await _operations.FindExistingAsync(
            command.ProjectId,
            command.SessionId,
            command.TurnId,
            command.IdempotencyKey,
            ct);
        if (existing is not null)
            return RenderRecorded(existing);

        var target = await ReadTargetAsync(command, ct);
        if (target is null || target.Turn is null || target.Turn.Status != AgentTurnStatus.Failed
            || !AgentSessionRetryPolicy.IsRetryable(target.Turn.Result?.FailureCategory))
        {
            return new AgentSessionRetryResult(AgentSessionRetryOutcome.NoLongerRetryable);
        }

        var kind = IsLaunchTurn(target.Turn)
            ? AgentRetryOperationKind.Root
            : AgentRetryOperationKind.Thread;
        var claim = await _operations.ClaimOrCreateAsync(
            command.ProjectId,
            command.SessionId,
            command.TurnId,
            command.IdempotencyKey,
            kind,
            preAllocatedSessionId: NewExecutionId("agent-session"),
            preAllocatedInputId: NewExecutionId("input"),
            preAllocatedTurnId: NewExecutionId("turn"),
            ct: ct);

        if (claim.AlreadyExists)
            return RenderRecorded(claim.Operation);

        try
        {
            var result = await DispatchPendingAsync(claim.Operation, target, ct);
            await _operations.MarkFinishedAsync(
                claim.Operation.OperationId,
                resultState: "accepted",
                resultText: AcceptedText,
                result.JobKey,
                result.SessionId,
                result.InputId,
                result.TurnId,
                ct: ct);
            return new AgentSessionRetryResult(
                AgentSessionRetryOutcome.Finished,
                claim.Operation.OperationId,
                AcceptedText,
                result.SessionId,
                result.JobKey,
                result.InputId,
                result.TurnId);
        }
        catch (Exception) when (claim.Operation.IsPending)
        {
            // The receipt is intentionally left Pending. A later redelivery or
            // recovery worker can replay the same pre-allocated launch.
            return RenderPending(claim.Operation);
        }
    }

    /// <summary>
    /// Re-dispatches a committed pending receipt. The database is checked
    /// again so an in-memory or fabricated operation can never reach launch.
    /// </summary>
    public async Task<AgentSessionRetryResult> DispatchPendingAsync(
        string projectId,
        string operationId,
        CancellationToken ct = default)
    {
        var operation = await _operations.GetAsync(projectId, operationId, ct)
            ?? throw new InvalidOperationException($"Retry operation '{operationId}' was not found.");
        if (!operation.IsPending)
            return RenderRecorded(operation);

        var target = await ReadTargetAsync(
            new AgentSessionRetryCommand(operation.ProjectId, operation.SessionId, operation.TurnId, operation.IdempotencyKey), ct)
            ?? throw new InvalidOperationException("Retry target disappeared before pending dispatch.");
        var result = await DispatchPendingAsync(operation, target, ct);
        await _operations.MarkFinishedAsync(
            operation.OperationId,
            "accepted",
            AcceptedText,
            result.JobKey,
            result.SessionId,
            result.InputId,
            result.TurnId,
            ct: ct);
        return new AgentSessionRetryResult(
            AgentSessionRetryOutcome.Finished,
            operation.OperationId,
            AcceptedText,
            result.SessionId,
            result.JobKey,
            result.InputId,
            result.TurnId);
    }

    private async Task<RetryDispatchResult> DispatchPendingAsync(
        AgentRetryOperation operation,
        RetryTarget target,
        CancellationToken ct)
    {
        if (!operation.IsPending)
            throw new InvalidOperationException($"Retry operation '{operation.OperationId}' is not pending.");
        if (target.Turn is null || target.Turn.Status != AgentTurnStatus.Failed
            || !AgentSessionRetryPolicy.IsRetryable(target.Turn.Result?.FailureCategory))
            throw new InvalidOperationException("Retry target is no longer retryable.");

        var input = target.Inputs.FirstOrDefault(candidate => target.Turn.InputIds.Contains(candidate.Id))
            ?? throw new InvalidOperationException("Retry target input was not found.");

        if (operation.Kind == AgentRetryOperationKind.Thread)
        {
            var grain = _grains.GetGrain<IAgentSessionGrain>(target.Session.Id);
            var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: input.Text,
                Source: input.Source,
                IdempotencyKey: $"agent-retry:{operation.OperationId}",
                PreMintedInputId: operation.PreAllocatedInputId,
                PreMintedTurnId: operation.PreAllocatedTurnId,
                Provenance: input.Provenance,
                ForceNewTurn: true));

            // A busy Session deliberately returns no dispatch here. The
            // accepted turn remains queued and the ordinary scheduler will
            // select it in order after the executing turn ends. When idle,
            // the targeted call cannot select an unrelated queued turn.
            await _followups.DispatchForTurnAsync(
                target.ProjectId,
                target.Session.Id,
                accepted.TurnId,
                ct);

            return new RetryDispatchResult(
                SessionId: target.Session.Id,
                JobKey: null,
                InputId: accepted.InputId,
                TurnId: accepted.TurnId);
        }

        var provenance = input.Provenance
            ?? throw new InvalidOperationException("Retry target has no durable Connection provenance.");
        var agentId = target.Session.Metadata.Label(GenericAgentSessionMetadata.AgentId);
        if (string.IsNullOrWhiteSpace(agentId))
            throw new InvalidOperationException("Retry target has no durable Agent identity.");
        var agent = await _agents.GetByIdAsync(target.ProjectId, agentId, ct)
            ?? throw new InvalidOperationException($"Agent '{agentId}' was not found.");
        var connectionId = provenance.ConnectionId
            ?? throw new InvalidOperationException("Retry target has no durable Connection identity.");
        var definition = target.Session.Settings.Definition
            ?? throw new InvalidOperationException("Retry target has no durable execution definition.");
        var startup = target.Session.Settings.AgentSessionStartup is { } recordedStartup
            ? recordedStartup with
            {
                SessionId = operation.PreAllocatedSessionId,
                SpawnCommand = recordedStartup.SpawnCommand.Replace(
                    $"--parent-session {recordedStartup.SessionId}",
                    $"--parent-session {operation.PreAllocatedSessionId}",
                    StringComparison.Ordinal),
            }
            : null;

        var origin = new ConnectionLaunchOrigin(
            connectionId,
            provenance.WorkspaceId,
            provenance.MemberId,
            provenance.ConversationId,
            provenance.MessageId,
            provenance.ThreadId,
            provenance.OriginMarker);
        var launch = await _launcher.LaunchConnectionAsync(
            agent,
            input.Text,
            origin,
            workspaceName: target.Session.Metadata.Label(GenericAgentSessionMetadata.WorkspaceName),
            startupContext: input.StartupContext,
            attachments: input.Attachments,
            attachmentIds: input.Attachments?.Select(attachment => attachment.Id).ToArray(),
            preMintedSessionId: operation.PreAllocatedSessionId,
            preMintedInputId: operation.PreAllocatedInputId,
            preMintedTurnId: operation.PreAllocatedTurnId,
            idempotencyKeyOverride: $"agent-retry:{operation.OperationId}",
            definitionOverride: definition,
            agentSessionStartup: startup,
            skipLaunchability: true,
            ct: ct);
        return new RetryDispatchResult(launch.SessionId, launch.JobKey, launch.InputId, launch.TurnId);
    }

    private async Task<RetryTarget?> ReadTargetAsync(
        AgentSessionRetryCommand command,
        CancellationToken ct)
    {
        var record = (await _sessions.ListByIdsAsync([command.SessionId], ct)).FirstOrDefault();
        if (record is null
            || !string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), command.ProjectId, StringComparison.Ordinal))
            return null;

        var grain = _grains.GetGrain<IAgentSessionGrain>(command.SessionId);
        var turns = await grain.ListTurnsAsync();
        var inputs = await grain.ListInputsAsync();
        var turn = turns.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.TurnId, StringComparison.Ordinal));
        return new RetryTarget(command.ProjectId, record.Session, turn, inputs);
    }

    private static bool IsLaunchTurn(AgentTurnRecord turn) => !string.IsNullOrWhiteSpace(turn.JobId);

    private static string NewExecutionId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static AgentSessionRetryResult RenderRecorded(AgentRetryOperation operation)
    {
        if (operation.State == AgentRetryOperationState.Pending)
            return RenderPending(operation);
        return new AgentSessionRetryResult(
            AgentSessionRetryOutcome.Finished,
            operation.OperationId,
            operation.ResultText,
            operation.ResultSessionId,
            operation.ResultJobKey,
            operation.ResultInputId,
            operation.ResultTurnId);
    }

    private static AgentSessionRetryResult RenderPending(AgentRetryOperation operation) =>
        new(
            AgentSessionRetryOutcome.AcceptedPending,
            operation.OperationId,
            PendingText,
            operation.PreAllocatedSessionId,
            null,
            operation.PreAllocatedInputId,
            operation.PreAllocatedTurnId);

    private sealed record RetryTarget(
        string ProjectId,
        AgentSession Session,
        AgentTurnRecord? Turn,
        IReadOnlyList<AgentSessionInputRecord> Inputs);

    private sealed record RetryDispatchResult(
        string SessionId,
        string? JobKey,
        string InputId,
        string TurnId);
}
