using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.StagePopulation;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Events.Hosting;

/// <summary>
/// Daily stage-population snapshot job. Once per period (default 1 day)
/// the service walks every project in keyset batches, derives each
/// in-flow issue's attributed stage as of the current UTC day from
/// already-persisted events (via <see cref="IssueStageAttribution"/>),
/// and upserts one snapshot row per project per day. The CFD widget
/// reads from this table; the event stream is the source of truth, the
/// snapshot is the persisted cache.
/// <para>
/// Attribution is the same shared <em>latest-attempt,
/// latest-run-wins, invalidate-on-restart</em> rule the
/// <c>workflow-stage-duration-metrics</c> surface uses, so the two
/// surfaces cannot disagree on an issue's latest stage. Cancelled
/// exclusion reads the emitted <c>IssueCancelled</c>
/// (<c>com.mohist.issue.cancelled</c>) event, which the catalog
/// declares and which <c>Issue.Close()</c> now emits.
/// </para>
/// <para>
/// Per-sweep exceptions are swallowed so a single bad project never
/// kills the loop; idempotent writes (re-running for the same day
/// upserts in place via the unique index
/// <c>UQ_StagePopulationSnapshots_ProjectId_Day</c>) make retries
/// safe. No historical backfill — the snapshot history accrues from
/// the go-live day forward.
/// </para>
/// <para>
/// Lives under <c>Events/Hosting/</c> to honor the
/// <em>feature directories only contain Domain/Grains/Services</em>
/// convention: the job reads across slices and must not form a
/// slice-internal cycle, the documented placement rationale also
/// followed by <see cref="EpicReconciliationService"/>.
/// </para>
/// </summary>
public sealed class StagePopulationSnapshotService : BackgroundService
{
    private const int ProjectBatchSize = 200;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StagePopulationSnapshotService> _log;
    private readonly TimeSpan _snapshotPeriod;

