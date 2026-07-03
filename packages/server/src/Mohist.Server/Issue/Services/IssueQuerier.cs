using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services.Attachments;

namespace Mohist.Server.Issue.Services;

public class IssueQuerier : IScopedService
{
    internal const string WorkStartedType = "com.mohist.issue.work-started";
    private static readonly string[] QualityStageOrder = ["plan", "build", "check", "integrate"];
    private static readonly string[] QualityWorkflowEventTypes =
    [
        EventCatalog.ReverseDns.StageStarted,
        EventCatalog.ReverseDns.CheckPassed,
        EventCatalog.ReverseDns.CheckFailed,
        EventCatalog.ReverseDns.CheckPending,
        EventCatalog.ReverseDns.RepairScheduled,
    ];
    // Stage-duration event loader selects BOTH `StageStarted` and
    // `StageCompleted` over the per-run `WorkflowRunEvents` source. The
    // existing `QualityWorkflowEventTypes` set omits `StageCompleted`,
    // so the loaders cannot be shared.
    private static readonly string[] StageDurationEventTypes =
    [
        EventCatalog.ReverseDns.StageStarted,
        EventCatalog.ReverseDns.StageCompleted,
    ];

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly ProjectQuerier _projects;
    private readonly IssueRepositoryResolver _resolver;
    private readonly ConfigService _configService;
    private readonly EffectiveWorkflowProfileResolver _effectiveProfileResolver;
    private readonly ProjectWorkflowProfileManager _projectProfileManager;

