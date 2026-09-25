using System.Text.Json;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
namespace Mohist.Server.Runner.Services;

/// <summary>
/// Classifies the current execution observation without changing canonical
/// Session or Turn state. A recent Session observation can establish a current
/// Turn, while a quiet Turn stays running only when the current Runner owner
/// snapshot confirms the same Session, Turn, and process generation with a
/// freshness window of its own. Reading the status never renews that
/// confirmation: only a poll that names the work key does.
/// </summary>
internal static class ActivityExecutionEvidencePolicy
{
    internal static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    internal static ActivityExecutionAssessment Evaluate(
        AgentSessionRecord record,
        DateTimeOffset observedAt,
        RunnerStatusEntry? runner,
        DateTimeOffset? activityObservedAt = null,
        string? activityTurnId = null)
    {
        var session = record.Session;
        var turn = CurrentTurn(session);
        if (turn is null)
        {
            return session.Status.Activity == AgentSessionActivity.Active
                ? Needs("missing", observedAt: null, turnId: null, runnerId: session.Runtime.RunnerId)
                : NotRunning("idle", runnerId: session.Runtime.RunnerId);
        }

        if (turn.Status is AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled)
            return NotRunning("terminal", turn.Id, session.Runtime.RunnerId);

        var turnObservedAt = ToOffset(session.Status.LastDataAt);
        if (turn.Status == AgentTurnStatus.Queued)
        {
            return new(
                "queued",
                new ActivityExecutionEvidenceDto(
                    "queued",
                    ToOffset(turn.UpdatedAt) ?? turnObservedAt,
                    turn.Id,
                    session.Runtime.RunnerId));
        }

        var sourceAt = activityObservedAt ?? turnObservedAt;
        if (!string.IsNullOrWhiteSpace(activityTurnId)
            && !string.Equals(activityTurnId, turn.Id, StringComparison.Ordinal))
        {
            return Needs("superseded-generation", sourceAt ?? observedAt, turn.Id, session.Runtime.RunnerId);
        }

        var owner = FindOwner(record, turn, runner);
        if (owner.Kind == OwnerMatchKind.GenerationMismatch)
        {
            return Needs("superseded-generation", sourceAt ?? observedAt, turn.Id, session.Runtime.RunnerId);
        }

        // The owner confirmation is the Runner's direct statement about this
        // work, and it carries its own time. A stale or future confirmation
        // confirms nothing, but it stays the freshest known source.
        if (owner.ConfirmedAt is { } confirmedAt
            && FreshnessReason(confirmedAt, observedAt) == "fresh")
        {
            return new(
                "running",
                new ActivityExecutionEvidenceDto(
                    "owner-confirmed",
                    confirmedAt,
                    turn.Id,
                    session.Runtime.RunnerId));
        }

        if (turn.Status != AgentTurnStatus.Executing)
            return NeedsFreshest(observedAt, turn, session.Runtime.RunnerId, sourceAt, owner.ConfirmedAt);

        if (sourceAt is { } activityAt
            && FreshnessReason(activityAt, observedAt) == "fresh")
        {
            return new(
                "running",
                new ActivityExecutionEvidenceDto(
                    "activity",
                    activityAt,
                    turn.Id,
                    session.Runtime.RunnerId));
        }

        return NeedsFreshest(observedAt, turn, session.Runtime.RunnerId, sourceAt, owner.ConfirmedAt);
    }

    internal static AgentTurnRecord? CurrentTurn(AgentSession session) =>
        (session.Status.Turns ?? [])
            .OrderByDescending(turn => turn.Sequence)
            .FirstOrDefault(turn => turn.Status is AgentTurnStatus.Queued
                or AgentTurnStatus.Executing
                or AgentTurnStatus.Unknown
                or AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled);

    internal static string? ReadTurnId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            return payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("turnId", out var turnId)
                && turnId.ValueKind == JsonValueKind.String
                    ? turnId.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ActivityExecutionAssessment Needs(
        string reason,
        DateTimeOffset? observedAt,
        string? turnId,
        string? runnerId) =>
        new(
            "needs-verification",
            new ActivityExecutionEvidenceDto(reason, observedAt, turnId, runnerId));

    /// <summary>
    /// The freshest known source labels the needs-verification read. A
    /// confirmation the Runner stopped renewing is still better evidence of when
    /// this work was last observed than older Session data, and a source with no
    /// time at all reports as missing.
    /// </summary>
    private static ActivityExecutionAssessment NeedsFreshest(
        DateTimeOffset observedAt,
        AgentTurnRecord turn,
        string? runnerId,
        DateTimeOffset? activityAt,
        DateTimeOffset? confirmedAt)
    {
        var freshest = activityAt is { } activity
            && (confirmedAt is not { } confirmation || activity > confirmation)
                ? activityAt
                : confirmedAt;
        return freshest is { } sourceAt
            ? Needs(FreshnessReason(sourceAt, observedAt), sourceAt, turn.Id, runnerId)
            : Needs("missing", null, turn.Id, runnerId);
    }