    public StagePopulationSnapshotService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<StagePopulationSnapshotService> log,
        IOptions<StagePopulationSnapshotOptions>? options = null)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _log = log;
        _snapshotPeriod = options?.Value.SnapshotPeriod
            ?? StagePopulationSnapshotOptions.DefaultSnapshotPeriod;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't run on host startup — wait one period first so the
        // rest of the host has time to settle. Mirrors
        // IssueWorkflowReconciliationService and EpicReconciliationService.
        try
        {
            await Task.Delay(_snapshotPeriod, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SnapshotOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "StagePopulationSnapshotService sweep failed");
            }

            try
            {
                await Task.Delay(_snapshotPeriod, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Test seam — invokes the same candidate-walk the hosted loop
    /// runs without waiting for the timer. Derives each in-flow
    /// issue's attributed stage as of <c>TimeProvider.GetUtcNow()</c>'s
    /// UTC day and upserts one snapshot row per project per day.
    /// </summary>
    public Task<IReadOnlyList<StagePopulationSnapshotRow>> SnapshotOnceAsync(CancellationToken ct = default) =>
        SnapshotForUtcDayAsync(_timeProvider.GetUtcNow(), ct);

    /// <summary>
    /// Snapshot-as-of <paramref name="nowUtc"/>. The UTC day
    /// (<c>"yyyy-MM-dd"</c>) is the row's identity; a partial-day
    /// re-run for the same day upserts the same row's counts in
    /// place. Exposed for tests that need to drive a snapshot at a
    /// specific clock without touching <see cref="TimeProvider"/>.
    /// Returns the rows that were upserted (one per project seen
    /// during the sweep) for assertion in tests.
    /// </summary>
    public async Task<IReadOnlyList<StagePopulationSnapshotRow>> SnapshotForUtcDayAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var dayUtc = new DateOnly(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, nowUtc.UtcDateTime.Day);
        var dayEndUtc = new DateTimeOffset(
            dayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            TimeSpan.Zero).AddDays(1);
        var dayString = dayUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // Collect all project ids in a single keyset walk so the
        // per-project attribution passes can each open a short-lived
        // DbContext (mirrors EpicReconciliationService's keyset
        // pagination pattern).
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var projectIds = new List<string>();
        string lastProjectId = string.Empty;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Projects.AsNoTracking()
                .Where(p => string.Compare(p.Id, lastProjectId) > 0)
                .OrderBy(p => p.Id)
                .Select(p => p.Id)
                .Take(ProjectBatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            projectIds.AddRange(batch);
            lastProjectId = batch[^1];
        }

        var upserted = new List<StagePopulationSnapshotRow>(projectIds.Count);
        foreach (var projectId in projectIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Per-project scope so the workflow-profile resolvers
                // (scoped services) get fresh state for each sweep.
                // The shared DbContext lives inside the scope alongside
                // them.
                await using var scope = _scopeFactory.CreateAsyncScope();
                var row = await UpsertProjectAsync(
                    scope.ServiceProvider,
                    projectId,
                    dayEndUtc,
                    dayString,
                    ct);
                if (row is not null) upserted.Add(row);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to attribute stage population for project {ProjectId}",
                    projectId);
            }
        }

        _log.LogInformation(
            "StagePopulationSnapshotService wrote {Count} snapshot rows for day {Day}",
            upserted.Count, dayString);

        return upserted;
    }

    private async Task<StagePopulationSnapshotRow?> UpsertProjectAsync(
        IServiceProvider scopedServices,
        string projectId,
        DateTimeOffset dayEndUtc,
        string dayString,
        CancellationToken ct)
    {
        var totals = new StagePopulationSnapshotCounts();
        var dbFactory = scopedServices.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await AttributeProjectAsync(
            scopedServices,
            db,
            projectId,
            dayEndUtc,
            totals,
            ct);

        // Upsert: unique index UQ_StagePopulationSnapshots_ProjectId_Day
        // is the dedup signal. Read first (or use a write that fires
        // the unique-constraint conflict path) — for SQLite, a
        // select-then-insert/update keeps the upsert simple and
        // race-safe within a single sweep because the sweep is
        // single-threaded per project.
        var existing = await db.StagePopulationSnapshots
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Day == dayString, ct);
        if (existing is null)
        {
            var inserted = new StagePopulationSnapshotRow
            {
                ProjectId = projectId,
                Day = dayString,
                Backlog = totals.Backlog,
                Plan = totals.Plan,
                Build = totals.Build,
                Check = totals.Check,
                Integrate = totals.Integrate,
                Done = totals.Done,
            };
            db.StagePopulationSnapshots.Add(inserted);
            try
            {
                await db.SaveChangesAsync(ct);
                return inserted;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                db.ChangeTracker.Clear();
                existing = await db.StagePopulationSnapshots
                    .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Day == dayString, ct);
                if (existing is null) throw;
            }
        }

        existing.Backlog = totals.Backlog;
        existing.Plan = totals.Plan;
        existing.Build = totals.Build;
        existing.Check = totals.Check;
        existing.Integrate = totals.Integrate;
        existing.Done = totals.Done;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    private async Task AttributeProjectAsync(
        IServiceProvider scopedServices,
        MohistDbContext db,
        string projectId,
        DateTimeOffset dayEndUtc,
        StagePopulationSnapshotCounts totals,
        CancellationToken ct)
    {
        // Resolve the project's issue sources once (IssueEvents has
        // no indexed ProjectId column; we filter on Source).
        var projectIssueIds = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => row.IssueId)
            .ToListAsync(ct);
        if (projectIssueIds.Count == 0) return;

        var projectSources = projectIssueIds
            .Select(id => IssueEventPersistence.IssueSource(id))
            .ToList();

        // Resolve the project's stage order from the effective
        // workflow profile. The shared attribution core only
        // documents the order; the function does not validate it.
        var profiles = scopedServices.GetRequiredService<IssueWorkflowProfileRegistry>();
        var effectiveProfileResolver = scopedServices.GetRequiredService<EffectiveWorkflowProfileResolver>();
        var projectProfileManager = scopedServices.GetRequiredService<ProjectWorkflowProfileManager>();
        var stageOrder = await ResolveProjectStageOrderAsync(
            profiles, effectiveProfileResolver, projectProfileManager, db, projectId);

        // Pull every IssueEvent for the project (work-started /
        // completed / cancelled / reopened) and every workflow-run
        // event for the project's run sources (StageStarted /
        // StageCompleted) — applied to the day bound in LINQ-to-objects
        // after materialization (SQLite cannot translate
        // DateTimeOffset against the TEXT Time column).
        var runIdsByIssue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var lifecycleByIssue = new Dictionary<string, List<IssueStageAttribution.AttributionEvent>>(StringComparer.Ordinal);
        var issueEvents = await db.IssueEvents.AsNoTracking()
            .Where(e => projectSources.Contains(e.Source))
            .Select(e => new { e.Source, e.Type, e.Time, e.Id, e.Data })
            .ToListAsync(ct);

        foreach (var e in issueEvents)
        {
                if (e.Time >= dayEndUtc) continue;
                var issueId = e.Source[IssueEventPersistence.SourcePrefix.Length..];
                var wrId = ReadWorkflowRunId(e.Data);
            if (e.Type == "com.mohist.issue.work-started"
                || e.Type == EventCatalog.ReverseDns.IssueCompleted
                || e.Type == EventCatalog.ReverseDns.IssueCancelled
                || e.Type == "com.mohist.issue.reopened")
            {
                if (!lifecycleByIssue.TryGetValue(issueId, out var list))
                {
                    list = new List<IssueStageAttribution.AttributionEvent>();
                    lifecycleByIssue[issueId] = list;
                }
                list.Add(new IssueStageAttribution.AttributionEvent(
                    Type: e.Type,
                    Time: e.Time,
                    Id: e.Id,
                    Stage: null,
                    WorkflowRunId: wrId));
            }

            if (e.Type == "com.mohist.issue.work-started" || e.Type == EventCatalog.ReverseDns.IssueCompleted)
            {
                if (!runIdsByIssue.TryGetValue(issueId, out var ids))
                {
                    ids = new List<string>();
                    runIdsByIssue[issueId] = ids;
                }
                if (!string.IsNullOrWhiteSpace(wrId)) ids.Add(wrId);
            }
        }

        // Union each issue's current WorkflowRunId with the historical
        // ids found in events, so a stage whose events live only on
        // the current run is still discoverable.
        var currentRunIdsByIssue = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.WorkflowRunId != null)
            .Select(row => new { row.IssueId, row.WorkflowRunId })
            .ToListAsync(ct);
        foreach (var r in currentRunIdsByIssue)
        {
            if (string.IsNullOrWhiteSpace(r.WorkflowRunId)) continue;
            if (!runIdsByIssue.TryGetValue(r.IssueId, out var ids))
            {
                ids = new List<string>();
                runIdsByIssue[r.IssueId] = ids;
            }
            ids.Add(r.WorkflowRunId);
        }

        var allRunIds = runIdsByIssue.Values
            .SelectMany(ids => ids)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Load stage events for the project's run sources (filtered
        // to StageStarted / StageCompleted types).
        var stageEventsByRun = new Dictionary<string, List<(long Id, string Type, DateTimeOffset Time, string? Stage)>>(StringComparer.Ordinal);
        if (allRunIds.Length > 0)
        {
            var sources = allRunIds
                .Select(id => WorkflowRunEventPersistence.WorkflowRunSource(id))
                .ToArray();
            var rows = await db.WorkflowRunEvents.AsNoTracking()
                .Where(row => sources.Contains(row.Source)
                    && (row.Type == EventCatalog.ReverseDns.StageStarted
                        || row.Type == EventCatalog.ReverseDns.StageCompleted))
                .Select(row => new { row.Source, row.Id, row.Type, row.Time, row.Data })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                if (row.Time >= dayEndUtc) continue;
                if (!TryMapRunId(row.Source, allRunIds, out var runId)) continue;
                if (!stageEventsByRun.TryGetValue(runId, out var list))
                {
                    list = new List<(long, string, DateTimeOffset, string?)>();
                    stageEventsByRun[runId] = list;
                }
                list.Add((row.Id, row.Type, row.Time, ReadStageId(row.Data)));
            }
        }

        // Per-issue attribution via the shared core.
        foreach (var issueId in projectIssueIds)
        {
            if (ct.IsCancellationRequested) break;

            var lifecycle = lifecycleByIssue.TryGetValue(issueId, out var lc)
                ? lc
                : (IReadOnlyList<IssueStageAttribution.AttributionEvent>)Array.Empty<IssueStageAttribution.AttributionEvent>();
            var runIds = runIdsByIssue.TryGetValue(issueId, out var ri)
                ? ri
                : (IReadOnlyList<string>)Array.Empty<string>();

            var stageEvents = new List<IssueStageAttribution.AttributionEvent>(8);
            foreach (var runId in runIds)
            {
                if (string.IsNullOrWhiteSpace(runId)) continue;
                if (!stageEventsByRun.TryGetValue(runId, out var list)) continue;
                foreach (var se in list)
                {
                    stageEvents.Add(new IssueStageAttribution.AttributionEvent(
                        Type: se.Type,
                        Time: se.Time,
                        Id: se.Id,
                        Stage: se.Stage,
                        WorkflowRunId: runId));
                }
            }

            var combined = new List<IssueStageAttribution.AttributionEvent>(lifecycle.Count + stageEvents.Count);
            combined.AddRange(lifecycle);
            combined.AddRange(stageEvents);

            var attribution = IssueStageAttribution.Attribute(combined, stageOrder, dayEndUtc);
            switch (attribution.Kind)
            {
                case IssueStageAttribution.Kind.Backlog:
                    totals.Backlog++;
                    break;
                case IssueStageAttribution.Kind.Done:
                    totals.Done++;
                    break;
                case IssueStageAttribution.Kind.Cancelled:
                    // Cancelled issues are excluded from the flow
                    // population — no count for them.
                    break;
                case IssueStageAttribution.Kind.Stage:
                    IncrementStage(totals, attribution.Stage);
                    break;
                case IssueStageAttribution.Kind.None:
                    // Defensive: work-started but no stage events yet.
                    // The issue is in transit between work-start and
                    // the first stage; not in any bucket. This is a
                    // vanishingly rare transient.
                    break;
            }
        }
    }

    private static void IncrementStage(StagePopulationSnapshotCounts totals, string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return;
        if (string.Equals(stage, "plan", StringComparison.OrdinalIgnoreCase)) totals.Plan++;
        else if (string.Equals(stage, "build", StringComparison.OrdinalIgnoreCase)) totals.Build++;
        else if (string.Equals(stage, "check", StringComparison.OrdinalIgnoreCase)) totals.Check++;
        else if (string.Equals(stage, "integrate", StringComparison.OrdinalIgnoreCase)) totals.Integrate++;
        // Stages outside the CFD's known set (e.g. custom stages
        // from a project profile) are not bucketed — they fall out
        // of the population by design.
    }

    private static async Task<IReadOnlyList<string>> ResolveProjectStageOrderAsync(
        IssueWorkflowProfileRegistry profiles,
        EffectiveWorkflowProfileResolver effectiveProfileResolver,
        ProjectWorkflowProfileManager projectProfileManager,
        MohistDbContext db,
        string projectId)
    {
        var profileId = effectiveProfileResolver.Resolve(
            issueSelection: null,
            projectDefaultId: await LoadProjectDefaultTemplateAsync(db, projectId),
            disabledIds: await projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId));
        if (profileId is null) return new List<string>();
        var profile = profiles.Get(profileId);
        return profile.Definition.Stages?
            .Select(s => s.Stage)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList()
            ?? new List<string>();
    }

    private static async Task<string?> LoadProjectDefaultTemplateAsync(MohistDbContext db, string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row?.DefaultTemplateId;
    }

    private static string? ReadWorkflowRunId(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        foreach (var prop in data.EnumerateObject())
        {
            if (string.Equals(prop.Name, "workflowRunId", StringComparison.Ordinal)
                || string.Equals(prop.Name, "WorkflowRunId", StringComparison.Ordinal))
            {
                return prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
            }
        }
        return null;
    }

    private static string? ReadStageId(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        foreach (var prop in data.EnumerateObject())
        {
            if (string.Equals(prop.Name, "value", StringComparison.Ordinal))
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var inner in prop.Value.EnumerateObject())
                    {
                        if (string.Equals(inner.Name, "stage", StringComparison.Ordinal))
                        {
                            return inner.Value.ValueKind == System.Text.Json.JsonValueKind.String
                                ? inner.Value.GetString()
                                : inner.Value.ToString();
                        }
                    }
                }
            }
            else if (string.Equals(prop.Name, "stage", StringComparison.Ordinal))
            {
                return prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
            }
        }
        return null;
    }

    private static bool TryMapRunId(string source, string[] allRunIds, out string runId)
    {
        runId = string.Empty;
        if (string.IsNullOrEmpty(source)) return false;
        const string prefix = "/mohist/workflow-runs/";
        if (!source.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = source[prefix.Length..];
        if (Array.IndexOf(allRunIds, id) < 0) return false;
        runId = id;
        return true;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite
        && sqlite.SqliteExtendedErrorCode == 2067;
}

public sealed class StagePopulationSnapshotOptions
{
    public const string SectionName = "Mohist:StagePopulationSnapshot";

    public static readonly TimeSpan DefaultSnapshotPeriod = TimeSpan.FromDays(1);

    public TimeSpan SnapshotPeriod { get; set; } = DefaultSnapshotPeriod;
}

internal sealed class StagePopulationSnapshotCounts
{
    public int Backlog;
    public int Plan;
    public int Build;
    public int Check;
    public int Integrate;
    public int Done;
}
