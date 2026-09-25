using System.Text.Json;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
namespace Mohist.Server.Runner.Services;

/// <summary>
/// Classifies the current execution observation without changing canonical
/// Session or Turn state. A recent Session observation can establish a current
/// Turn, while a quiet Turn stays running only when the current Runner owner
/// snapshot confirms the same Session, Turn, and process generation.
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

        if (owner.Kind == OwnerMatchKind.Confirmed)
        {
            return new(
                "running",
                new ActivityExecutionEvidenceDto(
                    "owner-confirmed",
                    observedAt,
                    turn.Id,
                    session.Runtime.RunnerId));
        }

        if (turn.Status != AgentTurnStatus.Executing)
        {
            var reason = sourceAt is null ? "missing" : FreshnessReason(sourceAt.Value, observedAt);
            return Needs(reason, sourceAt, turn.Id, session.Runtime.RunnerId);
        }

        if (sourceAt is null)
            return Needs("missing", null, turn.Id, session.Runtime.RunnerId);

        var freshness = FreshnessReason(sourceAt.Value, observedAt);
        if (freshness == "fresh")
        {
            return new(
                "running",
                new ActivityExecutionEvidenceDto(
                    "activity",
                    sourceAt,
                    turn.Id,
                    session.Runtime.RunnerId));
        }

        return Needs(freshness, sourceAt, turn.Id, session.Runtime.RunnerId);
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
        var candidates = runner.ActiveWorks.Where(work =>
            (string.Equals(work.AgentSessionId, record.Session.Id, StringComparison.Ordinal)
                && string.Equals(work.AgentTurnId, turn.Id, StringComparison.Ordinal))
            || (binding is not null
                && string.Equals(work.AgentSessionId, record.Session.Id, StringComparison.Ordinal)
                && string.Equals(work.OwnerId, binding.WorkflowRunId, StringComparison.Ordinal)
                && string.Equals(work.WorkId, binding.WorkId, StringComparison.Ordinal)));
        var candidate = candidates.FirstOrDefault();
        if (candidate is null)
            return OwnerMatch.None;

        if (string.IsNullOrWhiteSpace(runner.ProcessGeneration)
            || string.IsNullOrWhiteSpace(candidate.ProcessGeneration))
            return OwnerMatch.None;

        return string.Equals(candidate.ProcessGeneration, runner.ProcessGeneration, StringComparison.Ordinal)
            ? OwnerMatch.ConfirmedMatch
            : OwnerMatch.GenerationMismatchMatch;
    }

    private enum OwnerMatchKind
    {
        None,
        Confirmed,
        GenerationMismatch,
    }

    private readonly record struct OwnerMatch(OwnerMatchKind Kind)
    {
        internal static OwnerMatch None => new(OwnerMatchKind.None);
        internal static OwnerMatch ConfirmedMatch => new(OwnerMatchKind.Confirmed);
        internal static OwnerMatch GenerationMismatchMatch => new(OwnerMatchKind.GenerationMismatch);
    }
}

internal sealed record ActivityExecutionAssessment(
    string ExecutionState,
    ActivityExecutionEvidenceDto Evidence);
