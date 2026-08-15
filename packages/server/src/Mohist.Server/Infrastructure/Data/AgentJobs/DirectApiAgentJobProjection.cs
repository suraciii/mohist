using Mohist.Server.Infrastructure;

namespace Mohist.Server.Infrastructure.Data.AgentJobs;

/// <summary>
/// Strict public snapshot stored beside an AgentJob ledger row. Keeping this
/// separate from the canonical state JSON makes the direct route's disclosure
/// boundary durable and reviewable.
/// </summary>
public sealed record DirectApiAgentJobProjection(
    string ProjectId,
    string? AgentId,
    string JobId,
    string Status,
    string? Outcome,
    string? ReasonCode,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? TerminalAt,
    DateTimeOffset ObservedAt,
    long SourceRevision)
{
    public static DirectApiAgentJobProjection? Create(
        string jobId,
        string stateJson,
        long sourceRevision,
        DateTimeOffset observedAt)
    {
        var state = JSON.Deserialize<ProjectionSourceState>(stateJson);
        var input = state?.Input;
        if (string.IsNullOrWhiteSpace(input?.ProjectId))
            return null;

        var source = state!;
        var (status, outcome, reasonCode) = source.Status?.ToLowerInvariant() switch
        {
            null or "" or "pending" => ("queued", null, null),
            "running" => ("running", null, null),
            "completed" => ("terminal", "completed", null),
            "failed" => ("terminal", "failed", "failed"),
            "cancelled" => ("terminal", "cancelled", "cancelled"),
            _ => ("unknown", null, "unconfirmed"),
        };

        return new DirectApiAgentJobProjection(
            input.ProjectId,
            input.AgentId,
            jobId,
            status,
            outcome,
            reasonCode,
            source.SubmittedAt,
            source.RunningSince,
            source.TerminalAt,
            observedAt,
            sourceRevision);
    }

    // Persistence only knows the allowlisted JSON fields it needs to project.
    private sealed record ProjectionSourceState(
        string? Status,
        ProjectionSourceInput? Input,
        DateTimeOffset? SubmittedAt,
        DateTimeOffset? RunningSince,
        DateTimeOffset? TerminalAt);

    private sealed record ProjectionSourceInput(string? ProjectId, string? AgentId);
}
