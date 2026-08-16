using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Infrastructure.PublicApi;

public sealed partial class PublicApiProjectionEngine
{
    private static async Task<List<PublicProjectionCheckpointRow>> LoadCheckpointRowsAsync(MohistDbContext db, CancellationToken ct) =>
        await db.PublicProjectionCheckpoints.ToListAsync(ct);

    private sealed record JournalHead(string Source, long MaxId);

    private static async Task<List<JournalHead>> LoadJournalHeadsAsync<T>(IQueryable<T> rows, CancellationToken ct)
        where T : IEventRow =>
        (await rows
            .GroupBy(row => row.Source)
            .Select(group => new JournalHeadSql(group.Key, group.Max(row => row.Id)))
            .ToListAsync(ct))
            .Select(head => new JournalHead(head.Source, head.MaxId))
            .ToList();

    private sealed record JournalHeadSql(string Source, long MaxId);

    private static bool IsJournalBehind(
        Dictionary<string, string> checkpoints,
        string feed,
        string source,
        long maxId) =>
        !checkpoints.TryGetValue(CheckpointKey(feed, source), out var watermark)
        || !long.TryParse(watermark, out var consumed)
        || consumed < maxId;

    private static void AdvanceCheckpoint(
        Dictionary<string, string> checkpoints,
        string feed,
        string sourceKey,
        string watermark)
    {
        checkpoints[CheckpointKey(feed, sourceKey)] = watermark;
    }

    private static async Task<Dictionary<string, long>> LoadJobJournalHeadsAsync(
        MohistDbContext db,
        IReadOnlyList<AgentJobRow> jobRows,
        CancellationToken ct)
    {
        if (jobRows.Count == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var sources = jobRows.Select(row => AgentJobSource(row.JobKey)).ToList();
        var heads = await db.AgentJobEvents.AsNoTracking()
            .Where(row => sources.Contains(row.Source))
            .GroupBy(row => row.Source)
            .Select(group => new { group.Key, Max = group.Max(row => row.Id) })
            .ToListAsync(ct);
        return heads.ToDictionary(head => head.Key, head => head.Max, StringComparer.Ordinal);
    }

    private static void AdvanceSessionCheckpoints(
        Dictionary<string, string> checkpoints,
        AgentSessionRow sessionRow,
        IReadOnlyList<AgentJobRow> jobRows,
        IReadOnlyList<AgentSessionEventRow> journalRows,
        IReadOnlyDictionary<string, long> jobJournalHeads,
        IReadOnlyList<AgentSessionLifecycleTransitionRow> lifecycleRows)
    {
        AdvanceCheckpoint(
            checkpoints,
            PublicProjectionFeeds.AgentSessions,
            sessionRow.Id,
            PublicExecutionAggregator.StateDigest(sessionRow.State));

        if (journalRows.Count > 0)
        {
            AdvanceCheckpoint(
                checkpoints,
                PublicProjectionFeeds.AgentSessionEvents,
                AgentSessionSource(sessionRow.Id),
                journalRows.Max(row => row.Id).ToString());
        }

        if (lifecycleRows.Count > 0)
        {
            AdvanceCheckpoint(
                checkpoints,
                PublicProjectionFeeds.AgentSessionLifecycle,
                sessionRow.Id,
                lifecycleRows.Max(row => row.Id).ToString());
        }

        foreach (var jobRow in jobRows)
        {
            AdvanceCheckpoint(
                checkpoints,
                PublicProjectionFeeds.AgentJobs,
                jobRow.JobKey,
                RevisionWatermark(jobRow.Revision));

            // The joined Job's journal rows are part of the consumed
            // input for this target; checkpointing their head is what
            // lets the target settle instead of staying forever dirty.
            if (jobJournalHeads.TryGetValue(AgentJobSource(jobRow.JobKey), out var head))
            {
                AdvanceCheckpoint(
                    checkpoints,
                    PublicProjectionFeeds.AgentJobEvents,
                    AgentJobSource(jobRow.JobKey),
                    head.ToString());
            }
        }
    }

    /// <summary>
    /// Stages the in-memory checkpoint watermark map onto the change
    /// tracker so the checkpoints commit in the same transaction as
    /// the snapshots, journal entries, and sequence allocations they
    /// prove.
    /// </summary>
    private static void StageCheckpoints(
        MohistDbContext db,
        List<PublicProjectionCheckpointRow> trackedRows,
        Dictionary<string, string> checkpoints,
        DateTimeOffset updatedAt)
    {
        foreach (var entry in checkpoints)
        {
            var separator = entry.Key.IndexOf('\u001f');
            var feed = entry.Key[..separator];
            var sourceKey = entry.Key[(separator + 1)..];
            var existing = trackedRows.FirstOrDefault(
                row => row.Feed == feed && row.SourceKey == sourceKey);
            if (existing is null)
            {
                var added = new PublicProjectionCheckpointRow
                {
                    Feed = feed,
                    SourceKey = sourceKey,
                    Watermark = entry.Value,
                    UpdatedAt = updatedAt,
                };
                db.PublicProjectionCheckpoints.Add(added);
                trackedRows.Add(added);
            }
            else
            {
                existing.Watermark = entry.Value;
                existing.UpdatedAt = updatedAt;
            }
        }
    }

    private static async Task SafeRollbackAsync(IDbContextTransaction transaction, CancellationToken ct)
    {
        try
        {
            await transaction.RollbackAsync(ct);
        }
        catch
        {
            // The transaction is already gone — the discard outcome the
            // caller needs either way.
        }
    }
}