    private static ActivityExecutionAssessment NotRunning(
        string reason,
        string? turnId = null,
        string? runnerId = null) =>
        new(
            "not-running",
            new ActivityExecutionEvidenceDto(reason, null, turnId, runnerId));

    private static DateTimeOffset? ToOffset(DateTime? value)
    {
        if (value is null || value.Value == default) return null;
        var utc = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private static string FreshnessReason(DateTimeOffset sourceAt, DateTimeOffset observedAt)
    {
        var age = observedAt - sourceAt;
        if (age < TimeSpan.Zero) return "future";
        return age <= FreshnessWindow ? "fresh" : "aged";
    }

    private static OwnerMatch FindOwner(
        AgentSessionRecord record,
        AgentTurnRecord turn,
        RunnerStatusEntry? runner)
    {
        if (runner is null
            || !string.Equals(runner.Identity.Id, record.Session.Runtime.RunnerId, StringComparison.Ordinal)
            || !string.Equals(runner.Presence.State, "online", StringComparison.Ordinal)
            || !string.Equals(runner.Control.State, "connected", StringComparison.Ordinal))
            return OwnerMatch.None;

        var binding = turn.WorkflowExecution;
        var workflowClaimsThisTurn = binding is not null
            && string.Equals(binding.AgentTurnId, turn.Id, StringComparison.Ordinal)
            && !AnotherLiveTurnClaims(record.Session, turn, binding);
        var candidates = runner.ActiveWorks.Where(work =>
            (string.Equals(work.AgentSessionId, record.Session.Id, StringComparison.Ordinal)
                && string.Equals(work.AgentTurnId, turn.Id, StringComparison.Ordinal))
            || (workflowClaimsThisTurn
                && string.Equals(work.AgentSessionId, record.Session.Id, StringComparison.Ordinal)
                && string.Equals(work.OwnerId, binding!.WorkflowRunId, StringComparison.Ordinal)
                && string.Equals(work.WorkId, binding!.WorkId, StringComparison.Ordinal)));
        var candidate = candidates.FirstOrDefault();
        if (candidate is null)
            return OwnerMatch.None;

        if (string.IsNullOrWhiteSpace(runner.ProcessGeneration)
            || string.IsNullOrWhiteSpace(candidate.ProcessGeneration))
            return OwnerMatch.None;

        if (!string.Equals(candidate.ProcessGeneration, runner.ProcessGeneration, StringComparison.Ordinal))
            return OwnerMatch.GenerationMismatch;

        // The owner ledger row says the work is owned; only the confirmation says
        // the Runner is still executing it, and only the confirmation's own time
        // is evidence. Whether that time is fresh is the caller's decision.
        return OwnerMatch.Matched(candidate.ConfirmedAt);
    }

    /// <summary>
    /// Two live turns of one Session must not share one Workflow work identity:
    /// the Runner confirms that work with a single work key, so the evidence
    /// cannot be attributed to either turn without guessing.
    /// </summary>
    private static bool AnotherLiveTurnClaims(
        AgentSession session,
        AgentTurnRecord turn,
        SessionWorkflowExecutionBinding binding) =>
        (session.Status.Turns ?? []).Any(other =>
            !string.Equals(other.Id, turn.Id, StringComparison.Ordinal)
            && other.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing or AgentTurnStatus.Unknown
            && other.WorkflowExecution is { } otherBinding
            && string.Equals(otherBinding.WorkflowRunId, binding.WorkflowRunId, StringComparison.Ordinal)
            && string.Equals(otherBinding.WorkId, binding.WorkId, StringComparison.Ordinal));

    private enum OwnerMatchKind
    {
        None,
        Matched,
        GenerationMismatch,
    }

    private readonly record struct OwnerMatch(OwnerMatchKind Kind, DateTimeOffset? ConfirmedAt = null)
    {
        internal static OwnerMatch None => new(OwnerMatchKind.None);
        internal static OwnerMatch Matched(DateTimeOffset? confirmedAt) =>
            new(OwnerMatchKind.Matched, confirmedAt);
        internal static OwnerMatch GenerationMismatch => new(OwnerMatchKind.GenerationMismatch);
    }
}

internal sealed record ActivityExecutionAssessment(
    string ExecutionState,
    ActivityExecutionEvidenceDto Evidence);
