using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// The projection read service behind the direct API's public Job,
/// Input, and Turn reads. Every answer is served only from the
/// persisted public projection tables: the stored snapshot is the
/// response body verbatim, a resource absent from or not belonging to
/// the authorized Project is the route's 404 code, and freshness is a
/// comparison in the same request between the required durable source
/// watermark (the canonical ledger revisions and per-source journal
/// heads for the anchor's Job and Session) and the stored projection
/// checkpoint — a checkpoint that has not consumed those facts yet
/// answers <c>projection_lag</c> instead of a stale snapshot.
/// <para>
/// The comparison mirrors exactly what the projection engine consumes
/// per target, so "not behind" proves the served snapshot reflects
/// every durable fact the engine would consider for that anchor. The
/// read never mutates anything: lag is a transport condition, not a
/// public execution state.
/// </para>
/// </summary>
public sealed class PublicExecutionReadQuerier : IScopedService
{
    internal const string JobAnchor = "job";
    internal const string InputAnchor = "input";
    internal const string TurnAnchor = "turn";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public PublicExecutionReadQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Reads the public Job anchor. A prepared Job (durable prepare
    /// fact, no Session acceptance yet) answers its own Job-anchored
    /// snapshot; once the Job's Session is joined, the Job's feeds and
    /// the Session's feeds both have to be current.
    /// </summary>
    public async Task<PublicReadOutcome> ReadJobAsync(
        string projectId,
        string jobId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.AgentJobs.AsNoTracking()
            .FirstOrDefaultAsync(row => row.JobKey == jobId, ct);

        // Membership is established from the canonical Job before the
        // public projection is consulted. A deleted or foreign Job is
        // indistinguishable from a missing one even if an old snapshot
        // remains in the projection tables.
        if (job is null
            || !string.Equals(ResolveJobProject(job), projectId, StringComparison.Ordinal))
        {
            return PublicReadOutcome.Missing;
        }

        var snapshot = await LoadSnapshotAsync(db, JobAnchor, jobId, ct);
        if (snapshot is not null
            && !string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal))
        {
            return PublicReadOutcome.Missing;
        }

        var feeds = new List<FeedWatermark>
        {
            new(
                PublicProjectionFeeds.AgentJobs,
                job.JobKey,
                RevisionWatermark(job.Revision),
                HeadComparison: false),
        };
        var sessions = new List<string>();
        AddJournalFeed(
            feeds,
            PublicProjectionFeeds.AgentJobEvents,
            AgentJobEventPersistence.AgentJobSource(job.JobKey),
            await MaxJournalIdAsync(db.AgentJobEvents, AgentJobEventPersistence.AgentJobSource(job.JobKey), ct));
        if (job.AgentSessionId is { Length: > 0 } sessionId)
        {
            sessions.Add(sessionId);
        }

        if (snapshot?.SessionId is { Length: > 0 } snapshotSession)
        {
            sessions.Add(snapshotSession);
        }

        if (await IsBehindAsync(db, feeds, sessions, ct))
        {
            return PublicReadOutcome.Lag;
        }

        // A canonical Job can exist before its first public snapshot is
        // committed. Once its feeds are current, absence means the
        // public projection has no readable anchor rather than stale
        // state being served.
        return snapshot is null
            ? PublicReadOutcome.Missing
            : PublicReadOutcome.Found(snapshot.SnapshotJson);
    }

    /// <summary>
    /// Reads the public Session Input anchor. Input and Turn anchors
    /// only exist once their Session projection published them, so a
    /// missing anchor is the canonical 404 — a caller can never learn
    /// an Input ID before its projection exists.
    /// </summary>
    public Task<PublicReadOutcome> ReadInputAsync(
        string projectId,
        string inputId,
        CancellationToken ct = default) =>
        ReadSessionAnchorAsync(InputAnchor, projectId, inputId, ct);

    /// <summary>Reads the public Session Turn anchor.</summary>
    public Task<PublicReadOutcome> ReadTurnAsync(
        string projectId,
        string turnId,
        CancellationToken ct = default) =>
        ReadSessionAnchorAsync(TurnAnchor, projectId, turnId, ct);

