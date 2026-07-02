using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.StagePopulation;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Read surface for the cumulative-flow diagram. The CFD renders the
/// daily stage-population snapshots produced by
/// <c>StagePopulationSnapshotService</c>; this querier is the additive
/// read path that turns the persisted snapshot table into the
/// <see cref="CumulativeFlowResult"/> the endpoint serializes.
/// <para>
/// The trailing window is a single fixed 90-day constant — not
/// caller-configurable, per design D6 / spec requirement
/// <em>an additive project-scoped read surface returns the snapshot
/// series over a fixed trailing window</em>. The window length lives
/// here so any read-path change touches one place.
/// </para>
/// <para>
/// The read path performs no event-stream recomputation. It issues a
/// single ordered query against the snapshot table; the cost is
/// proportional to the number of snapshots in the window, independent
/// of how many issues the project has, which is the whole point of the
/// persisted cache.
/// </para>
/// </summary>
public sealed class CumulativeFlowQuerier : IScopedService
{
    /// <summary>
    /// The fixed trailing-window length (inclusive of today), per
    /// design D6 and the spec requirement that the window length
    /// not be configurable by the caller. Kept as a single constant
    /// so any future change of horizon is a one-line edit.
    /// </summary>
    internal const int TrailingWindowDays = 90;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public CumulativeFlowQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// The ordered list of daily snapshots for <paramref name="projectId"/>
    /// within the fixed trailing window, anchored on
    /// <paramref name="nowUtc"/>'s UTC calendar day. Empty (not an
    /// error) when no snapshot has landed yet. The window bounds in
    /// the result are the inclusive UTC calendar days (<c>"yyyy-MM-dd"</c>)
    /// the window covers, irrespective of how many snapshots exist
    /// inside it — they are stable for the same <paramref name="nowUtc"/>
    /// so the consuming chart can render a fixed x-axis.
    /// </summary>
    public async Task<CumulativeFlowResult> GetAsync(
        string projectId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime.Date);
        var rangeFrom = today.AddDays(-(TrailingWindowDays - 1));
        var rangeTo = today;

        var rangeFromString = rangeFrom.ToString("yyyy-MM-dd");
        var rangeToString = rangeTo.ToString("yyyy-MM-dd");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.StagePopulationSnapshots.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Where(row => string.Compare(row.Day, rangeFromString) >= 0
                && string.Compare(row.Day, rangeToString) <= 0)
            .OrderBy(row => row.Day)
            .Select(row => new CumulativeFlowSnapshot(
                Day: row.Day,
                Backlog: row.Backlog,
                Plan: row.Plan,
                Build: row.Build,
                Check: row.Check,
                Integrate: row.Integrate,
                Done: row.Done))
            .ToListAsync(ct);

        return new CumulativeFlowResult(
            Snapshots: rows,
            RangeFrom: rangeFromString,
            RangeTo: rangeToString);
    }

    /// <summary>
    /// The result of <see cref="GetAsync"/>. <see cref="Snapshots"/> is
    /// ordered oldest-first; the length is at most
    /// <see cref="TrailingWindowDays"/>. <see cref="RangeFrom"/> and
    /// <see cref="RangeTo"/> are the inclusive UTC calendar-day bounds
    /// (<c>"yyyy-MM-dd"</c>) the window covers.
    /// </summary>
    public sealed record CumulativeFlowResult(
        IReadOnlyList<CumulativeFlowSnapshot> Snapshots,
        string RangeFrom,
        string RangeTo);
}

/// <summary>
/// One snapshot row in the cumulative-flow result, projected from
/// <see cref="StagePopulationSnapshotRow"/>. The DTO field order matches
/// the consuming widget's band order (bottom of the stack first).
/// </summary>
public sealed record CumulativeFlowSnapshot(
    string Day,
    int Backlog,
    int Plan,
    int Build,
    int Check,
    int Integrate,
    int Done);
