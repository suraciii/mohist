using Orleans;

namespace Mohist.Server.Contracts;

public static class AgentWorkInterruptionStates
{
    public const string Interrupting = "interrupting";
    public const string Interrupted = "interrupted";
    public const string Recovering = "recovering";
    public const string Recovered = "recovered";

    public static bool IsKnown(string? state) => state is
        Interrupting or Interrupted or Recovering or Recovered;
}

/// <summary>
/// Durable, additive visibility fact for work named by a Runner update
/// operation. The identity is the work id plus recovery generation; a later
/// generation is a replacement attempt, never a mutation of the old turn.
/// </summary>
[GenerateSerializer]
public sealed record AgentWorkInterruptionTransition(
    [property: Id(0)] string State,
    [property: Id(1)] string UpdateOperationId,
    [property: Id(2)] string WorkId,
    [property: Id(3)] string? TaskRunId,
    [property: Id(4)] int RecoveryGeneration,
    [property: Id(5)] string? OriginalTurnId,
    [property: Id(6)] string? ReplacementTurnId,
    [property: Id(7)] string? StopFailure,
    [property: Id(8)] string ExpectedRecoveryPath,
    [property: Id(9)] DateTimeOffset RecordedAt)
{
    public string IdentityKey => $"{WorkId}\u001f{RecoveryGeneration}";
}

/// <summary>
/// Pure projection for interruption visibility. Replayed events and duplicate
/// receipts replace only an older state for the same identity; a later state
/// can never move a work backwards or create a second transition row.
/// </summary>
public static class AgentWorkInterruptionProjection
{
    public static IReadOnlyList<AgentWorkInterruptionTransition> Apply(
        IReadOnlyList<AgentWorkInterruptionTransition>? current,
        AgentWorkInterruptionTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!AgentWorkInterruptionStates.IsKnown(transition.State))
            throw new ArgumentException($"Unknown interruption state '{transition.State}'.", nameof(transition));
        if (string.IsNullOrWhiteSpace(transition.UpdateOperationId)
            || string.IsNullOrWhiteSpace(transition.WorkId)
            || transition.RecoveryGeneration < 0)
        {
            throw new ArgumentException("Interruption visibility requires operation, work, and generation identity.", nameof(transition));
        }

        var normalized = transition with { StopFailure = SanitizeStopFailure(transition.StopFailure) };
        var result = (current ?? []).ToList();
        var index = result.FindIndex(item =>
            string.Equals(item.WorkId, normalized.WorkId, StringComparison.Ordinal)
            && item.RecoveryGeneration == normalized.RecoveryGeneration);
        if (index < 0)
        {
            result.Add(normalized);
            return result;
        }

        var existing = result[index];
        if (Rank(normalized.State) <= Rank(existing.State))
            return result;

        result[index] = normalized;
        return result;
    }

    /// <summary>
    /// Stop adapters can fail while their transport is being torn down. Keep
    /// that implementation detail out of durable read models and replace it
    /// with an actionable, stable recovery explanation.
    /// </summary>
    public static string? SanitizeStopFailure(string? failure) =>
        string.IsNullOrWhiteSpace(failure)
            ? null
            : "The Runner could not confirm the stop before shutdown; the recorded recovery path remains active.";

    public static AgentWorkInterruptionTransition? Latest(
        IReadOnlyList<AgentWorkInterruptionTransition>? transitions,
        string? workId = null) =>
        (transitions ?? [])
            .Where(item => string.IsNullOrWhiteSpace(workId)
                || string.Equals(item.WorkId, workId, StringComparison.Ordinal))
            .OrderByDescending(item => item.RecoveryGeneration)
            .ThenByDescending(item => Rank(item.State))
            .ThenByDescending(item => item.RecordedAt)
            .FirstOrDefault();

    public static int Rank(string state) => state switch
    {
        AgentWorkInterruptionStates.Interrupting => 1,
        AgentWorkInterruptionStates.Interrupted => 2,
        AgentWorkInterruptionStates.Recovering => 3,
        AgentWorkInterruptionStates.Recovered => 4,
        _ => 0,
    };
}