    /// <summary>
    /// Applies the same Session feed freshness comparison used by Input
    /// and Turn reads. Event pages use it only as a transport gate; their
    /// body remains exclusively the persisted public event journal.
    /// </summary>
    public async Task<bool> IsSessionProjectionBehindAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await IsBehindAsync(db, feeds: [], [sessionId], ct);
    }

    private async Task<PublicReadOutcome> ReadSessionAnchorAsync(
        string anchorType,
        string projectId,
        string anchorId,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var canonical = await FindCanonicalSessionAnchorAsync(db, anchorType, anchorId, ct);
        if (canonical is null
            || !string.Equals(canonical.ProjectId, projectId, StringComparison.Ordinal))
        {
            return PublicReadOutcome.Missing;
        }

        var snapshot = await LoadSnapshotAsync(db, anchorType, anchorId, ct);
        if (snapshot is null
            || !string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(snapshot.SessionId, canonical.SessionId, StringComparison.Ordinal))
        {
            // An owned canonical record without a public snapshot is
            // still subject to the Session freshness gate. If the
            // checkpoint is behind, the caller must retry rather than
            // interpret the temporary absence as a 404.
            if (await IsBehindAsync(db, feeds: [], [canonical.SessionId], ct))
            {
                return PublicReadOutcome.Lag;
            }

            return PublicReadOutcome.Missing;
        }

        if (await IsBehindAsync(db, feeds: [], [canonical.SessionId], ct))
        {
            return PublicReadOutcome.Lag;
        }

        return PublicReadOutcome.Found(snapshot.SnapshotJson);
    }

    private static async Task<CanonicalSessionAnchor?> FindCanonicalSessionAnchorAsync(
        MohistDbContext db,
        string anchorType,
        string anchorId,
        CancellationToken ct)
    {
        // The JSON contains the canonical input/turn arrays; the
        // substring predicate narrows the rows before exact record
        // matching and keeps this lookup independent of the public
        // projection. Prompt text can produce false candidates, but
        // the exact ID checks below prevent false ownership matches.
        var candidates = await db.AgentSessions.AsNoTracking()
            .Where(row => row.State.Contains(anchorId))
            .Select(row => new { row.Id, row.State, row.LabelProjectId })
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            var session = AgentSessionJson.Deserialize(new AgentSessionRow
            {
                Id = candidate.Id,
                State = candidate.State,
                LabelProjectId = candidate.LabelProjectId,
            });
            if (session is null)
            {
                continue;
            }

            var ownsAnchor = anchorType switch
            {
                InputAnchor => (session.Status.Inputs ?? [])
                    .Any(input => string.Equals(input.Id, anchorId, StringComparison.Ordinal)),
                TurnAnchor => (session.Status.Turns ?? [])
                    .Any(turn => string.Equals(turn.Id, anchorId, StringComparison.Ordinal)),
                _ => false,
            };
            if (ownsAnchor)
            {
                return new CanonicalSessionAnchor(candidate.Id, candidate.LabelProjectId);
            }
        }

        return null;
    }

    private static async Task<PublicExecutionSnapshotRow?> LoadSnapshotAsync(
        MohistDbContext db,
        string anchorType,
        string anchorId,
        CancellationToken ct) =>
        await db.PublicExecutionSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(row => row.AnchorType == anchorType && row.AnchorId == anchorId, ct);

    /// <summary>
    /// The freshness comparison: every feed the projection engine
    /// consumes for the anchor's target must have a stored checkpoint
    /// watermark that already covers the current durable value. Any
    /// feed that has never been checkpointed or holds an older value
    /// means the persisted snapshot is not current enough to serve.
    /// </summary>
    private static async Task<bool> IsBehindAsync(
        MohistDbContext db,
        List<FeedWatermark> feeds,
        IReadOnlyList<string> sessionIds,
        CancellationToken ct)
    {
        foreach (var sessionId in sessionIds.Distinct(StringComparer.Ordinal))
        {
            await AddSessionFeedsAsync(db, feeds, sessionId, ct);
        }

        if (feeds.Count == 0)
        {
            return false;
        }

        var pairs = feeds.Select(feed => feed.Feed + "\u001f" + feed.SourceKey).ToList();
        var stored = (await db.PublicProjectionCheckpoints.AsNoTracking()
                .Where(row => pairs.Contains(row.Feed + "\u001f" + row.SourceKey))
                .ToListAsync(ct))
            .ToDictionary(
                row => row.Feed + "\u001f" + row.SourceKey,
                row => row.Watermark,
                StringComparer.Ordinal);

        foreach (var feed in feeds)
        {
            stored.TryGetValue(feed.Feed + "\u001f" + feed.SourceKey, out var watermark);
            if (!IsSatisfied(feed, watermark))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects the feeds the projection engine consumes for one
    /// Session target: the Session ledger's consumed state digest, the
    /// Session journal and lifecycle-history heads, and every joined Job's
    /// ledger revision and journal head.
    /// </summary>
    private static async Task AddSessionFeedsAsync(
        MohistDbContext db,
        List<FeedWatermark> feeds,
        string sessionId,
        CancellationToken ct)
    {
        var session = await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == sessionId, ct);
        if (session is null)
        {
            // The canonical Session is gone; there is nothing left for
            // the projection to consume about it.
            return;
        }

        feeds.Add(new FeedWatermark(
            PublicProjectionFeeds.AgentSessions,
            session.Id,
            PublicExecutionAggregator.StateDigest(session.State),
            HeadComparison: false));
        AddJournalFeed(
            feeds,
            PublicProjectionFeeds.AgentSessionEvents,
            AgentSessionEventPersistence.AgentSessionSource(session.Id),
            await MaxJournalIdAsync(
                db.AgentSessionEvents,
                AgentSessionEventPersistence.AgentSessionSource(session.Id),
                ct));
        AddJournalFeed(
            feeds,
            PublicProjectionFeeds.AgentSessionLifecycle,
            session.Id,
            await db.AgentSessionLifecycleTransitions.AsNoTracking()
                .Where(row => row.SessionId == session.Id)
                .MaxAsync(row => (long?)row.Id, ct));

        var jobs = await db.AgentJobs.AsNoTracking()
            .Where(row => row.AgentSessionId == sessionId)
            .Select(row => new { row.JobKey, row.Revision })
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            feeds.Add(new FeedWatermark(
                PublicProjectionFeeds.AgentJobs,
                job.JobKey,
                RevisionWatermark(job.Revision),
                HeadComparison: false));
            AddJournalFeed(
                feeds,
                PublicProjectionFeeds.AgentJobEvents,
                AgentJobEventPersistence.AgentJobSource(job.JobKey),
                await MaxJournalIdAsync(
                    db.AgentJobEvents,
                    AgentJobEventPersistence.AgentJobSource(job.JobKey),
                    ct));
        }
    }

    private static void AddJournalFeed(
        List<FeedWatermark> feeds,
        string feed,
        string sourceKey,
        long? head)
    {
        // A source with no journal rows has nothing to consume; the
        // engine writes no checkpoint for it and discovery never marks
        // it dirty, so the feed is trivially satisfied.
        if (head is null)
        {
            return;
        }

        feeds.Add(new FeedWatermark(feed, sourceKey, head.Value.ToString(), HeadComparison: true));
    }

    private static async Task<long?> MaxJournalIdAsync<T>(
        IQueryable<T> rows,
        string source,
        CancellationToken ct) where T : class, IEventRow =>
        await rows.AsNoTracking()
            .Where(row => row.Source == source)
            .MaxAsync(row => (long?)row.Id, ct);

    private static bool IsSatisfied(FeedWatermark feed, string? storedWatermark)
    {
        if (feed.HeadComparison)
        {
            // Journal feeds advance by per-source sequence: the stored
            // watermark must parse and cover the current head.
            return long.TryParse(storedWatermark, out var consumed)
                && long.TryParse(feed.Watermark, out var head)
                && consumed >= head;
        }

        // Ledger feeds advance by exact revision or consumed state
        // digest: anything else means the durable fact is unconsumed.
        return string.Equals(storedWatermark, feed.Watermark, StringComparison.Ordinal);
    }

    private static string RevisionWatermark(long revision) => revision.ToString();

    private static string? ResolveJobProject(AgentJobRow row) =>
        TryDeserializeJobState(row.State)?.Input?.ProjectId ?? row.ProjectId;

    private sealed record CanonicalSessionAnchor(string SessionId, string? ProjectId);

    private static AgentJobState? TryDeserializeJobState(string stateJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentJobState>(stateJson, JSON.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One required durable source fact and its current value, in the
    /// same watermark shape the projection engine checkpoints.
    /// </summary>
    private sealed record FeedWatermark(
        string Feed,
        string SourceKey,
        string Watermark,
        bool HeadComparison);
}
