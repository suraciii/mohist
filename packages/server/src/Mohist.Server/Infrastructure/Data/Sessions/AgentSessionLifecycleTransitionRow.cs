using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.Data.Sessions;

/// <summary>
/// Durable history of public-relevant Session lifecycle transitions. The
/// canonical Session row is mutable, so the public projector consumes these
/// rows to preserve transitions that occur between projection sweeps.
/// </summary>
public sealed class AgentSessionLifecycleTransitionRow
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string SourceTransition { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string AnchorKind { get; set; } = string.Empty;
    public string AnchorId { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// The canonical facts needed to reconstruct one public lifecycle event.
/// Prompt, runtime, workspace, and other private Session fields are
/// deliberately absent; the snapshot is never served directly.
/// </summary>
internal sealed record AgentSessionLifecycleSnapshot(
    AgentSessionActivity Activity,
    bool PendingStopActive,
    bool PendingResetActive,
    IReadOnlyList<AgentSessionLifecycleInput> Inputs,
    IReadOnlyList<AgentSessionLifecycleTurn> Turns,
    IReadOnlyList<AgentSessionLifecycleJob> Jobs);

internal sealed record AgentSessionLifecycleInput(
    string InputId,
    AgentSessionInputAcceptance Acceptance,
    DateTimeOffset RecordedAt,
    string? JobId);

internal sealed record AgentSessionLifecycleTurn(
    string TurnId,
    AgentTurnStatus Status,
    IReadOnlyList<string> InputIds,
    string? JobId,
    DateTimeOffset? RecordedAt,
    DateTimeOffset? UpdatedAt,
    AgentTurnResult? Result);

internal sealed record AgentSessionLifecycleJob(
    string JobKey,
    AgentJobStatus Status,
    string? ProjectId,
    string? AgentId,
    string? SessionId,
    string? InitialInputId,
    string? InitialTurnId,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReadySince,
    DateTimeOffset? RunningSince,
    DateTimeOffset? TerminalAt,
    string? WaitingReason,
    AgentJobTerminalResult? TerminalResult);

internal sealed record AgentSessionLifecycleTransition(
    string SourceTransition,
    string EventType,
    string AnchorKind,
    string AnchorId,
    DateTimeOffset OccurredAt,
    string SnapshotJson);

internal static class AgentSessionLifecycleHistory
{
    internal const string InputAccepted = "input.accepted";
    internal const string InputRejected = "input.rejected";
    internal const string TurnQueued = "turn.queued";
    internal const string TurnRunning = "turn.running";
    internal const string TurnOutcomePending = "turn.outcome_pending";
    internal const string TurnTerminal = "turn.terminal";
    internal const string SessionUnknown = "session.unknown";

    public static AgentSessionLifecycleSnapshot Snapshot(
        AgentSession session,
        IReadOnlyList<AgentSessionLifecycleJob> jobs) =>
        new(
            session.Status.Activity,
            session.Status.PendingStop?.IsActive == true,
            session.Status.PendingReset is not null && session.Status.PendingReset.Outcome is null,
            (session.Status.Inputs ?? [])
                .Select(input => new AgentSessionLifecycleInput(
                    input.Id,
                    input.Acceptance,
                    ToUtc(input.RecordedAt),
                    input.JobId))
                .ToList(),
            (session.Status.Turns ?? [])
                .Select(turn => new AgentSessionLifecycleTurn(
                    turn.Id,
                    turn.Status,
                    turn.InputIds ?? [],
                    turn.JobId,
                    ToNullableUtc(turn.RecordedAt),
                    ToNullableUtc(turn.UpdatedAt),
                    turn.Result))
                .ToList(),
            jobs);

    public static IReadOnlyList<AgentSessionLifecycleTransition> Derive(
        AgentSession? previous,
        AgentSession current,
        IReadOnlyList<AgentSessionLifecycleJob> jobs,
        DateTimeOffset now)
    {
        var currentSnapshot = Snapshot(current, jobs);
        var previousSnapshot = previous is null ? null : Snapshot(previous, []);
        var transitions = new List<AgentSessionLifecycleTransition>();

        foreach (var input in currentSnapshot.Inputs)
        {
            var before = previousSnapshot?.Inputs.FirstOrDefault(item => item.InputId == input.InputId);
            if (input.Acceptance is not (AgentSessionInputAcceptance.Accepted or AgentSessionInputAcceptance.Rejected)
                || before?.Acceptance == input.Acceptance)
            {
                continue;
            }

            transitions.Add(Create(
                input.Acceptance == AgentSessionInputAcceptance.Accepted ? InputAccepted : InputRejected,
                "input",
                input.InputId,
                input.RecordedAt,
                currentSnapshot));
        }

        foreach (var turn in currentSnapshot.Turns)
        {
            var before = previousSnapshot?.Turns.FirstOrDefault(item => item.TurnId == turn.TurnId);
            var eventType = TurnEventType(turn.Status, currentSnapshot.PendingStopActive);
            var previousEventType = before is null
                ? null
                : TurnEventType(before.Status, previousSnapshot!.PendingStopActive);
            if (eventType is null
                || eventType == previousEventType
                || (before is not null && IsTerminal(before.Status)))
            {
                continue;
            }

            transitions.Add(Create(
                eventType,
                "turn",
                turn.TurnId,
                TurnTime(turn, now),
                currentSnapshot));
        }

        if (currentSnapshot.Activity == AgentSessionActivity.Unknown
            && previousSnapshot?.Activity != AgentSessionActivity.Unknown
            && currentSnapshot.Turns.All(turn => turn.Status != AgentTurnStatus.Unknown))
        {
            transitions.Add(Create(SessionUnknown, "session", current.Id, now, currentSnapshot));
        }

        return transitions;
    }

    private static AgentSessionLifecycleTransition Create(
        string eventType,
        string anchorKind,
        string anchorId,
        DateTimeOffset occurredAt,
        AgentSessionLifecycleSnapshot snapshot) =>
        new(
            $"session-lifecycle:{Guid.NewGuid():N}",
            eventType,
            anchorKind,
            anchorId,
            occurredAt,
            JsonSerializer.Serialize(snapshot, JSON.Options));

    private static string? TurnEventType(AgentTurnStatus status, bool pendingStopActive) => status switch
    {
        AgentTurnStatus.Queued => TurnQueued,
        AgentTurnStatus.Executing when pendingStopActive => TurnOutcomePending,
        AgentTurnStatus.Executing => TurnRunning,
        AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled => TurnTerminal,
        AgentTurnStatus.Unknown => SessionUnknown,
        _ => null,
    };

    private static bool IsTerminal(AgentTurnStatus status) =>
        status is AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled;

    private static DateTimeOffset TurnTime(AgentSessionLifecycleTurn turn, DateTimeOffset now) =>
        turn.UpdatedAt
        ?? turn.RecordedAt
        ?? now;

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());

    private static DateTimeOffset? ToNullableUtc(DateTime? value) =>
        value is null ? null : ToUtc(value.Value);
}