    public IssueQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueWorkflowProfileRegistry profiles,
        ProjectQuerier projects,
        IssueRepositoryResolver resolver,
        ConfigService configService,
        EffectiveWorkflowProfileResolver effectiveProfileResolver,
        ProjectWorkflowProfileManager projectProfileManager)
    {
        _dbFactory = dbFactory;
        _profiles = profiles;
        _projects = projects;
        _resolver = resolver;
        _configService = configService;
        _effectiveProfileResolver = effectiveProfileResolver;
        _projectProfileManager = projectProfileManager;
    }

    public async Task<IssueReadModel?> GetAsync(string projectId, int number, ProjectInfo? project = null)
    {
        project ??= await _projects.GetByIdAsync(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, projectId, number);
        return issue is null ? null : await EnrichAsync(db, await ToReadModelAsync(db, issue, project));
    }

    public async Task<IssueInfo?> GetInfoAsync(string projectId, int number, ProjectInfo? project = null)
    {
        project ??= await _projects.GetByIdAsync(projectId);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var issue = await LoadIssueAsync(db, projectId, number);
        if (issue is null) return null;
        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);
        return ToInfo(issue, project, projectDefaultTemplateId, disabledIds);
    }

    public async Task<Domain.Issue?> GetDomainAsync(string projectId, int number)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await LoadIssueAsync(db, projectId, number);
    }

    /// <summary>
    /// Reverse lookup: returns the <c>issueId</c> of the in-progress issue
    /// bound to <paramref name="workflowRunId"/>, or <c>null</c> when no
    /// in-progress issue is bound. Used by
    /// <c>Events/Subscriptions/IssueWorkflowCompletionHandler</c> to
    /// resolve the owning issue from a <c>com.mohist.workflow.run.completed</c>
    /// CloudEvent (whose payload carries no issue context).
    /// <para>
    /// The query rides the existing indexed <c>IssueRow.WorkflowRunId</c>
    /// computed column plus the <c>Status</c> index, so it is a single
    /// cheap indexed query — no schema change, no new index. Filtering
    /// to <c>Status = 'inProgress'</c> enforces a documented invariant:
    /// a preserved <c>WorkflowRunId</c> on <c>Done</c>/archived issues is
    /// execution history, not a stuck-run signal, so an unfiltered lookup
    /// could match a stale binding. The status filter also makes the
    /// post-<c>Done</c> idempotent path explicit at the handler level
    /// (lookup returns <c>null</c> → no-op) instead of relying solely on
    /// the grain guard.
    /// </para>
    /// </summary>
    public async Task<string?> GetIssueIdForWorkflowRunAsync(string workflowRunId)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.Issues.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.Status == "inProgress")
            .Select(r => new { r.IssueId })
            .FirstOrDefaultAsync();
        return row?.IssueId;
    }

    private static async Task<Domain.Issue?> LoadIssueAsync(MohistDbContext db, string projectId, int number)
    {
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Number == number);
        return row is null ? null : IssueStore.Deserialize(row.State);
    }

    public Task<List<IssueReadModel>> ListAsync(
        string projectId,
        ProjectInfo? project = null,
        string? stage = null,
        string? label = null,
        string? priority = null,
        bool? archived = null,
        bool? all = null) =>
        ListWithLabelFiltersAsync(projectId, project, stage, LabelFilterTokens(label), priority, archived, all);

    public async Task<List<IssueReadModel>> ListWithLabelFiltersAsync(
        string projectId,
        ProjectInfo? project,
        string? stage,
        IReadOnlyList<string>? labels,
        string? priority,
        bool? archived,
        bool? all)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(); var rows = await db.Issues
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();
        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);
        var list = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project, projectDefaultTemplateId, disabledIds)))
            .OrderBy(i => i.Number)
            .ToList();

        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));
        ApplyFeedbackProjections(list, await LoadFeedbackAsync(db, list));

        var query = list.AsEnumerable();

        if (archived == true)
            query = query.Where(i => i.ArchivedAt != null);
        else if (all != true)
            query = query.Where(i => i.ArchivedAt == null);

        if (!string.IsNullOrEmpty(stage))
            query = query.Where(i => string.Equals(i.Status, stage, StringComparison.OrdinalIgnoreCase));

        if (labels is { Count: > 0 })
        {
            var filters = labels
                .Select(ParseLabelFilter)
                .Where(filter => filter.Key is not null)
                .ToArray();
            if (filters.Length > 0)
            {
                query = query.Where(i => filters.All(filter =>
                    i.Labels.TryGetValue(filter.Key!, out var v)
                    && string.Equals(v, filter.Value, StringComparison.Ordinal)));
            }
        }

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(i => string.Equals(i.Priority, priority, StringComparison.OrdinalIgnoreCase));

        return await EnrichAsync(db, query.OrderBy(i => i.Number).ToList());
    }

    public async Task<IReadOnlyList<IssueReadModel>> ListInProgressWithApprovalGateAsync(string projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.Issues
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();
        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);
        var list = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project: null, projectDefaultTemplateId, disabledIds)))
            .ToList();
        ApplyWorkflowProjections(list, await LoadWorkflowStatesAsync(db, list));
        ApplyFeedbackProjections(list, await LoadFeedbackAsync(db, list));

        return list
            .Where(IsPausedOnApprovalGate)
            .OrderBy(i => i.Number)
            .ToList();
    }

    private static bool IsPausedOnApprovalGate(IssueReadModel issue) =>
        string.Equals(issue.Status, "in_progress", StringComparison.OrdinalIgnoreCase)
        && string.Equals(issue.WorkflowStatus, "awaiting-approval", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bucketing granularity for the completion time-series. v1 only supports
    /// fixed by-day and by-week windows; the controller rejects anything else.
    /// </summary>
    public enum CompletionBucket
    {
        Day,
        Week,
    }

    /// <summary>
    /// One bucket in the completion time-series. The <see cref="Boundary"/>
    /// string is the ISO calendar boundary that the bucket represents:
    /// <c>yyyy-MM-dd</c> for day buckets, and the Monday of the ISO week for
    /// week buckets. Both are UTC. Counts are the distinct number of
    /// project-scoped issues that reached the terminal state within the
    /// bucket.
    /// </summary>
    public sealed record CompletionBucketPoint(
        string Boundary,
        int Completed,
        int Failed);

    /// <summary>
    /// The result of <see cref="GetCompletionBucketsAsync"/>. <see cref="Buckets"/>
    /// is dense — every bucket in the fixed trailing window is present, even
    /// when its counts are zero, so callers can render a continuous chart.
    /// <see cref="CurrentTotal"/> and <see cref="PreviousTotal"/> are strictly
    /// additive: the existing per-bucket series, window bounds, and fixed
    /// bucket granularity are preserved unchanged. Both totals aggregate the
    /// latest-terminal-event classification (completed vs cancelled) over
    /// the current window <c>[now − W, now]</c> and the immediately-preceding
    /// window of the same length <c>[now − 2W, now − W]</c>.
    /// </summary>
    public sealed record CompletionBucketsResult(
        string Bucket,
        DateTimeOffset WindowFrom,
        DateTimeOffset WindowTo,
        IReadOnlyList<CompletionBucketPoint> Buckets,
        CompletionTotal CurrentTotal,
        CompletionTotal PreviousTotal);

    /// <summary>
    /// Window-scoped completion totals. <see cref="Completed"/> and
    /// <see cref="Failed"/> are aggregated from the latest-terminal-event
    /// classification across every issue whose terminal event falls in
    /// the window. <see cref="SampleCount"/> is the number of terminal
    /// issues contributing — the discriminator that distinguishes the
    /// empty (zero-sample) result (<c>SampleCount == 0</c>, no terminal
    /// issues fell in the window) from a genuine zero-completion window
    /// (<c>SampleCount > 0</c>, every terminal issue cancelled and none
    /// completed).
    /// </summary>
    public sealed record CompletionTotal(
        int Completed,
        int Failed,
        int SampleCount);

    /// <summary>
    /// The trailing window returned by <see cref="GetApprovalWaitAsync"/>.
    /// </summary>
    public sealed record ApprovalWaitWindow(DateTimeOffset From, DateTimeOffset To);

    /// <summary>
    /// The result of <see cref="GetApprovalWaitAsync"/>. <see cref="SampleCount"/>
    /// distinguishes a true zero-sample window (null stats) from a completed
    /// approval with no measurable wait (SampleCount 1, stats 0).
    /// </summary>
    public sealed record ApprovalWaitResult(
        ApprovalWaitWindow Window,
        int SampleCount,
        double? AverageSeconds,
        double? MedianSeconds,
        double? MaxSeconds);

    /// <summary>
    /// One per-issue sample returned by <see cref="GetDeliveryTimesAsync"/>.
    /// <see cref="IssueNumber"/> is the project's display number (so the
    /// consuming chart can identify the point without resolving the stable
    /// id); <see cref="CompletedAt"/> is the project's persisted completion
    /// moment (latest terminal <c>done</c>) — not a post-completion
    /// <c>updatedAt</c>; <see cref="LeadDays"/> is always defined
    /// (creation → completion); <see cref="CycleDays"/> is the
    /// first-work-start → final-completion duration when at least one
    /// <c>IssueWorkStarted</c> event exists for the issue, or <c>null</c>
    /// when the issue has no recorded work-start. <c>null</c> means
    /// "undefined" (no recorded start) and is structurally distinguishable
    /// from a genuine zero-duration cycle (<c>CycleDays == 0</c>).
    /// </summary>
    public sealed record DeliveryTimePoint(
        int IssueNumber,
        DateTimeOffset CompletedAt,
        double LeadDays,
        double? CycleDays);

    /// <summary>
    /// The result of <see cref="GetDeliveryTimesAsync"/>. <see cref="Points"/>
    /// is empty (not an error, not a fabricated zero) when the trailing
    /// window contains no delivered issues; an empty list length is the
    /// empty signal the consuming chart relies on.
    /// <see cref="PreviousAverageCycleDays"/> is strictly additive: it is the
    /// average cycle time over the immediately-preceding window of the same
    /// length as the current 30-day trailing window
    /// (<c>[now − 2W, now − W]</c>), computed with the identical
    /// earliest-work-start-to-final-completion definition and
    /// completion-time windowing. <c>null</c> is the defined empty result
    /// when the previous window has no delivered issues, structurally
    /// distinguishable from a genuine zero-duration average; it is
    /// evaluated independently of <see cref="Points"/>.
    /// </summary>
    public sealed record DeliveryTimeResult(
        IReadOnlyList<DeliveryTimePoint> Points,
        double? PreviousAverageCycleDays);

    /// <summary>
    /// The trailing window returned by <see cref="GetStageDurationsAsync"/>.
    /// Fixed 30 days, anchored on the issue's persisted completion moment,
    /// shared with the delivery-time surface so the two charts see the
    /// same delivered-issue population.
    /// </summary>
    public sealed record StageDurationWindow(DateTimeOffset From, DateTimeOffset To);

    /// <summary>
    /// Per-stage aggregate returned by <see cref="GetStageDurationsAsync"/>.
    /// <see cref="AverageSeconds"/> / <see cref="MedianSeconds"/> are
    /// <c>null</c> when no delivered issue in the window contributes a
    /// defined sample for the stage (the stage is absent from the result
    /// in that case, but the nullable shape lets the route distinguish
    /// "absent" from "fabricated zero"). <see cref="SampleCount"/>
    /// distinguishes a true zero-sample window from a stage with a genuine
    /// zero-duration sample (one or more issues whose latest attempt
    /// completed at the same moment as it started).
    /// </summary>
    public sealed record StageDurationStageAggregate(
        string Stage,
        int SampleCount,
        double? AverageSeconds,
        double? MedianSeconds);

    /// <summary>
    /// The wait breakout returned alongside the flow-efficiency ratio.
    /// <see cref="AverageApprovalGateWaitSeconds"/> is the mean of the
    /// per-issue approval-gate wait (sum of respondedAt − requestedAt over
    /// completed approvals), averaged over the same delivered issues that
    /// contribute to the ratio. <see cref="AverageInactiveGapSeconds"/>
    /// is the mean of <c>cycle − Σ(stage durations)</c> over the same
    /// population. Both fields are <c>null</c> when the window contains
    /// no delivered issues with a defined, strictly positive cycle.
    /// Issues with zero wait or zero gap contribute zero to the averages
    /// rather than being excluded.
    /// </summary>
    public sealed record StageDurationWaitBreakout(
        double? AverageApprovalGateWaitSeconds,
        double? AverageInactiveGapSeconds);

    /// <summary>
    /// The result of <see cref="GetStageDurationsAsync"/>. The stages are
    /// returned in workflow stage order (the configured profile's
    /// definition). Stages reached by no delivered issue in the window
    /// are absent — a fabricated zero would mislead the consuming chart.
    /// <see cref="FlowEfficiencyRatio"/> is the population-weighted
    /// Σ activeWork ÷ Σ cycleTime over issues with a defined, strictly
    /// positive cycle (issues with undefined or zero cycle are excluded).
    /// </summary>
    public sealed record StageDurationResult(
        StageDurationWindow Window,
        IReadOnlyList<StageDurationStageAggregate> Stages,
        double? FlowEfficiencyRatio,
        StageDurationWaitBreakout WaitBreakout);

    // CloudEvents reverse-DNS bus types that mark a terminal transition.
    // <c>com.mohist.issue.completed</c> → <c>completed</c> (Done).
    // <c>com.mohist.issue.cancelled</c> → <c>failed</c> (Cancelled).
    internal const string CompletedType = EventCatalog.ReverseDns.IssueCompleted;
    internal const string CancelledType = EventCatalog.ReverseDns.IssueCancelled;
    internal const string IssueSourcePrefix = "/mohist/issues/";

    /// <summary>
    /// Buckets the project's terminal-issue transitions (<c>completed</c>
    /// and <c>cancelled</c>) from the durable <c>IssueEvents</c> table by
    /// <c>IssueEvents.Time</c>, not by issue <c>updatedAt</c>. The window
    /// is fixed: <paramref name="bucket"/> = <see cref="CompletionBucket.Day"/>
    /// returns 30 trailing UTC days; <see cref="CompletionBucket.Week"/>
    /// returns 12 trailing ISO weeks (Mon-anchored, UTC). Every bucket in
    /// the window is emitted (zeros included). Issue counts are distinct
    /// per bucket — an issue with multiple terminal events of the same
    /// Type in the same bucket counts once.
    /// <para>
    /// In addition to the per-bucket series, the result includes a
    /// <see cref="CompletionBucketsResult.CurrentTotal"/> over the current
    /// window and a <see cref="CompletionBucketsResult.PreviousTotal"/> over
    /// the immediately-preceding window of the same length
    /// (<c>[now − 2W, now − W]</c>). Both totals use the same
    /// latest-terminal-event classification as the per-bucket series. The
    /// totals carry a <c>SampleCount</c> discriminator so a zero-sample
    /// (empty) window is distinguishable from a genuine zero-completion
    /// window.
    /// </para>
    /// </summary>
    public async Task<CompletionBucketsResult> GetCompletionBucketsAsync(
        string projectId,
        CompletionBucket bucket,
        DateTimeOffset now)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Resolve the project's issue sources once. IssueEvents has no
        // indexed projectId column, so we constrain on Source in the
        // computed set of "/mohist/issues/{id}" URIs.
        var projectIssueIds = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => row.IssueId)
            .ToListAsync();
        var projectSources = projectIssueIds
            .Select(id => IssueSourcePrefix + id)
            .ToList();

        DateTimeOffset windowFrom;
        DateTimeOffset windowTo;
        DateTimeOffset previousFrom;
        DateTimeOffset previousTo;
        IReadOnlyList<DateOnly> boundaries;

        if (bucket == CompletionBucket.Day)
        {
            // 30 trailing UTC days inclusive of today.
            var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
            windowFrom = new DateTimeOffset(today.AddDays(-29).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            windowTo = new DateTimeOffset(today.AddDays(1).ToDateTime(new TimeOnly(0, 0)), TimeSpan.Zero);
            // Previous window is the same length immediately preceding the
            // current window: [today − 59d, today − 29d].
            previousFrom = new DateTimeOffset(today.AddDays(-59).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            previousTo = windowFrom;
            boundaries = Enumerable.Range(0, 30)
                .Select(i => today.AddDays(-29 + i))
                .ToList();
        }
        else
        {
            // 12 trailing ISO weeks inclusive of the current week.
            var currentWeek = ISOWeekHelper.StartOfIsoWeek(now.UtcDateTime);
            var firstWeek = currentWeek.AddDays(-7 * 11);
            windowFrom = new DateTimeOffset(firstWeek, TimeSpan.Zero);
            windowTo = new DateTimeOffset(currentWeek.AddDays(7), TimeSpan.Zero);
            // Previous window: 12 trailing ISO weeks immediately preceding
            // the current 12-week window, same length.
            var previousFirstWeek = firstWeek.AddDays(-7 * 12);
            previousFrom = new DateTimeOffset(previousFirstWeek, TimeSpan.Zero);
            previousTo = windowFrom;
            boundaries = Enumerable.Range(0, 12)
                .Select(i => DateOnly.FromDateTime(firstWeek.AddDays(7 * i)))
                .ToList();
        }

        var points = Enumerable.Range(0, boundaries.Count)
            .Select(i => new CompletionBucketPoint(
                Boundary: boundaries[i].ToString("yyyy-MM-dd"),
                Completed: 0,
                Failed: 0))
            .ToList();

        if (projectSources.Count == 0)
        {
            return new CompletionBucketsResult(
                Bucket: bucket == CompletionBucket.Day ? "day" : "week",
                WindowFrom: windowFrom,
                WindowTo: windowTo,
                Buckets: points,
                CurrentTotal: new CompletionTotal(Completed: 0, Failed: 0, SampleCount: 0),
                PreviousTotal: new CompletionTotal(Completed: 0, Failed: 0, SampleCount: 0));
        }

        // Pull terminal events for this project's issue sources, choose
        // each issue's latest terminal event, then apply the window and
        // bucket in-memory. This keeps reopened/recompleted issues from
        // leaving stale counts in earlier buckets.
        // EF Core SQLite cannot translate a `DateTimeOffset` comparison
        // against a TEXT column, so we fetch all events first and
        // filter the time + type + project-source predicates in memory.
        // At v1 volumes (≤ 30/12 buckets per project) the candidate set
        // is small; profiling per design D-OQ2 will tell us whether we
        // need an explicit index on (Type, Time) later.
        var candidates = await db.IssueEvents.AsNoTracking()
            .Select(e => new { e.Source, e.Type, e.Time, e.Id })
            .ToListAsync();
        var sourceSet = new HashSet<string>(projectSources, StringComparer.Ordinal);
        var terminalTypes = new HashSet<string>(StringComparer.Ordinal) { CompletedType, CancelledType };
        var latestTerminalByIssue = candidates
            .Where(r => sourceSet.Contains(r.Source)
                && terminalTypes.Contains(r.Type))
            .GroupBy(r => r.Source, StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(r => r.Time)
                .ThenByDescending(r => r.Id)
                .First())
            .ToList();

        var indexByBoundary = boundaries
            .Select((b, i) => (Boundary: b, Index: i))
            .ToDictionary(t => t.Boundary, t => t.Index);

        var currentTotal = new CompletionTotal(Completed: 0, Failed: 0, SampleCount: 0);
        var previousTotal = new CompletionTotal(Completed: 0, Failed: 0, SampleCount: 0);

        foreach (var row in latestTerminalByIssue)
        {
            // Bucket membership is anchored on terminal event time, using
            // the same latest-terminal-event classification. An issue's
            // latest terminal event may land in either window (current or
            // previous); its contribution is counted exactly once, in the
            // window that holds it.
            if (row.Time >= windowFrom && row.Time < windowTo)
            {
                if (row.Type == CompletedType)
                {
                    currentTotal = currentTotal with { Completed = currentTotal.Completed + 1 };
                }
                else
                {
                    currentTotal = currentTotal with { Failed = currentTotal.Failed + 1 };
                }
                currentTotal = currentTotal with { SampleCount = currentTotal.SampleCount + 1 };

                var boundary = bucket == CompletionBucket.Day
                    ? DateOnly.FromDateTime(row.Time.UtcDateTime.Date)
                    : DateOnly.FromDateTime(ISOWeekHelper.StartOfIsoWeek(row.Time.UtcDateTime));
                if (indexByBoundary.TryGetValue(boundary, out var idx))
                {
                    if (row.Type == CompletedType)
                    {
                        points[idx] = points[idx] with { Completed = points[idx].Completed + 1 };
                    }
                    else
                    {
                        points[idx] = points[idx] with { Failed = points[idx].Failed + 1 };
                    }
                }
            }
            else if (row.Time >= previousFrom && row.Time < previousTo)
            {
                if (row.Type == CompletedType)
                {
                    previousTotal = previousTotal with { Completed = previousTotal.Completed + 1 };
                }
                else
                {
                    previousTotal = previousTotal with { Failed = previousTotal.Failed + 1 };
                }
                previousTotal = previousTotal with { SampleCount = previousTotal.SampleCount + 1 };
            }
        }

        return new CompletionBucketsResult(
            Bucket: bucket == CompletionBucket.Day ? "day" : "week",
            WindowFrom: windowFrom,
            WindowTo: windowTo,
            Buckets: points,
            CurrentTotal: currentTotal,
            PreviousTotal: previousTotal);
    }

    /// <summary>
    /// One trailing window in the quality aggregation result.
    /// <see cref="SampleCount"/> distinguishes a true zero-sample
    /// window (null rates) from a window with a genuine perfect score.
    /// </summary>
    public sealed record QualityMetricsWindow(
        DateTimeOffset From,
        DateTimeOffset To,
        int SampleCount,
        double? FirstTimeRightRate,
        IReadOnlyList<QualityStageReworkAggregate> Stages);

    /// <summary>
    /// Per-stage rework aggregate for one trailing window.
    /// <see cref="EnteredCount"/> is the denominator; a null rate means
    /// no shipped-in-window issue entered the stage.
    /// </summary>
    public sealed record QualityStageReworkAggregate(
        string Stage,
        int EnteredCount,
        double? ReworkRate);

    /// <summary>
    /// The previous-adjacent-window first-time-right rate. <see cref="SampleCount"/>
    /// is the number of shipped-in-window issues and is the empty
    /// discriminator: <c>SampleCount == 0</c> means no shipped issues fell
    /// in the window (the defined empty result, structurally distinguishable
    /// from a genuine <c>0</c> or <c>1</c> rate). <see cref="FirstTimeRightRate"/>
    /// is the arithmetic mean of the per-issue FTR classification
    /// (<c>true</c>/<c>false</c>) over the same population. Computed over
    /// <c>[now − 2W, now − W]</c> using the identical ship-time windowing
    /// and first-time-right classification the current 30-day window uses.
    /// </summary>
    public sealed record QualityPreviousWindow(
        int SampleCount,
        double? FirstTimeRightRate);

    /// <summary>
    /// The result of <see cref="GetQualityAsync"/>. Both the current 30-day
    /// window and the immediately-preceding 30-day window are returned
    /// together so callers can derive a percentage-point delta in a single
    /// read. <see cref="Window7d"/> and <see cref="Trend"/> are preserved
    /// unchanged. <see cref="PreviousWindow"/> is strictly additive: it is
    /// the previous-window FTR rate plus its <see cref="QualityPreviousWindow.SampleCount"/>
    /// discriminator; the existing 7-day and 30-day single-point rates,
    /// the per-stage rework rates, and the per-bucket trend series are
    /// unchanged.
    /// </summary>
    public sealed record QualityMetricsResult(
        QualityMetricsWindow Window7d,
        QualityMetricsWindow Window30d,
        QualityPreviousWindow PreviousWindow,
        QualityTrend Trend);

    /// <summary>
    /// One pre-sized per-day bucket in the quality trend. <see cref="Boundary"/>
    /// is the ISO calendar day (yyyy-MM-dd, UTC). <see cref="SampleCount"/>
    /// distinguishes a true zero-sample bucket (null rates) from a bucket
    /// whose computed rates happened to be 0 or 1.
    /// </summary>
    public sealed record QualityTrendPoint(
        string Boundary,
        int SampleCount,
        double? FirstTimeRightRate,
        double? ReworkRate);

    /// <summary>
    /// The pre-sized 30-day daily trend returned alongside the trailing
    /// window scalars. <see cref="Points"/> has length 30, anchored on
    /// <see cref="Window30dFrom"/> and ordered oldest-first.
    /// </summary>
    public sealed record QualityTrend(
        string Bucket,
        DateTimeOffset Window30dFrom,
        DateTimeOffset Window30dTo,
        IReadOnlyList<QualityTrendPoint> Points);

    /// <summary>
    /// Aggregates AI quality signals (first-time-right rate and per-stage
    /// rework rate) over trailing 7-day and 30-day windows. Only issues
    /// whose status is <see cref="IssueStatus.Done"/> participate. Window
    /// membership is anchored on the <c>com.mohist.issue.completed</c>
    /// event time, matching <see cref="GetCompletionBucketsAsync"/>. Rates
    /// are computed from existing workflow-run state and durable check events;
    /// no new data collection is introduced.
    /// <para>
    /// In addition to the 7-day and 30-day single-point rates, the result
    /// also carries the previous-adjacent-window
    /// <see cref="QualityMetricsResult.PreviousWindow"/> first-time-right
    /// rate over <c>[now − 60d, now − 30d)</c> — the same length as the
    /// current 30-day window and immediately preceding it, using the
    /// identical ship-time windowing and first-time-right classification.
    /// The previous-window rate is the arithmetic mean of the per-issue
    /// FTR flag over shipped-in-previous-window issues. The window's
    /// <see cref="QualityPreviousWindow.SampleCount"/> is the empty
    /// discriminator (<c>0</c> ⟹ no shipped issues fell in the window,
    /// structurally distinguishable from a genuine <c>0</c> or <c>1</c>
    /// rate). The two windows are evaluated independently: the current
    /// window can be non-empty while the previous window is empty and
    /// vice-versa.
    /// </para>
    /// </summary>
    public async Task<QualityMetricsResult> GetQualityAsync(
        string projectId,
        DateTimeOffset now)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var window7dFrom = now.AddDays(-7);
        var window7dTo = now;
        var window30dFrom = now.AddDays(-30);
        var window30dTo = now;

        // Previous 30-day window — same length as the current window,
        // immediately preceding it. The upper bound is exclusive so the
        // boundary moment belongs to the current window (matches the
        // delivery-time surface's [now − 2W, now − W) shape).
        var previous30dFrom = now.AddDays(-60);
        var previous30dTo = window30dFrom;

        // Pre-sized 30 UTC calendar-day buckets inclusive of today. Every
        // bucket is emitted so the consuming chart's x-axis never compresses.
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var trendStart = today.AddDays(-29);
        var trendBoundaries = Enumerable.Range(0, 30)
            .Select(i => trendStart.AddDays(i))
            .ToList();
        var trendIndexByBoundary = trendBoundaries
            .Select((b, i) => (Boundary: b, Index: i))
            .ToDictionary(t => t.Boundary, t => t.Index);
        var trendBuckets = new QualityTrendAccumulator[trendBoundaries.Count];
        for (var i = 0; i < trendBuckets.Length; i++)
            trendBuckets[i] = new QualityTrendAccumulator();

        // Load the project's issues and their workflow runs. We need the
        // raw WorkflowRun (not the projected view) because RepairCount is
        // dropped by WorkflowStatusMapper. WorkflowRunEvents provide the
        // durable history that mutable stage snapshots lose on rerun/retry.
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();

        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);
        var issues = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project: null, projectDefaultTemplateId, disabledIds)))
            .ToList();

        // Resolve lifecycle workflow runs and ship time per Done issue from durable events.
        var projectIssueIds = issues.Select(i => i.Id).ToList();
        var projectSources = projectIssueIds
            .Select(id => IssueSourcePrefix + id)
            .ToList();

        var shipTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var runIdsByIssue = issues.ToDictionary(
            i => i.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        if (projectSources.Count > 0)
        {
            var sourceSet = new HashSet<string>(projectSources, StringComparer.Ordinal);
            var candidates = await db.IssueEvents.AsNoTracking()
                .Select(e => new { e.Source, e.Type, e.Time, e.Data })
                .ToListAsync();
            foreach (var e in candidates)
            {
                if (!sourceSet.Contains(e.Source)) continue;

                var issueId = e.Source[IssueSourcePrefix.Length..];

                if (e.Type == WorkStartedType)
                {
                    var workflowRunId = ReadWorkflowRunId(e.Data);
                    if (!string.IsNullOrWhiteSpace(workflowRunId) && runIdsByIssue.TryGetValue(issueId, out var ids))
                        ids.Add(workflowRunId);
                }
                else if (e.Type == CompletedType)
                {
                    // Keep the latest completion time if multiple exist.
                    if (!shipTimes.TryGetValue(issueId, out var existing) || e.Time > existing)
                        shipTimes[issueId] = e.Time;

                    var workflowRunId = ReadWorkflowRunId(e.Data);
                    if (!string.IsNullOrWhiteSpace(workflowRunId) && runIdsByIssue.TryGetValue(issueId, out var ids))
                        ids.Add(workflowRunId);
                }
            }
        }

        foreach (var issue in issues)
        {
            if (!string.IsNullOrWhiteSpace(issue.WorkflowRunId) && runIdsByIssue.TryGetValue(issue.Id, out var ids))
                ids.Add(issue.WorkflowRunId);
        }

        var allRunIds = runIdsByIssue.Values.SelectMany(ids => ids).ToArray();
        var runs = await LoadWorkflowRunsAsync(db, allRunIds);
        var eventFactsByRun = await LoadWorkflowRunEventFactsAsync(db, allRunIds);

        // Single-pass classification and bucketing.
        var window7d = new QualityAccumulator();
        var window30d = new QualityAccumulator();
        var previous30d = new QualityFirstTimeRightAccumulator();

        foreach (var issue in issues)
        {
            if (issue.Status != MohistDefaultWorkflowProjection.IssueStatusName(IssueStatus.Done)) continue;
            if (!shipTimes.TryGetValue(issue.Id, out var shipTime)) continue;

            if (!runIdsByIssue.TryGetValue(issue.Id, out var issueRunIds)) continue;

            var distinctRunIds = issueRunIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var lifecycleRuns = new List<WorkflowRun>();
            var hasUnknownRun = false;
            foreach (var runId in distinctRunIds)
            {
                if (runs.TryGetValue(runId, out var run))
                    lifecycleRuns.Add(run);
                else
                    hasUnknownRun = true;
            }

            var lifecycleEvents = distinctRunIds
                .Where(eventFactsByRun.ContainsKey)
                .SelectMany(runId => eventFactsByRun[runId])
                .ToList();

            var (isFirstTimeRight, stageRework) = lifecycleRuns.Count == 0 || hasUnknownRun
                ? (false, new Dictionary<string, bool>(StringComparer.Ordinal))
                : ClassifyRuns(lifecycleRuns, lifecycleEvents);

            if (shipTime >= window7dFrom && shipTime <= window7dTo)
                Accumulate(window7d, isFirstTimeRight, stageRework);

            if (shipTime >= window30dFrom && shipTime <= window30dTo)
            {
                Accumulate(window30d, isFirstTimeRight, stageRework);

                var shipDay = DateOnly.FromDateTime(shipTime.UtcDateTime.Date);
                if (trendIndexByBoundary.TryGetValue(shipDay, out var trendIdx))
                    Accumulate(trendBuckets[trendIdx], isFirstTimeRight, stageRework);
            }
            else if (shipTime >= previous30dFrom && shipTime < previous30dTo)
            {
                // Previous window contributes ONLY the FTR scalar; the
                // per-stage rework breakdown and per-day trend series
                // are current-window-only contracts and stay unchanged.
                Accumulate(previous30d, isFirstTimeRight);
            }
        }

        return new QualityMetricsResult(
            BuildWindow(window7dFrom, window7dTo, window7d),
            BuildWindow(window30dFrom, window30dTo, window30d),
            BuildPreviousWindow(previous30d),
            BuildTrend(window30dFrom, window30dTo, trendBoundaries, trendBuckets));
    }

    private static QualityPreviousWindow BuildPreviousWindow(QualityFirstTimeRightAccumulator accumulator)
    {
        var sampleCount = accumulator.SampleCount;
        double? firstTimeRightRate = sampleCount == 0
            ? null
            : (double)accumulator.FirstTimeRightCount / sampleCount;
        return new QualityPreviousWindow(sampleCount, firstTimeRightRate);
    }

    private sealed class QualityFirstTimeRightAccumulator
    {
        public int SampleCount { get; set; }
        public int FirstTimeRightCount { get; set; }
    }

    private static void Accumulate(
        QualityFirstTimeRightAccumulator accumulator,
        bool isFirstTimeRight)
    {
        accumulator.SampleCount++;
        if (isFirstTimeRight) accumulator.FirstTimeRightCount++;
    }

    private static (bool IsFirstTimeRight, IReadOnlyDictionary<string, bool> StageRework) ClassifyRuns(
        IReadOnlyCollection<WorkflowRun> runs,
        IReadOnlyCollection<WorkflowRunEventFact> events)
    {
        var isFirstTimeRight = true;
        var stageRework = new Dictionary<string, bool>(StringComparer.Ordinal);
        var checkEventCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var run in runs)
        {
            foreach (var stage in run.Stages)
            {
                if (!stage.Initialized) continue;

                var stageHasRepair = false;
                foreach (var check in stage.Checks)
                {
                    if (check.RepairCount > 0)
                    {
                        stageHasRepair = true;
                        isFirstTimeRight = false;
                    }
                }

                stageRework[stage.Id] = stageRework.GetValueOrDefault(stage.Id) || stageHasRepair;
            }
        }

        foreach (var fact in events.OrderBy(e => e.RunId, StringComparer.Ordinal).ThenBy(e => e.Sequence))
        {
            if (string.IsNullOrWhiteSpace(fact.Stage)) continue;

            stageRework.TryAdd(fact.Stage, false);

            if (string.Equals(fact.Type, EventCatalog.ReverseDns.RepairScheduled, StringComparison.Ordinal))
            {
                stageRework[fact.Stage] = true;
                isFirstTimeRight = false;
                continue;
            }

            if (!IsCheckRunEvent(fact.Type) || string.IsNullOrWhiteSpace(fact.CheckName)) continue;

            var key = string.Join('\u001f', fact.RunId, fact.Stage, fact.CheckName);
            var count = checkEventCounts.GetValueOrDefault(key) + 1;
            checkEventCounts[key] = count;
            if (count > 1)
            {
                stageRework[fact.Stage] = true;
                isFirstTimeRight = false;
            }
        }

        return (isFirstTimeRight, stageRework);
    }

    private static bool IsCheckRunEvent(string type) =>
        string.Equals(type, EventCatalog.ReverseDns.CheckPassed, StringComparison.Ordinal)
        || string.Equals(type, EventCatalog.ReverseDns.CheckFailed, StringComparison.Ordinal)
        || string.Equals(type, EventCatalog.ReverseDns.CheckPending, StringComparison.Ordinal);

    private sealed record WorkflowRunEventFact(
        string RunId,
        long Sequence,
        string Type,
        string? Stage,
        string? CheckName);

    private sealed class QualityAccumulator
    {
        public int SampleCount { get; set; }
        public int FirstTimeRightCount { get; set; }
        public Dictionary<string, int> EnteredByStage { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ReworkedByStage { get; } = new(StringComparer.Ordinal);
    }

    private static void Accumulate(
        QualityAccumulator accumulator,
        bool isFirstTimeRight,
        IReadOnlyDictionary<string, bool> stageRework)
    {
        accumulator.SampleCount++;
        if (isFirstTimeRight) accumulator.FirstTimeRightCount++;

        foreach (var (stage, reworked) in stageRework)
        {
            accumulator.EnteredByStage[stage] = accumulator.EnteredByStage.GetValueOrDefault(stage) + 1;
            if (reworked)
                accumulator.ReworkedByStage[stage] = accumulator.ReworkedByStage.GetValueOrDefault(stage) + 1;
        }
    }

    private static QualityMetricsWindow BuildWindow(
        DateTimeOffset from,
        DateTimeOffset to,
        QualityAccumulator accumulator)
    {
        double? firstTimeRightRate = accumulator.SampleCount == 0
            ? null
            : (double)accumulator.FirstTimeRightCount / accumulator.SampleCount;

        var observedStages = accumulator.EnteredByStage.Keys
            .Where(stage => !QualityStageOrder.Contains(stage, StringComparer.Ordinal))
            .OrderBy(stage => stage, StringComparer.Ordinal);

        var stages = QualityStageOrder
            .Concat(observedStages)
            .Select(stage =>
            {
                var entered = accumulator.EnteredByStage.GetValueOrDefault(stage);
                var reworked = accumulator.ReworkedByStage.GetValueOrDefault(stage);
                return new QualityStageReworkAggregate(
                    stage,
                    entered,
                    entered == 0 ? null : (double)reworked / entered);
            })
            .ToList();

        return new QualityMetricsWindow(from, to, accumulator.SampleCount, firstTimeRightRate, stages);
    }

    private sealed class QualityTrendAccumulator
    {
        public int SampleCount { get; set; }
        public int FirstTimeRightCount { get; set; }
        public int ReworkedAtAnyStageCount { get; set; }
    }

    private static void Accumulate(
        QualityTrendAccumulator accumulator,
        bool isFirstTimeRight,
        IReadOnlyDictionary<string, bool> stageRework)
    {
        accumulator.SampleCount++;
        if (isFirstTimeRight) accumulator.FirstTimeRightCount++;
        if (stageRework.Values.Any(v => v)) accumulator.ReworkedAtAnyStageCount++;
    }

    private QualityTrend BuildTrend(
        DateTimeOffset window30dFrom,
        DateTimeOffset window30dTo,
        IReadOnlyList<DateOnly> boundaries,
        IReadOnlyList<QualityTrendAccumulator> buckets)
    {
        var points = new QualityTrendPoint[boundaries.Count];
        for (var i = 0; i < boundaries.Count; i++)
        {
            var bucket = buckets[i];
            var sampleCount = bucket.SampleCount;
            double? firstTimeRightRate = sampleCount == 0
                ? null
                : (double)bucket.FirstTimeRightCount / sampleCount;
            double? reworkRate = sampleCount == 0
                ? null
                : (double)bucket.ReworkedAtAnyStageCount / sampleCount;
            points[i] = new QualityTrendPoint(
                Boundary: boundaries[i].ToString("yyyy-MM-dd"),
                SampleCount: sampleCount,
                FirstTimeRightRate: firstTimeRightRate,
                ReworkRate: reworkRate);
        }

        return new QualityTrend(
            Bucket: "day",
            Window30dFrom: window30dFrom,
            Window30dTo: window30dTo,
            Points: points);
    }

    private async Task<Dictionary<string, WorkflowRun>> LoadWorkflowRunsAsync(
        MohistDbContext db,
        IEnumerable<string> workflowRunIds)
    {
        var ids = workflowRunIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return [];

        var runRows = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => ids.Contains(row.WorkflowRunId))
            .ToListAsync();

        var runs = new Dictionary<string, WorkflowRun>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.State);
            if (run is not null)
                runs[row.WorkflowRunId] = run;
        }
        return runs;
    }

    private async Task<Dictionary<string, List<WorkflowRunEventFact>>> LoadWorkflowRunEventFactsAsync(
        MohistDbContext db,
        IEnumerable<string> workflowRunIds)
    {
        var runIds = workflowRunIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runIds.Length == 0) return new Dictionary<string, List<WorkflowRunEventFact>>(StringComparer.Ordinal);

        var sourcesByRunId = runIds.ToDictionary(
            id => WorkflowRunEventPersistence.WorkflowRunSource(id),
            id => id,
            StringComparer.Ordinal);
        var sources = sourcesByRunId.Keys.ToArray();

        var rows = await db.WorkflowRunEvents
            .AsNoTracking()
            .Where(row => sources.Contains(row.Source) && QualityWorkflowEventTypes.Contains(row.Type))
            .Select(row => new { row.Source, row.Id, row.Type, row.Data })
            .ToListAsync();

        var facts = new Dictionary<string, List<WorkflowRunEventFact>>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(row => row.Source, StringComparer.Ordinal).ThenBy(row => row.Id))
        {
            if (!sourcesByRunId.TryGetValue(row.Source, out var runId)) continue;

            var fact = new WorkflowRunEventFact(
                runId,
                row.Id,
                row.Type,
                ReadWorkflowEventStage(row.Data),
                ReadWorkflowEventCheckName(row.Data));

            if (!facts.TryGetValue(runId, out var runFacts))
            {
                runFacts = [];
                facts[runId] = runFacts;
            }
            runFacts.Add(fact);
        }

        return facts;
    }

    private static string? ReadWorkflowRunId(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("workflowRunId", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        if (data.TryGetProperty("WorkflowRunId", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            return pascal.GetString();
        return null;
    }

    private static string? ReadWorkflowEventStage(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("stage", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        if (data.TryGetProperty("Stage", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            return pascal.GetString();
        return null;
    }

    private static string? ReadWorkflowEventCheckName(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (data.TryGetProperty("checkName", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        if (data.TryGetProperty("CheckName", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            return pascal.GetString();
        return null;
    }

    /// <summary>
    /// Aggregates approval-gate wait times (requestedAt → respondedAt) for
    /// completed approvals (`approved` or `rejected`) whose <c>respondedAt</c>
    /// falls within the trailing 7-day window [now - 7d, now]. Pending
    /// (`awaiting`) approvals are excluded — they have no <c>respondedAt</c>
    /// and surface separately as attention items. Statistics are computed in
    /// memory because EF Core SQLite cannot translate <c>DateTimeOffset</c>
    /// comparisons against the TEXT <c>WorkflowRuns.State</c> column.
    /// </summary>
    public async Task<ApprovalWaitResult> GetApprovalWaitAsync(
        string projectId,
        DateTimeOffset now)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var windowFrom = now.AddDays(-7);
        var windowTo = now;

        // Resolve the project's issue set from the indexed Issues table,
        // then load each issue's workflow-run state so approval projection
        // semantics stay shared with the read model.
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();

        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);
        var issues = IssueRowMapper.ByNumber(rows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project: null, projectDefaultTemplateId, disabledIds)))
            .ToList();

        var workflows = await LoadWorkflowStatesAsync(db, issues);

        var samples = new List<double>(issues.Count);
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null || !workflows.TryGetValue(issue.WorkflowRunId, out var workflow)) continue;

            foreach (var approval in MohistDefaultWorkflowProjection.StageApprovals(workflow))
            {
                if (!approval.RespondedAt.HasValue) continue;
                if (!string.Equals(approval.Status, "approved", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(approval.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var respondedAt = new DateTimeOffset(approval.RespondedAt.Value.ToUniversalTime(), TimeSpan.Zero);
                if (respondedAt < windowFrom || respondedAt > windowTo) continue;

                var waitSeconds = (approval.RespondedAt.Value - approval.RequestedAt).TotalSeconds;
                samples.Add(waitSeconds);
            }
        }

        if (samples.Count == 0)
        {
            return new ApprovalWaitResult(
                new ApprovalWaitWindow(windowFrom, windowTo),
                SampleCount: 0,
                AverageSeconds: null,
                MedianSeconds: null,
                MaxSeconds: null);
        }

        samples.Sort();
        var average = samples.Average();
        var max = samples[^1];
        var median = samples.Count % 2 == 1
            ? samples[samples.Count / 2]
            : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2.0;

        return new ApprovalWaitResult(
            new ApprovalWaitWindow(windowFrom, windowTo),
            SampleCount: samples.Count,
            AverageSeconds: average,
            MedianSeconds: median,
            MaxSeconds: max);
    }

    /// <summary>
    /// Per-issue lead-time and cycle-time series for delivered issues in a
    /// fixed trailing window. Only issues that have reached <c>done</c>
    /// (<see cref="IssueStatus.Done"/>) with a non-null
    /// <c>CompletedAt</c> contribute a sample; <c>cancelled</c>
    /// (<see cref="IssueStatus.Cancelled"/>) issues are excluded. Window
    /// membership is anchored on <c>Issue.CompletedAt</c>, which the
    /// <c>issue-completion-timestamp</c> spec already defines as the
    /// latest terminal <c>done</c> moment (reopen-and-re-complete
    /// therefore re-anchors the point at the latest completion, and the
    /// prior completion is not retained as a separate sample). The
    /// window length is fixed at <c>30</c> days and is not
    /// user-configurable (<c>now</c> is the only injected parameter,
    /// satisfying the no-wall-clock rule).
    /// <para>
    /// Lead time = <c>CompletedAt − CreatedAt</c>; <c>CreatedAt</c> is
    /// the aggregate's <c>init</c>-only/immutable field, so it cannot
    /// drift across retries. Cycle time = <c>CompletedAt − earliest
    /// IssueWorkStarted</c> per issue — a scan over durable
    /// <c>IssueEvents</c> rows (same idiom as <see cref="GetQualityAsync"/>,
    /// but <c>Min</c> instead of "latest"), preserving the earliest
    /// work-start across retries. When an issue has no recorded
    /// <c>IssueWorkStarted</c>, <c>CycleDays</c> is <c>null</c>
    /// (undefined), distinguishable from a genuine
    /// <c>CycleDays == 0</c> zero-duration cycle. The returned series
    /// is at per-issue granularity (no pre-aggregation) and is ordered by
    /// <c>CompletedAt</c> ascending so the consuming chart can plot
    /// against the completion-date axis directly.
    /// </para>
    /// <para>
    /// In addition to the per-issue series, the result includes a
    /// <see cref="DeliveryTimeResult.PreviousAverageCycleDays"/> over the
    /// immediately-preceding window of the same length
    /// (<c>[now − 2W, now − W]</c>), computed with the identical
    /// cycle-time definition and completion-time windowing as the current
    /// window. The previous-window average is the arithmetic mean of
    /// <c>CycleDays</c> over delivered issues that fall in the previous
    /// window and have a defined cycle (issues whose cycle is undefined
    /// <c>null</c> do not contribute). When the previous window contains
    /// no such issues, the average is the defined empty result
    /// (<c>null</c>), structurally distinguishable from a genuine
    /// zero-duration average and evaluated independently of the current
    /// window.
    /// </para>
    /// </summary>
    public async Task<DeliveryTimeResult> GetDeliveryTimesAsync(
        string projectId,
        DateTimeOffset now)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Fixed 30-day trailing window keyed on the completion moment.
        // `now` is the injected anchor; never read the wall clock here.
        var windowFrom = now.AddDays(-30);
        var windowTo = now;

        // Previous window: same length, immediately preceding the current
        // window. The two windows are evaluated independently; this gives
        // the consumer the cycle-time baseline it needs to derive a trend
        // in a single read.
        var previousWindowFrom = now.AddDays(-60);
        var previousWindowTo = windowFrom;

        // Resolve the project's issue set. We use `db.Issues` directly
        // (not the projected read model) so the local-filters below see
        // the raw `Status`/`CreatedAt`/`CompletedAt` fields without
        // enrichment overhead — the metrics surface never renders
        // an issue row, only a duration tuple.
        var issueRows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();

        if (issueRows.Count == 0)
        {
            return new DeliveryTimeResult(
                Array.Empty<DeliveryTimePoint>(),
                PreviousAverageCycleDays: null);
        }

        var issuesById = IssueRowMapper.ById(issueRows, projectId)
            .ToDictionary(i => i.Id, StringComparer.Ordinal);

        var projectIssueIds = issuesById.Keys.ToList();
        var projectSources = projectIssueIds
            .Select(id => IssueSourcePrefix + id)
            .ToList();

        // Scan durable `IssueEvents` for the project's work-started
        // events. EF Core SQLite cannot translate `DateTimeOffset`
        // comparisons against the TEXT `Time` column, so we pull the
        // candidate rows first and filter in memory — the same idiom
        // `GetQualityAsync` already uses. Candidate set is bounded
        // by the project's issue count; at v1 volumes this is small.
        var earliestWorkStartedByIssue = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (projectSources.Count > 0)
        {
            var sourceSet = new HashSet<string>(projectSources, StringComparer.Ordinal);
            var candidates = await db.IssueEvents.AsNoTracking()
                .Select(e => new { e.Source, e.Type, e.Time })
                .ToListAsync();

            foreach (var e in candidates)
            {
                if (e.Type != WorkStartedType) continue;
                if (!sourceSet.Contains(e.Source)) continue;

                var issueId = e.Source[IssueSourcePrefix.Length..];
                if (!earliestWorkStartedByIssue.TryGetValue(issueId, out var existing) || e.Time < existing)
                {
                    earliestWorkStartedByIssue[issueId] = e.Time;
                }
            }
        }

        var points = new List<DeliveryTimePoint>(issuesById.Count);
        // Previous-window cycle accumulator. Only `defined`-cycle issues
        // contribute; the count is tracked separately so the empty
        // (zero-sample) and a genuine zero-duration mean are
        // distinguishable.
        double previousCycleSum = 0;
        int previousCycleCount = 0;

        foreach (var issue in issuesById.Values)
        {
            // Only delivered issues participate. Cancelled issues carry
            // no lead/cycle contribution per the spec; still-in-flight
            // issues (Backlog/InProgress) lack `CompletedAt` and are
            // skipped here.
            if (issue.Status != IssueStatus.Done) continue;
            if (issue.CompletedAt is null) continue;

            var completedAtDt = DateTime.SpecifyKind(issue.CompletedAt.Value, DateTimeKind.Utc);

            double? cycleDays = null;
            if (earliestWorkStartedByIssue.TryGetValue(issue.Id, out var firstStart))
            {
                var cycleSpan = completedAtDt - firstStart.UtcDateTime;
                cycleDays = cycleSpan.TotalDays;
            }

            if (completedAtDt >= windowFrom.UtcDateTime && completedAtDt <= windowTo.UtcDateTime)
            {
                var createdAtUtc = DateTime.SpecifyKind(issue.CreatedAt, DateTimeKind.Utc);
                var leadSpan = completedAtDt - createdAtUtc;
                var leadDays = leadSpan.TotalDays;

                points.Add(new DeliveryTimePoint(
                    IssueNumber: issue.Number,
                    CompletedAt: new DateTimeOffset(completedAtDt, TimeSpan.Zero),
                    LeadDays: leadDays,
                    CycleDays: cycleDays));
            }
            else if (cycleDays.HasValue
                && completedAtDt >= previousWindowFrom.UtcDateTime
                && completedAtDt < previousWindowTo.UtcDateTime)
            {
                previousCycleSum += cycleDays.Value;
                previousCycleCount++;
            }
        }

        // Order by completion time ascending so the consuming chart can
        // plot the series directly along the x-axis (left → older,
        // right → newer).
        points.Sort(static (a, b) => a.CompletedAt.CompareTo(b.CompletedAt));

        // Empty previous window is `null`, distinguishable from a
        // genuine zero-duration average (which would require
        // `previousCycleCount > 0`).
        double? previousAverageCycleDays = previousCycleCount == 0
            ? null
            : previousCycleSum / previousCycleCount;

        return new DeliveryTimeResult(
            Points: points,
            PreviousAverageCycleDays: previousAverageCycleDays);
    }

    /// <summary>
    /// Per-stage duration distribution, flow-efficiency ratio, and wait
    /// breakout for delivered issues in the fixed 30-day trailing window
    /// anchored on completion time (shared with <see cref="GetDeliveryTimesAsync"/>).
    /// Derived purely from already-persisted workflow-run events
    /// (<c>StageStarted</c> / <c>StageCompleted</c>) and lifecycle events
    /// (<c>IssueWorkStarted</c> / <c>IssueCompleted</c>) — no new
    /// collection, no schema change.
    /// <para>
    /// Aggregation is in-memory (EF Core SQLite cannot translate
    /// <c>DateTimeOffset</c> against the TEXT <c>Time</c> column). For
    /// each delivered issue in the window the surface:
    /// <list type="number">
    /// <item><description>computes the issue's cycle from the earliest
    /// <c>IssueWorkStarted</c> to the persisted <c>CompletedAt</c>;</description></item>
    /// <item><description>collects <c>StageStarted</c> /
    /// <c>StageCompleted</c> events across all the issue's workflow
    /// runs (loaded via the same run-discovery pattern as
    /// <see cref="GetQualityAsync"/>) and pairs the latest
    /// <c>StageStarted</c> with the first following <c>StageCompleted</c>
    /// per stage id (invalidated earlier attempts are not averaged or
    /// summed);</description></item>
    /// <item><description>decomposes the cycle into active-work,
    /// approval-gate wait, and inactive-gap using the same approval-wait
    /// definition as <see cref="GetApprovalWaitAsync"/>; pending
    /// approvals contribute nothing;</description></item>
    /// <item><description>aggregates per-stage average / median over
    /// defined samples and computes the population-weighted flow-efficiency
    /// ratio over issues with a defined, strictly positive cycle.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The empty result (no delivered issues in the window) is a
    /// <see cref="StageDurationResult"/> with an empty
    /// <see cref="StageDurationResult.Stages"/>, a <c>null</c>
    /// <see cref="StageDurationResult.FlowEfficiencyRatio"/>, and a
    /// <see cref="StageDurationResult.WaitBreakout"/> carrying two
    /// <c>null</c> averages — distinguishable on the wire from any
    /// genuine zero via the nullable shape and zero sample counts.
    /// </para>
    /// </summary>
    public async Task<StageDurationResult> GetStageDurationsAsync(
        string projectId,
        DateTimeOffset now)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Fixed 30-day trailing window keyed on the completion moment.
        // Anchored on `Issue.CompletedAt`, mirroring delivery-time.
        var windowFrom = now.AddDays(-30);
        var windowTo = now;

        // Resolve the project's issue set.
        var issueRows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync();

        if (issueRows.Count == 0)
        {
            return BuildEmptyStageDurationResult(windowFrom, windowTo);
        }

        // Resolve the project's issue set as `IssueReadModel` so we can
        // feed `LoadWorkflowStatesAsync` (which expects the read-model
        // shape) for the approval-wait projection.
        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, projectId);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId);
        var issueReadModels = IssueRowMapper.ByNumber(issueRows, projectId)
            .Select(issue => ToReadModel(ToInfo(issue, project: null, projectDefaultTemplateId, disabledIds)))
            .ToList();

        var issuesById = issueReadModels.ToDictionary(i => i.Id, StringComparer.Ordinal);

        var projectIssueIds = issuesById.Keys.ToList();
        var projectSources = projectIssueIds
            .Select(id => IssueSourcePrefix + id)
            .ToList();

        // Resolve the workflow stage order from the project's effective
        // workflow profile so reached stages are reported in the right
        // order and any stage that exists in the profile but was not
        // reached by a delivered issue stays absent (rather than
        // appearing as a fabricated zero).
        var stageOrder = await ResolveProjectStageOrderAsync(db, projectId);

        // Scan durable `IssueEvents` for the project's work-started
        // events (to anchor cycle time) and to discover every workflow
        // run id that executed the issue (including runs from prior
        // reruns / rerun-from-stage — the same cross-run discovery
        // pattern as `GetQualityAsync`).
        var earliestWorkStartedByIssue = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var runIdsByIssue = issuesById.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        // Per-issue lifecycle event stream needed for the shared
        // attribution core (`IssueStageAttribution.Attribute`).
        // Captures WorkStarted / Completed / Cancelled / Reopened so
        // the stage-duration surface and the stage-population snapshot
        // job produce the same "latest stage" verdict for the same
        // issue.
        var lifecycleEventsByIssue = new Dictionary<string, List<IssueStageAttribution.AttributionEvent>>(StringComparer.Ordinal);

        if (projectSources.Count > 0)
        {
            var sourceSet = new HashSet<string>(projectSources, StringComparer.Ordinal);
            var candidates = await db.IssueEvents.AsNoTracking()
                .Select(e => new { e.Source, e.Type, e.Time, e.Data, e.Id })
                .ToListAsync();

            foreach (var e in candidates)
            {
                if (!sourceSet.Contains(e.Source)) continue;
                var issueId = e.Source[IssueSourcePrefix.Length..];

                // Record every lifecycle event the shared attribution
                // core consumes; the WorkStarted / Completed
                // branches below also drive the existing per-issue
                // run-id / earliest-start accumulators.
                if (e.Type == WorkStartedType
                    || e.Type == CompletedType
                    || e.Type == CancelledType
                    || e.Type == "com.mohist.issue.reopened")
                {
                    if (!lifecycleEventsByIssue.TryGetValue(issueId, out var list))
                    {
                        list = new List<IssueStageAttribution.AttributionEvent>();
                        lifecycleEventsByIssue[issueId] = list;
                    }
                    list.Add(new IssueStageAttribution.AttributionEvent(
                        Type: e.Type,
                        Time: e.Time,
                        Id: e.Id,
                        Stage: null,
                        WorkflowRunId: ReadWorkflowRunId(e.Data)));
                }

                if (e.Type == WorkStartedType)
                {
                    if (earliestWorkStartedByIssue.TryGetValue(issueId, out var existing))
                    {
                        if (e.Time < existing) earliestWorkStartedByIssue[issueId] = e.Time;
                    }
                    else
                    {
                        earliestWorkStartedByIssue[issueId] = e.Time;
                    }

                    var wrId = ReadWorkflowRunId(e.Data);
                    if (!string.IsNullOrWhiteSpace(wrId) && runIdsByIssue.TryGetValue(issueId, out var ids))
                        ids.Add(wrId);
                }
                else if (e.Type == CompletedType)
                {
                    var wrId = ReadWorkflowRunId(e.Data);
                    if (!string.IsNullOrWhiteSpace(wrId) && runIdsByIssue.TryGetValue(issueId, out var ids))
                        ids.Add(wrId);
                }
            }
        }

        // Union each issue's current WorkflowRunId with the historical
        // ids found in events, so a stage whose events live only on the
        // current run (and never produced a fresh WorkStarted) is still
        // discoverable.
        foreach (var issue in issuesById.Values)
        {
            if (!string.IsNullOrWhiteSpace(issue.WorkflowRunId) && runIdsByIssue.TryGetValue(issue.Id, out var ids))
                ids.Add(issue.WorkflowRunId);
        }

        var allRunIds = runIdsByIssue.Values
            .SelectMany(ids => ids)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Load workflow-run state for approval projection semantics across
        // the same issue run set used for stage-duration discovery.
        var workflows = await LoadWorkflowStatesAsync(db, allRunIds);

        // Load StageStarted / StageCompleted events selecting `Time` (the
        // existing `LoadWorkflowRunEventFactsAsync` projection drops
        // `Time`; this loader selects `Source/Id/Type/Time/Data` over
        // BOTH event types so the latest-attempt pairing can use the
        // durable CloudEvent timestamps).
        var stageEventsByRun = await LoadWorkflowRunStageEventsAsync(db, allRunIds);

        // Per-issue computation: stage durations, cycle components.
        var perIssue = new List<PerIssueCycleBreakdown>();
        var samplesByStage = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (var issue in issuesById.Values)
        {
            if (!string.Equals(issue.Status, "done", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(issue.CompletedAt)) continue;
            if (!DateTime.TryParse(
                issue.CompletedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var completedAtRaw))
                continue;

            var completedAtDt = DateTime.SpecifyKind(completedAtRaw, DateTimeKind.Utc);
            if (completedAtDt < windowFrom.UtcDateTime || completedAtDt > windowTo.UtcDateTime) continue;

            if (!runIdsByIssue.TryGetValue(issue.Id, out var issueRunIds)) continue;

            // Attribution core: the snapshot and the stage-duration
            // surface share the same latest-run decision. When the
            // attribution model can identify the active workflow run,
            // durations are computed from that run only so historical
            // invalidated runs cannot contribute samples.
            var lifecycleForIssue = lifecycleEventsByIssue.TryGetValue(issue.Id, out var le)
                ? le
                : (IReadOnlyList<IssueStageAttribution.AttributionEvent>)Array.Empty<IssueStageAttribution.AttributionEvent>();
            var attribution = ComputeIssueAttribution(lifecycleForIssue, issueRunIds, stageEventsByRun, stageOrder, dayEndUtc: now);
            IReadOnlyList<string> durationRunIds = !string.IsNullOrWhiteSpace(attribution.WorkflowRunId)
                ? new[] { attribution.WorkflowRunId }
                : issueRunIds;

            // Pair each reached stage with its latest attempt duration.
            // Stages with no following completion contribute no sample.
            var stageDurations = ComputeLatestAttemptStageDurations(durationRunIds, stageEventsByRun);

            // Cycle = CompletedAt - earliest IssueWorkStarted.
            double? cycleSeconds = null;
            if (earliestWorkStartedByIssue.TryGetValue(issue.Id, out var firstStart))
            {
                var span = completedAtDt - firstStart.UtcDateTime;
                cycleSeconds = span.TotalSeconds;
            }

            var approvalGateWaitSeconds = SumApprovalGateWaitSeconds(durationRunIds, workflows);

            var sumStageSeconds = stageDurations.Values.Sum();

            perIssue.Add(new PerIssueCycleBreakdown(
                StageDurations: stageDurations,
                CycleSeconds: cycleSeconds,
                ApprovalGateWaitSeconds: approvalGateWaitSeconds,
                SumStageSeconds: sumStageSeconds));

            // Accumulate per-stage defined samples. Undefined stages
            // (started-without-completion) are simply absent from
            // `stageDurations` and therefore excluded from the averages.
            foreach (var (stage, duration) in stageDurations)
            {
                if (!samplesByStage.TryGetValue(stage, out var list))
                {
                    list = new List<double>();
                    samplesByStage[stage] = list;
                }
                list.Add(duration);
            }
        }

        if (perIssue.Count == 0)
        {
            return BuildEmptyStageDurationResult(windowFrom, windowTo);
        }

        // Build per-stage aggregates in workflow stage order. Stages
        // present in the profile but never reached by any delivered
        // issue are omitted entirely.
        var observedStages = new HashSet<string>(samplesByStage.Keys, StringComparer.Ordinal);
        var orderedStages = stageOrder
            .Concat(observedStages.Where(s => !stageOrder.Contains(s, StringComparer.Ordinal))
                .OrderBy(s => s, StringComparer.Ordinal))
            .ToList();

        var stageAggregates = orderedStages
            .Where(samplesByStage.ContainsKey)
            .Select(stage =>
            {
                var samples = samplesByStage[stage];
                samples.Sort();
                var average = samples.Average();
                var median = ComputeMedian(samples);
                return new StageDurationStageAggregate(
                    Stage: stage,
                    SampleCount: samples.Count,
                    AverageSeconds: average,
                    MedianSeconds: median);
            })
            .ToList();

        // Flow efficiency ratio: population-weighted Σ activeWork ÷
        // Σ cycleTime over issues with a defined, strictly positive
        // cycle. Issues with undefined (no WorkStarted) or zero cycle
        // contribute nothing to numerator or denominator.
        // `activeWork = Σ(stage durations) − approvalGateWait`. The
        // formula is taken verbatim per spec D6 so the three components
        // sum to cycle by construction.
        double sumActiveWork = 0;
        double sumCycle = 0;
        double sumApprovalWait = 0;
        double sumInactiveGap = 0;
        int waitPopulation = 0;
        foreach (var entry in perIssue)
        {
            if (entry.CycleSeconds is not double cycleValue || cycleValue <= 0)
                continue;

            var activeWork = entry.SumStageSeconds - entry.ApprovalGateWaitSeconds;
            var inactiveGap = cycleValue - entry.SumStageSeconds;
            // Event histories with approval wait outside stage spans or stage
            // spans outside the cycle cannot produce the public non-negative
            // decomposition, so they stay out of ratio/wait aggregates.
            if (activeWork < 0 || inactiveGap < 0)
                continue;

            sumActiveWork += activeWork;
            sumCycle += cycleValue;
            sumApprovalWait += entry.ApprovalGateWaitSeconds;
            sumInactiveGap += inactiveGap;
            waitPopulation += 1;
        }

        double? ratio = sumCycle > 0 ? sumActiveWork / sumCycle : null;
        StageDurationWaitBreakout? waitBreakout = waitPopulation > 0
            ? new StageDurationWaitBreakout(
                AverageApprovalGateWaitSeconds: sumApprovalWait / waitPopulation,
                AverageInactiveGapSeconds: sumInactiveGap / waitPopulation)
            : new StageDurationWaitBreakout(null, null);

        return new StageDurationResult(
            new StageDurationWindow(windowFrom, windowTo),
            stageAggregates,
            ratio,
            waitBreakout);
    }

    private static StageDurationResult BuildEmptyStageDurationResult(DateTimeOffset from, DateTimeOffset to) =>
        new(
            new StageDurationWindow(from, to),
            Array.Empty<StageDurationStageAggregate>(),
            null,
            new StageDurationWaitBreakout(null, null));

    private static double ComputeMedian(List<double> sortedSamples)
    {
        // Reuse the exact odd/even formula from `GetApprovalWaitAsync`
        // so the two surfaces agree.
        var count = sortedSamples.Count;
        return count % 2 == 1
            ? sortedSamples[count / 2]
            : (sortedSamples[count / 2 - 1] + sortedSamples[count / 2]) / 2.0;
    }

    private static double SumApprovalGateWaitSeconds(
        IReadOnlyList<string> issueRunIds,
        IReadOnlyDictionary<string, WorkflowStatusView> workflows)
    {
        double totalSeconds = 0;
        foreach (var runId in issueRunIds.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(runId)) continue;
            if (!workflows.TryGetValue(runId, out var workflow)) continue;

            foreach (var approval in MohistDefaultWorkflowProjection.StageApprovals(workflow))
            {
                if (!approval.RespondedAt.HasValue) continue;
                if (!string.Equals(approval.Status, "approved", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(approval.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var wait = (approval.RespondedAt.Value - approval.RequestedAt).TotalSeconds;
                if (wait > 0) totalSeconds += wait;
            }
        }

        return totalSeconds;
    }

    private async Task<IReadOnlyList<string>> ResolveProjectStageOrderAsync(MohistDbContext db, string projectId)
    {
        var profileId = _effectiveProfileResolver.Resolve(
            issueSelection: null,
            projectDefaultId: await LoadProjectDefaultTemplateAsync(db, projectId),
            disabledIds: await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectId));
        if (profileId is null) return new List<string>();
        var profile = _profiles.Get(profileId);
        return profile.Definition.Stages?
            .Select(s => s.Stage)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList()
            ?? new List<string>();
    }

    /// <summary>
    /// Shared attribution core. The stage-duration surface and the
    /// stage-population snapshot job both call this helper so the two
    /// surfaces are guaranteed to agree on an issue's latest stage
    /// under the same <em>latest-attempt, latest-run-wins,
    /// invalidate-on-restart</em> idiom. The per-stage duration
    /// dictionary produced by <see cref="ComputeLatestAttemptStageDurations"/>
    /// is a stage-duration-specific concern (it pairs the latest
    /// <c>StageStarted</c> with the first following
    /// <c>StageCompleted</c> per stage id); the attribution core here
    /// produces the issue's single attributed stage for the day.
    /// <para>
    /// Lifecycle events come from the per-issue <c>IssueEvents</c> the
    /// caller has already loaded (work-started / completed /
    /// cancelled / reopened); stage events come from the per-run
    /// <c>WorkflowRunEvents</c> the caller has already loaded. The
    /// day bound is <paramref name="dayEndUtc"/>; events past the
    /// bound are caller-filtered (SQLite cannot translate
    /// <see cref="DateTimeOffset"/> against TEXT, so the bound is
    /// applied in LINQ-to-objects after materialization).
    /// </para>
    /// </summary>
    private static IssueStageAttribution.Attribution ComputeIssueAttribution(
        IReadOnlyList<IssueStageAttribution.AttributionEvent> lifecycleEvents,
        IReadOnlyList<string> issueRunIds,
        IReadOnlyDictionary<string, List<WorkflowRunStageEvent>> stageEventsByRun,
        IReadOnlyList<string> stageOrder,
        DateTimeOffset dayEndUtc)
    {
        var events = new List<IssueStageAttribution.AttributionEvent>(lifecycleEvents.Count + 16);
        events.AddRange(lifecycleEvents);
        foreach (var runId in issueRunIds)
        {
            if (string.IsNullOrWhiteSpace(runId)) continue;
            if (!stageEventsByRun.TryGetValue(runId, out var list)) continue;
            foreach (var se in list)
            {
                events.Add(new IssueStageAttribution.AttributionEvent(
                    Type: se.Type,
                    Time: se.Time,
                    Id: se.Id,
                    Stage: se.Stage,
                    WorkflowRunId: runId));
            }
        }

        return IssueStageAttribution.Attribute(events, stageOrder, dayEndUtc);
    }

    /// <summary>
    /// For one issue, gather every <c>StageStarted</c> /
    /// <c>StageCompleted</c> event across its run sources, order the
    /// combined event stream by <c>(Time, Id)</c>, then for each stage
    /// id take the LAST <c>StageStarted</c> event and pair it with the
    /// FIRST <c>StageCompleted</c> event that follows it. Earlier
    /// attempts (invalidated by a subsequent <c>StageStarted</c> for
    /// the same stage) are not averaged in.
    /// <para>
    /// A stage whose latest <c>StageStarted</c> has no following
    /// <c>StageCompleted</c> is simply absent from the returned
    /// dictionary — its undefined duration does not contribute to any
    /// aggregate.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, double> ComputeLatestAttemptStageDurations(
        IReadOnlyList<string> issueRunIds,
        IReadOnlyDictionary<string, List<WorkflowRunStageEvent>> stageEventsByRun)
    {
        // Stream every stage event for this issue's runs, ordered by
        // `(Time, Id)` so the durable append-only sequence is canonical.
        var events = new List<WorkflowRunStageEvent>();
        foreach (var runId in issueRunIds)
        {
            if (string.IsNullOrWhiteSpace(runId)) continue;
            if (!stageEventsByRun.TryGetValue(runId, out var list)) continue;
            events.AddRange(list);
        }

        if (events.Count == 0) return new Dictionary<string, double>(StringComparer.Ordinal);

        // Sort by `(Time, Id)` so the durable append-only sequence is
        // the canonical ordering (matches `GetApprovalWaitAsync`'s
        // expectation that older events sort before newer ones).
        events.Sort(static (a, b) =>
        {
            var byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
        });

        // For each stage id, track the latest StageStarted event position
        // we've seen (supersedes any prior StageStarted for the same
        // stage, satisfying invalidate-on-restart). Pairing is a separate
        // pass so duplicate completions after the matching completion do
        // not stretch the latest attempt.
        var latestStartedIndexByStage = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            if (string.IsNullOrWhiteSpace(evt.Stage)) continue;
            if (string.Equals(evt.Type, EventCatalog.ReverseDns.StageStarted, StringComparison.Ordinal))
            {
                latestStartedIndexByStage[evt.Stage] = i;
            }
        }

        // For each stage id, the latest attempt is the latest StageStarted,
        // paired with the first StageCompleted that follows it in ordered
        // event sequence. Later duplicate/recovery completions are ignored.
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (stage, startedIndex) in latestStartedIndexByStage)
        {
            var startedAt = events[startedIndex].Time;
            for (var i = startedIndex + 1; i < events.Count; i++)
            {
                var evt = events[i];
                if (!string.Equals(evt.Stage, stage, StringComparison.Ordinal)) continue;
                if (!string.Equals(evt.Type, EventCatalog.ReverseDns.StageCompleted, StringComparison.Ordinal)) continue;

                var durationSeconds = (evt.Time - startedAt).TotalSeconds;
                if (durationSeconds >= 0)
                    durations[stage] = durationSeconds;
                break;
            }
        }

        return durations;
    }

    private async Task<Dictionary<string, List<WorkflowRunStageEvent>>> LoadWorkflowRunStageEventsAsync(
        MohistDbContext db,
        IEnumerable<string> workflowRunIds)
    {
        var runIds = workflowRunIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runIds.Length == 0)
            return new Dictionary<string, List<WorkflowRunStageEvent>>(StringComparer.Ordinal);

        var sourcesByRunId = runIds.ToDictionary(
            id => WorkflowRunEventPersistence.WorkflowRunSource(id),
            id => id,
            StringComparer.Ordinal);
        var sources = sourcesByRunId.Keys.ToArray();

        // Select Source/Id/Type/Time/Data over BOTH StageStarted and
        // StageCompleted (the existing
        // `LoadWorkflowRunEventFactsAsync` filters by
        // `QualityWorkflowEventTypes`, which omits `StageCompleted`).
        var rows = await db.WorkflowRunEvents
            .AsNoTracking()
            .Where(row => sources.Contains(row.Source)
                && StageDurationEventTypes.Contains(row.Type))
            .Select(row => new { row.Source, row.Id, row.Type, row.Time, row.Data })
            .ToListAsync();

        var grouped = new Dictionary<string, List<WorkflowRunStageEvent>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!sourcesByRunId.TryGetValue(row.Source, out var runId)) continue;
            var stage = ReadWorkflowEventStage(row.Data);
            var evt = new WorkflowRunStageEvent(
                runId, row.Id, row.Type, row.Time, stage);
            if (!grouped.TryGetValue(runId, out var list))
            {
                list = new List<WorkflowRunStageEvent>();
                grouped[runId] = list;
            }
            list.Add(evt);
        }

        return grouped;
    }

    private sealed record WorkflowRunStageEvent(
        string RunId,
        long Id,
        string Type,
        DateTimeOffset Time,
        string? Stage);

    private sealed record PerIssueCycleBreakdown(
        IReadOnlyDictionary<string, double> StageDurations,
        double? CycleSeconds,
        double ApprovalGateWaitSeconds,
        double SumStageSeconds);

    /// <summary>
    /// Helpers for the completion aggregation. Internal so the unit tests
    /// can pin ISO-week boundary computation without instantiating the
    /// service.
    /// </summary>
    internal static class ISOWeekHelper
    {
        public static DateTime StartOfIsoWeek(DateTime utc)
        {
            // ISO weeks start on Monday. DayOfWeek: Sunday=0, Monday=1, ....
            var dow = (int)utc.DayOfWeek;
            // Number of days since Monday (treating Monday as 0).
            var daysSinceMonday = (dow + 6) % 7;
            return utc.Date.AddDays(-daysSinceMonday);
        }
    }

    internal static (string? Key, string Value) ParseLabelFilter(string token)
    {
        var idx = token.IndexOf('=');
        if (idx <= 0) return (null, token);
        var key = token[..idx];
        var value = token[(idx + 1)..];
        return (key, value);
    }

    internal static IReadOnlyList<string> LabelFilterTokens(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? []
            : label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<IssueReadModel> ToReadModelAsync(MohistDbContext db, Domain.Issue issue, ProjectInfo? project = null)
    {
        var projectDefaultTemplateId = await LoadProjectDefaultTemplateAsync(db, issue.ProjectId);
        var model = ToReadModel(await ToInfoAsync(issue, project, projectDefaultTemplateId));
        ApplyWorkflowProjections([model], await LoadWorkflowStatesAsync(db, [model]));
        ApplyFeedbackProjections([model], await LoadFeedbackAsync(db, [model]));
        return model;
    }

    public async Task<IssueInfo> ToInfoAsync(Domain.Issue issue, ProjectInfo? project, string? projectDefaultTemplateId)
    {
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(issue.ProjectId);
        var resolved = _effectiveProfileResolver.Resolve(issue.WorkflowProfileId, projectDefaultTemplateId, disabledIds);
        var resolution = _resolver.Resolve(project, issue.RepositoryRef);
        return new()
        {
            Id = issue.Id,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body,
            Status = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
            Health = MohistDefaultWorkflowProjection.Health(issue.Status),
            ProjectId = issue.ProjectId,
            ProjectName = project?.Name,
            Labels = new Dictionary<string, string>(issue.Labels, StringComparer.Ordinal),
            Priority = issue.Priority,
            Risk = issue.Risk,
            Model = null,
            ModelVariant = null,
            AgentConfig = null,
            StageModels = null,
            StageModelVariants = null,
            StageVariables = null,
            CreatedAt = issue.CreatedAt.ToString("o"),
            UpdatedAt = issue.UpdatedAt.ToString("o"),
            ArchivedAt = issue.ArchivedAt?.ToString("o"),
            CompletedAt = issue.CompletedAt?.ToString("o"),
            WorkflowRunId = issue.WorkflowRunId,
            WorkflowProfileId = resolved,
            PrerequisiteNumbers = issue.PrerequisiteNumbers,
            IsDraft = issue.IsDraft,
            Repository = resolution.Repository,
            RepositoryProblem = resolution.Problem,
        };
    }

    public static IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project = null)
    {
        return ToInfo(new IssueRepositoryResolver(), issue, project);
    }

    public static IssueInfo ToInfo(IssueRepositoryResolver resolver, Domain.Issue issue, ProjectInfo? project = null)
    {
        var resolution = resolver.Resolve(project, issue.RepositoryRef);
        return new()
        {
            Id = issue.Id,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body,
            Status = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
            Health = MohistDefaultWorkflowProjection.Health(issue.Status),
            ProjectId = issue.ProjectId,
            ProjectName = project?.Name,
            Labels = new Dictionary<string, string>(issue.Labels, StringComparer.Ordinal),
            Priority = issue.Priority,
            Risk = issue.Risk,
            Model = null,
            ModelVariant = null,
            AgentConfig = null,
            StageModels = null,
            StageModelVariants = null,
            StageVariables = null,
            CreatedAt = issue.CreatedAt.ToString("o"),
            UpdatedAt = issue.UpdatedAt.ToString("o"),
            ArchivedAt = issue.ArchivedAt?.ToString("o"),
            CompletedAt = issue.CompletedAt?.ToString("o"),
            WorkflowRunId = issue.WorkflowRunId,
            WorkflowProfileId = IssueWorkflowProfiles.LocalId,
            PrerequisiteNumbers = issue.PrerequisiteNumbers,
            IsDraft = issue.IsDraft,
            Repository = resolution.Repository,
            RepositoryProblem = resolution.Problem,
        };
    }

    /// <summary>
    /// Instance projection that uses the centralized effective-profile
    /// resolver. Prefer this over the static overloads in any code path
    /// that has access to the scoped <see cref="IssueQuerier"/> so the
    /// profile id agrees across every read surface.
    /// </summary>
    public IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project, string? projectDefaultTemplateId) =>
        ToInfo(issue, project, projectDefaultTemplateId, null);

    public IssueInfo ToInfo(Domain.Issue issue, ProjectInfo? project, string? projectDefaultTemplateId, IReadOnlySet<string>? disabledIds)
    {
        var resolution = _resolver.Resolve(project, issue.RepositoryRef);
        return new()
        {
            Id = issue.Id,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body,
            Status = MohistDefaultWorkflowProjection.IssueStatusName(issue.Status),
            Health = MohistDefaultWorkflowProjection.Health(issue.Status),
            ProjectId = issue.ProjectId,
            ProjectName = project?.Name,
            Labels = new Dictionary<string, string>(issue.Labels, StringComparer.Ordinal),
            Priority = issue.Priority,
            Risk = issue.Risk,
            Model = null,
            ModelVariant = null,
            AgentConfig = null,
            StageModels = null,
            StageModelVariants = null,
            StageVariables = null,
            CreatedAt = issue.CreatedAt.ToString("o"),
            UpdatedAt = issue.UpdatedAt.ToString("o"),
            ArchivedAt = issue.ArchivedAt?.ToString("o"),
            CompletedAt = issue.CompletedAt?.ToString("o"),
            WorkflowRunId = issue.WorkflowRunId,
            WorkflowProfileId = _effectiveProfileResolver.Resolve(issue.WorkflowProfileId, projectDefaultTemplateId, disabledIds),
            PrerequisiteNumbers = issue.PrerequisiteNumbers,
            IsDraft = issue.IsDraft,
            Repository = resolution.Repository,
            RepositoryProblem = resolution.Problem,
        };
    }

    internal static RepositoryInfo? ResolveIssueRepository(IssueRepositoryResolver resolver, Domain.Issue issue, ProjectInfo? project)
    {
        return resolver.Resolve(project, issue.RepositoryRef).Repository;
    }

    public static IssueReadModel ToReadModel(IssueInfo issue) => new()
    {
        Id = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Status = issue.Status,
        Health = issue.Health,
        ProjectId = issue.ProjectId,
        ProjectName = issue.ProjectName,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Risk = issue.Risk,
        Model = issue.Model,
        ModelVariant = issue.ModelVariant,
        AgentConfig = issue.AgentConfig,
        StageModels = issue.StageModels,
        StageModelVariants = issue.StageModelVariants,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        CompletedAt = issue.CompletedAt,
        Attention = issue.Attention,
        WorkflowRunId = issue.WorkflowRunId,
        WorkflowProfileId = issue.WorkflowProfileId,
        WorkflowProfileMode = issue.WorkflowProfileMode,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        IsDraft = issue.IsDraft,
        CanStart = issue.CanStart,
        Blocker = issue.Blocker,
        Repository = issue.Repository,
        RepositoryProblem = issue.RepositoryProblem,
    };

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(MohistDbContext db, IReadOnlyCollection<IssueReadModel> issues)
    {
        var workflowRunIds = issues
            .Select(i => i.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await LoadWorkflowStatesAsync(db, workflowRunIds);
    }

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(MohistDbContext db, IReadOnlyCollection<string> workflowRunIds)
    {
        var runIds = workflowRunIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runIds.Length == 0) return [];

        var runRows = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => runIds.Contains(row.WorkflowRunId))
            .ToListAsync();

        var workflows = new Dictionary<string, WorkflowStatusView>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.State);
            if (run is null) continue;
            var snapshot = WorkflowStatusMapper.BuildStatusView(run, definition: null);
            if (snapshot is not null)
                workflows[row.WorkflowRunId] = snapshot;
        }
        return workflows;
    }

    private async Task<Dictionary<string, IReadOnlyList<ApprovalFeedback>>> LoadFeedbackAsync(MohistDbContext db, IReadOnlyCollection<IssueReadModel> issues)
    {
        var workflowRunIds = issues
            .Select(i => i.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (workflowRunIds.Length == 0) return [];

        var runRows = await db.WorkflowRuns
            .AsNoTracking()
            .Where(row => workflowRunIds.Contains(row.WorkflowRunId))
            .ToListAsync();

        var result = new Dictionary<string, IReadOnlyList<ApprovalFeedback>>(StringComparer.Ordinal);
        foreach (var row in runRows)
        {
            var run = DeserializeRun(row.State);
            if (run is null) continue;
            if (run.Feedback.Count == 0) continue;
            result[row.WorkflowRunId] = run.Feedback;
        }
        return result;
    }

    private async Task<string?> LoadProjectDefaultTemplateAsync(MohistDbContext db, string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        var row = await db.ProjectWorkflowProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId);
        return row?.DefaultTemplateId;
    }

    private void ApplyWorkflowProjections(IReadOnlyCollection<IssueReadModel> issues, IReadOnlyDictionary<string, WorkflowStatusView> workflows)
    {
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null || !workflows.TryGetValue(issue.WorkflowRunId, out var status)) continue;

            if (string.IsNullOrWhiteSpace(issue.WorkflowProfileId)) continue;

            var projection = _profiles.Get(issue.WorkflowProfileId).ProjectWorkflowState(issue, status);

            issue.Status = projection.IssueStatus;
            issue.Health = projection.Health;
            issue.BlockedReason = projection.BlockedReason;
            issue.StageApproval = projection.StageApproval;
            issue.Attention = projection.Attention;
            issue.WorkflowStage = status.CurrentStage;
            issue.WorkflowStatus = status.Status;
            issue.WorkflowStageProgress = ComputeStageProgress(status);
        }
    }

    private void ApplyFeedbackProjections(
        IReadOnlyCollection<IssueReadModel> issues,
        IReadOnlyDictionary<string, IReadOnlyList<ApprovalFeedback>> feedbackByRun)
    {
        foreach (var issue in issues)
        {
            if (issue.WorkflowRunId is null
                || !feedbackByRun.TryGetValue(issue.WorkflowRunId, out var feedback)
                || feedback.Count == 0)
            {
                issue.Feedback = [];
                continue;
            }

            issue.Feedback = feedback
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => new IssueFeedbackDto(
                    Id: f.Id,
                    IssueNumber: issue.Number,
                    WorkflowRunId: f.WorkflowRunId,
                    Stage: f.Stage,
                    Status: f.Status,
                    Body: f.Body,
                    CreatedAt: f.CreatedAt.ToString("o"),
                    Resolution: f.Status == ApprovalFeedbackStatus.Resolved
                        ? new IssueFeedbackResolutionDto(
                            ResolutionTaskId: f.ResolutionTaskId,
                            ResolvedAt: f.ResolvedAt?.ToString("o"),
                            ResolutionSummary: f.ResolutionSummary)
                        : null))
                .ToArray();
        }
    }

    private static WorkflowStageProgress? ComputeStageProgress(WorkflowStatusView status)
    {
        if (IsNonMeaningfulProgressState(status))
            return null;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null) return null;

        var userTasks = currentStage.Tasks.Where(t => t.Classification == TaskClassification.UserFacing).ToList();
        if (userTasks.Count == 0) return null;

        var total = userTasks.Count;
        var completed = userTasks.Count(t => t.Status == "completed");
        var running = userTasks.Count(t => t.Status == "running");
        var failed = userTasks.Count(t => t.Status == "failed");

        if (total == 0) return null;

        var currentTaskTitle = userTasks.FirstOrDefault(t => t.Status is "running" or "pending")?.Title;

        return new WorkflowStageProgress(
            status.CurrentStage!,
            total,
            completed,
            running,
            failed,
            currentTaskTitle);
    }

    private static bool IsNonMeaningfulProgressState(WorkflowStatusView status)
    {
        if (status.Status is "completed" or "failed" or "awaiting-approval" or "paused")
            return true;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null)
            return true;

        return currentStage.ApprovalStatus is { Result: null }
            || (currentStage.Tasks.All(t => t.Status == "completed") && currentStage.Checks.All(c => c.Status == "completed"));
    }

    private async Task<List<IssueReadModel>> EnrichAsync(MohistDbContext db, List<IssueReadModel> issues)
    {
        if (issues.Count == 0) return issues;

        var projectId = issues[0].ProjectId;
        var numbers = issues.Select(i => i.Number).ToArray();
        var issueIds = issues.Select(i => i.Id).ToArray();
        var byNumber = issues.ToDictionary(i => i.Number);
        var byId = issues.ToDictionary(i => i.Id);

        var comments = await db.IssueComments.AsNoTracking()
            .Where(c => c.ProjectId == projectId && numbers.Contains(c.IssueNumber))
            .ToListAsync();
        comments = comments.OrderBy(c => c.CreatedAt).ToList();

        var commentIds = comments.Select(c => c.Id).ToArray();
        var attachmentRows = await db.Attachments.AsNoTracking()
            .Where(a => a.ProjectId == projectId
                && a.OwnerKind != null
                && a.OwnerId != null
                && ((a.OwnerKind == AttachmentService.OwnerKindIssue && issueIds.Contains(a.OwnerId))
                    || (a.OwnerKind == AttachmentService.OwnerKindComment && commentIds.Contains(a.OwnerId))))
            .ToListAsync();

        var issueAttachments = attachmentRows
            .Where(a => a.OwnerKind == AttachmentService.OwnerKindIssue && a.OwnerId is not null)
            .GroupBy(a => a.OwnerId!)
            .ToDictionary(group => group.Key, group => group.Select(ToAttachmentInfo).ToArray());
        var commentAttachments = attachmentRows
            .Where(a => a.OwnerKind == AttachmentService.OwnerKindComment && a.OwnerId is not null)
            .GroupBy(a => a.OwnerId!)
            .ToDictionary(group => group.Key, group => group.Select(ToAttachmentInfo).ToArray());

        foreach (var issue in issues)
        {
            if (issueAttachments.TryGetValue(issue.Id, out var attachments))
                issue.Attachments = attachments;
        }

        foreach (var group in comments.GroupBy(c => c.IssueNumber))
        {
            if (byNumber.TryGetValue(group.Key, out var issue))
            {
                issue.Comments = group.Select(comment => ToCommentDto(comment, commentAttachments)).ToArray();
            }
        }

        var profileRows = await db.IssueWorkflowProfiles.AsNoTracking()
            .Where(profile => issueIds.Contains(profile.IssueId))
            .ToDictionaryAsync(profile => profile.IssueId, profile => profile.Variables);

        // Resolve the effective agent config for display by merging the live
        // global + project layers with each issue's snapshot (which now holds
        // only built-in context + explicit issue overrides). This keeps the
        // displayed model/agent in sync with project edits; see
        // WorkflowProfileManager.LoadVariablesAsync for the dispatch equivalent.
        var globalBundle = await _configService.GetVariables();
        VariableBundle? projectBundle = null;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var projectProfile = await db.ProjectWorkflowProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.ProjectId == projectId);
            projectBundle = VariableBundle.FromJson(projectProfile?.Variables);
        }

        foreach (var issue in issues)
        {
            profileRows.TryGetValue(issue.Id, out var variablesJson);
            ApplyIssueWorkflowVariables(issue, variablesJson, globalBundle, projectBundle);
        }

        var persistedRows = await db.IssuePrerequisites.AsNoTracking()
            .Where(p => p.ProjectId == projectId && numbers.Contains(p.IssueNumber))
            .ToListAsync();
        var prereqRows = issues
            .SelectMany(issue => issue.PrerequisiteNumbers.Select(prerequisiteNumber => new IssuePrerequisiteRow
            {
                ProjectId = projectId,
                IssueNumber = issue.Number,
                PrerequisiteNumber = prerequisiteNumber,
            }))
            .Concat(persistedRows)
            .GroupBy(p => new { p.IssueNumber, p.PrerequisiteNumber })
            .Select(group => group.First())
            .ToList();
        var prereqNumbers = prereqRows.Select(p => p.PrerequisiteNumber).Distinct().ToArray();
        var prereqIssues = issues.Where(i => prereqNumbers.Contains(i.Number)).ToDictionary(i => i.Number);
        var missingPrereqNumbers = prereqNumbers.Where(number => !prereqIssues.ContainsKey(number)).ToArray();
        if (missingPrereqNumbers.Length > 0)
        {
            var rows = await db.Issues.AsNoTracking()
                .Where(row => row.ProjectId == projectId && row.Number != null && missingPrereqNumbers.Contains(row.Number.Value))
                .ToListAsync();
            foreach (var issue in IssueRowMapper.ByNumber(rows, projectId, missingPrereqNumbers).Values)
            {
                prereqIssues[issue.Number] = ToReadModel(ToInfo(issue));
            }
        }
        var prereqGroups = prereqRows.GroupBy(p => p.IssueNumber).ToDictionary(g => g.Key);
        foreach (var issue in issues)
        {
            var summaries = prereqGroups.TryGetValue(issue.Number, out var group)
                ? group
                    .Select(p => prereqIssues.TryGetValue(p.PrerequisiteNumber, out var prereq) ? IssuePrerequisiteSummary.FromReadModel(prereq) : null)
                    .Where(p => p is not null)
                    .Cast<IssuePrerequisiteSummary>()
                    .ToArray()
                : [];
            issue.Prerequisites = summaries;
            var summariesByNumber = summaries.ToDictionary(s => s.Number);
            var undelivered = new HashSet<int>(summaries.Where(s => !s.Completed).Select(s => s.Number));
            var blocker = ComputeBlockerForReadModel(issue, undelivered);
            issue.Blocker = IssueStartBlockerDto.FromDomain(blocker, summariesByNumber);
            issue.CanStart = blocker is null;
        }

        var epicLinks = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == projectId && issueIds.Contains(link.IssueId))
            .ToListAsync();
        if (epicLinks.Count > 0)
        {
            var epicIds = epicLinks.Select(l => l.EpicId).Distinct().ToArray();
            var epics = await db.Epics.AsNoTracking()
                .Where(epic => epic.ProjectId == projectId && epicIds.Contains(epic.Id))
                .ToDictionaryAsync(e => e.Id);
            foreach (var link in epicLinks)
            {
                if (byId.TryGetValue(link.IssueId, out var issue) && epics.TryGetValue(link.EpicId, out var epic))
                {
                    // Issue-179: primaryEpic reflects the issue's NON-TERMINAL
                    // epic membership. After T-001, an issue may belong to at
                    // most one non-terminal epic, so filtering terminal
                    // owners leaves at most one candidate per issue. The
                    // "last write wins" loop naturally resolves to that
                    // single non-terminal epic; an issue with only terminal
                    // memberships leaves PrimaryEpic null.
                    if (EpicProgress.IsTerminal(epic.Status)) continue;
                    issue.PrimaryEpic = new IssuePrimaryEpic
                    {
                        Id = epic.Id,
                        Number = epic.Number,
                        Title = epic.Title,
                        Status = epic.Status,
                        Priority = epic.Priority,
                    };
                }
            }
        }

        return issues;
    }

    private async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueInfo issue) =>
        (await EnrichAsync(db, [ToReadModel(issue)]))[0];

    private async Task<IssueReadModel> EnrichAsync(MohistDbContext db, IssueReadModel issue) =>
        (await EnrichAsync(db, [issue]))[0];

    public static IssueCommentDto ToCommentDto(IssueCommentRow comment) =>
        ToCommentDto(comment, new Dictionary<string, AttachmentInfo[]>());

    private static IssueCommentDto ToCommentDto(
        IssueCommentRow comment,
        IReadOnlyDictionary<string, AttachmentInfo[]> attachmentsByComment) =>
        new(
            comment.Id,
            comment.IssueId,
            comment.Body,
            comment.CreatedAt.ToString("o"),
            attachmentsByComment.TryGetValue(comment.Id, out var attachments) ? attachments : []);

    private static AttachmentInfo ToAttachmentInfo(AttachmentRow row) => new(
        row.Id,
        row.OriginalFileName,
        string.IsNullOrWhiteSpace(row.ContentType) ? "application/octet-stream" : row.ContentType,
        row.Size);

    private static void ApplyIssueWorkflowVariables(
        IssueReadModel issue,
        string? variablesJson,
        VariableBundle globalBundle,
        VariableBundle? projectBundle)
    {
        var issueBundle = VariableBundle.FromJson(variablesJson);
        var effective = VariableBundle.MergeAll(globalBundle, projectBundle, issueBundle);
        var agentConfig = ReadAgentConfig(effective.Vars);
        issue.AgentConfig = agentConfig;
        issue.Model = ReadAgentModel(agentConfig);
        issue.ModelVariant = ReadAgentVariant(agentConfig, hasModel: !string.IsNullOrWhiteSpace(issue.Model));

        if (effective.Stages is null || effective.Stages.Count == 0)
        {
            issue.StageModels = null;
            issue.StageModelVariants = null;
            return;
        }

        var stageModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stageModelVariants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stage, variables) in effective.Stages)
        {
            var stageAgentConfig = ReadAgentConfig(variables.Vars);
            var model = ReadAgentModel(stageAgentConfig);
            if (!string.IsNullOrWhiteSpace(model))
                stageModels[stage] = model;
            var variant = ReadAgentVariant(stageAgentConfig, hasModel: !string.IsNullOrWhiteSpace(model));
            if (!string.IsNullOrWhiteSpace(variant))
                stageModelVariants[stage] = variant;
        }

        issue.StageModels = stageModels.Count > 0 ? stageModels : null;
        issue.StageModelVariants = stageModelVariants.Count > 0 ? stageModelVariants : null;
    }

    private static Dictionary<string, object?>? ReadAgentConfig(JsonElement? vars)
    {
        if (!vars.HasValue || vars.Value.ValueKind != JsonValueKind.Object)
            return null;
        if (!vars.Value.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(agent.GetRawText(), JSON.Options);
    }

    private static string? ReadAgentModel(Dictionary<string, object?>? agentConfig)
    {
        if (agentConfig is null || !agentConfig.TryGetValue("model", out var raw) || raw is null)
            return null;
        if (raw is string model)
            return string.IsNullOrWhiteSpace(model) ? null : model;
        if (raw is JsonElement { ValueKind: JsonValueKind.String } element)
            return element.GetString();
        return null;
    }

    private static string? ReadAgentVariant(Dictionary<string, object?>? agentConfig, bool hasModel)
    {
        // Variant is bound to its model: if no model, the variant is meaningless
        // and is suppressed from the response, mirroring the clear-on-clear invariant.
        if (!hasModel) return null;
        if (agentConfig is null || !agentConfig.TryGetValue("variant", out var raw) || raw is null)
            return null;
        if (raw is string variant)
            return string.IsNullOrWhiteSpace(variant) ? null : variant;
        if (raw is JsonElement { ValueKind: JsonValueKind.String } element)
            return element.GetString();
        return null;
    }

    private static WorkflowRun? DeserializeRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, JSON.Options); }
        catch { return null; }
    }

    private static IssueStartBlocker? ComputeBlockerForReadModel(IssueReadModel issue, IReadOnlySet<int> undeliveredPrerequisites)
    {
        if (issue.IsDraft) return new IssueStartBlocker.Draft();
        if (undeliveredPrerequisites.Count == 0) return null;
        foreach (var number in issue.PrerequisiteNumbers)
        {
            if (undeliveredPrerequisites.Contains(number))
                return new IssueStartBlocker.WaitingFor(number);
        }
        return null;
    }

}
