using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Domain;

public static partial class AgentSessionExtensions
{
    /// <summary>
    /// Appends a thinned <see cref="ContextUsageHistoryEntry"/> to
    /// <paramref name="history"/>. Behaviour:
    /// <list type="bullet">
    ///   <item><description>returns <paramref name="history"/> unchanged
    ///   when <paramref name="contextWindowUsed"/> or
    ///   <paramref name="contextWindowSize"/> cannot produce a finite
    ///   0..100 % (mirrors <see cref="AgentSessionJsonHelper.ContextUsagePercent"/>);</description></item>
    ///   <item><description>coalesces with the last entry when it falls
    ///   inside the same <see cref="ContextUsageHistoryBucket"/> time
    ///   window (last-wins) so back-to-back usage updates don't drown
    ///   the long-run trend;</description></item>
    ///   <item><description>truncates to the most recent
    ///   <see cref="ContextUsageHistoryCap"/> samples so the history
    ///   cannot grow unbounded regardless of session length (bounded
    ///   payload).</description></item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<ContextUsageHistoryEntry>? AppendUsageHistorySample(
        IReadOnlyList<ContextUsageHistoryEntry>? history,
        long? contextWindowUsed,
        long? contextWindowSize,
        DateTime now)
    {
        if (history is null) return null;

        var percent = AgentSessionJsonHelper.ContextUsagePercent(contextWindowUsed, contextWindowSize);
        if (percent is null) return history;

        var entries = new List<ContextUsageHistoryEntry>(history.Count + 1);
        entries.AddRange(history);

        var lastBucket = GetHistoryBucket(entries.Count > 0 ? entries[^1].At : (DateTime?)null);
        var nowBucket = GetHistoryBucket(now);

        if (entries.Count > 0 && lastBucket == nowBucket)
        {
            entries[^1] = new ContextUsageHistoryEntry(now, percent.Value);
        }
        else
        {
            entries.Add(new ContextUsageHistoryEntry(now, percent.Value));
        }

        if (entries.Count > ContextUsageHistoryCap)
        {
            entries.RemoveRange(0, entries.Count - ContextUsageHistoryCap);
        }

        return entries;
    }

    private static long GetHistoryBucket(DateTime? at) =>
        at is null
            ? long.MinValue
            : at.Value.Ticks / ContextUsageHistoryBucket.Ticks;
}
