using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Maintains the per-work execution confirmations a Runner sends with its poll.
///
/// The owner ledger answers "who owns this work"; it cannot answer "is that
/// owner still executing it", because a retained ledger row and a continuing
/// presence heartbeat look identical whether the Runner is executing the work or
/// not. Only a poll that names the work key is a statement about that work, so
/// the confirmation carries the poll receipt time as its own source time and the
/// process generation that made the statement.
///
/// Reads never renew a confirmation, and entries the Runner stopped naming keep
/// their original time until they age out, so a read surface cannot manufacture
/// fresh execution evidence.
/// </summary>
internal static class RunnerWorkConfirmationLedger
{
    /// <summary>
    /// How long a confirmation the Runner stopped naming is retained. It is twice
    /// the evidence freshness window, so evidence that a read would already
    /// refuse as aged stays reportable (with its real time) instead of being
    /// silently dropped, while the ledger stays bounded by the slots a Runner can
    /// hold.
    /// </summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    internal static IReadOnlyList<RunnerWorkConfirmation> Renew(
        IReadOnlyList<RunnerWorkConfirmation>? previous,
        IReadOnlyList<string>? reportedWorkKeys,
        string processGeneration,
        DateTimeOffset now)
    {
        var generation = processGeneration?.Trim() ?? string.Empty;
        var renewed = new List<RunnerWorkConfirmation>();
        var renewedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var workKey in reportedWorkKeys ?? [])
        {
            var key = workKey?.Trim();
            if (string.IsNullOrEmpty(key) || !renewedKeys.Add(key))
                continue;

            renewed.Add(new RunnerWorkConfirmation(key, now, generation));
        }

        foreach (var entry in previous ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.WorkKey)
                || renewedKeys.Contains(entry.WorkKey)
                || now - entry.ConfirmedAt > Retention)
                continue;

            renewed.Add(entry);
        }

        return renewed;
    }

    internal static DateTimeOffset? ConfirmedAt(
        RunnerActiveWorkItem work,
        IReadOnlyList<RunnerWorkConfirmation>? confirmations,
        string? processGeneration)
    {
        if (confirmations is null || confirmations.Count == 0)
            return null;

        var key = WorkDispatchKeys.WorkKey(work.OwnerKind, work.OwnerId, work.WorkId);
        foreach (var entry in confirmations)
        {
            if (!string.Equals(entry.WorkKey, key, StringComparison.Ordinal))
                continue;

            // A confirmation belongs to the process that made it. A process that
            // has been replaced cannot confirm work the new one owns.
            return string.Equals(entry.ProcessGeneration, processGeneration, StringComparison.Ordinal)
                ? entry.ConfirmedAt
                : null;
        }

        return null;
    }

    internal static IReadOnlyList<RunnerActiveWorkItem> Stamp(
        IReadOnlyList<RunnerActiveWorkItem> works,
        IReadOnlyList<RunnerWorkConfirmation>? confirmations,
        string? processGeneration)
    {
        if (works.Count == 0 || confirmations is null || confirmations.Count == 0)
            return works;

        List<RunnerActiveWorkItem>? stamped = null;
        for (var index = 0; index < works.Count; index++)
        {
            var work = works[index];
            var confirmedAt = ConfirmedAt(work, confirmations, processGeneration);
            if (confirmedAt is null || work.ConfirmedAt == confirmedAt)
            {
                stamped?.Add(work);
                continue;
            }

            stamped ??= [.. works.Take(index)];
            stamped.Add(work with { ConfirmedAt = confirmedAt });
        }

        return stamped ?? works;
    }
}
