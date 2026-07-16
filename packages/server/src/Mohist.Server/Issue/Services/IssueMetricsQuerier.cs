using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Dedicated analytics aggregation service. Owns all metrics-only
/// concerns: completion bucketing, quality windows + trend,
/// approval-wait statistics, delivery-time series, and per-stage
/// durations + flow-efficiency. Receives the project's issue read
/// models from <see cref="IssueReadModelLoader"/>; never calls into
/// <see cref="IssueQuerier"/> (the two services are orthogonal).
/// </summary>
public class IssueMetricsQuerier : IScopedService
{
    internal const string WorkStartedType = EventCatalog.ReverseDns.IssueWorkStarted;
    // CloudEvents reverse-DNS bus types that mark a terminal transition.
    // These must mirror what <see cref="IssueEventSerializer.BusType"/> emits
    // (and what <see cref="EventStore"/> persists to <c>IssueEvents.Type</c>),
    // so we anchor on <see cref="EventCatalog"/> rather than restating the
    // literal — a previous mismatch silenced every IssueEvents-backed metric.
    internal const string WorkCompletedType = EventCatalog.ReverseDns.IssueCompleted;
    internal const string ClosedType = EventCatalog.ReverseDns.IssueCancelled;
    internal const string IssueSourcePrefix = "/mohist/issues/";

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
    private readonly EffectiveWorkflowProfileResolver _effectiveProfileResolver;
    private readonly ProjectWorkflowProfileManager _projectProfileManager;
    private readonly IssueReadModelLoader _loader;
    private readonly ILogger<IssueMetricsQuerier> _logger;

    public IssueMetricsQuerier(
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueWorkflowProfileRegistry profiles,
        EffectiveWorkflowProfileResolver effectiveProfileResolver,
        ProjectWorkflowProfileManager projectProfileManager,
        IssueReadModelLoader loader,
        ILogger<IssueMetricsQuerier> logger)
    {
        _dbFactory = dbFactory;
        _profiles = profiles;
        _effectiveProfileResolver = effectiveProfileResolver;
        _projectProfileManager = projectProfileManager;
        _loader = loader;
        _logger = logger;
    }

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
    /// The result of <see cref="GetQualityAsync"/>. The range-driven
    /// primary window, the immediately-preceding window of the same
    /// length, and the per-day trend over the primary window are
    /// returned together so callers can derive a percentage-point
    /// delta and visualize within-window movement in a single read.
    /// <see cref="Window"/> replaces the prior dual-window contract
    /// (fixed <c>Window7d</c> + range-driven <c>Window30d</c>); the
    /// per-stage rework rates, the ship-time windowing, and the
    /// per-bucket trend series are unchanged. <see cref="PreviousWindow"/>
    /// is strictly additive: it carries the previous-window FTR rate
    /// plus its <see cref="QualityPreviousWindow.SampleCount"/>
    /// discriminator.
    /// </summary>
    public sealed record QualityMetricsResult(
        QualityMetricsWindow Window,
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
    /// The pre-sized daily trend returned alongside the trailing window
    /// scalars. <see cref="Points"/> has length equal to the primary
    /// window length, anchored on <see cref="WindowFrom"/> and ordered
    /// oldest-first.
    /// </summary>
    public sealed record QualityTrend(
        string Bucket,
        DateTimeOffset WindowFrom,
        DateTimeOffset WindowTo,
        IReadOnlyList<QualityTrendPoint> Points);

    /// <summary>
    /// Buckets the project's terminal-issue transitions (<c>work-completed</c>
    /// and <c>closed</c>) from the durable <c>IssueEvents</c> table by
    /// <c>IssueEvents.Time</c>, not by issue <c>updatedAt</c>. The window
    /// is fixed: <paramref name="bucket"/> = <see cref="CompletionBucket.Day"/>
    /// returns 30 trailing UTC days; <see cref="CompletionBucket.Week"/>
    /// returns 12 trailing ISO weeks (Mon-anchored, UTC). Every bucket in
    /// the window is emitted (zeros included). Issue counts are distinct
    /// per bucket — an issue with multiple terminal events of the same
    /// Type in the same bucket counts once.
    /// </summary>
    public async Task<CompletionBucketsResult> GetCompletionBucketsAsync(
        string projectId,
        CompletionBucket bucket,
        DateTimeOffset now,
        int? windowDays = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var days = windowDays ?? (bucket == CompletionBucket.Day ? 30 : 7 * 12);
        var weekCount = bucket == CompletionBucket.Day
            ? days
            : (int)Math.Ceiling(days / 7.0);

        DateTimeOffset windowFrom;
        DateTimeOffset windowTo;
        DateTimeOffset previousFrom;
        DateTimeOffset previousTo;
        IReadOnlyList<DateOnly> boundaries;

        if (bucket == CompletionBucket.Day)
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
            windowFrom = new DateTimeOffset(today.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            windowTo = new DateTimeOffset(today.AddDays(1).ToDateTime(new TimeOnly(0, 0)), TimeSpan.Zero);
            previousFrom = new DateTimeOffset(today.AddDays(-(2 * days - 1)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            previousTo = windowFrom;
            boundaries = Enumerable.Range(0, days)
                .Select(i => today.AddDays(-(days - 1) + i))
                .ToList();
        }
        else
        {
            var currentWeek = ISOWeekHelper.StartOfIsoWeek(now.UtcDateTime);
            var firstWeek = currentWeek.AddDays(-7 * (weekCount - 1));
            windowFrom = new DateTimeOffset(firstWeek, TimeSpan.Zero);
            windowTo = new DateTimeOffset(currentWeek.AddDays(7), TimeSpan.Zero);
            var previousFirstWeek = firstWeek.AddDays(-7 * weekCount);
            previousFrom = new DateTimeOffset(previousFirstWeek, TimeSpan.Zero);
            previousTo = windowFrom;
            boundaries = Enumerable.Range(0, weekCount)
                .Select(i => DateOnly.FromDateTime(firstWeek.AddDays(7 * i)))
                .ToList();
        }

        var points = Enumerable.Range(0, boundaries.Count)
            .Select(i => new CompletionBucketPoint(
                Boundary: boundaries[i].ToString("yyyy-MM-dd"),
                Completed: 0,
                Failed: 0))
            .ToList();

        var terminalEvents = await ScanIssueEventsByProjectSourceAsync(
            db, projectId, typeFilter: [WorkCompletedType, ClosedType]);

        var latestTerminalByIssue = terminalEvents
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
            if (row.Time >= windowFrom && row.Time < windowTo)
            {
                if (row.Type == WorkCompletedType)
                    currentTotal = currentTotal with { Completed = currentTotal.Completed + 1 };
                else
                    currentTotal = currentTotal with { Failed = currentTotal.Failed + 1 };
                currentTotal = currentTotal with { SampleCount = currentTotal.SampleCount + 1 };

                var boundary = bucket == CompletionBucket.Day
                    ? DateOnly.FromDateTime(row.Time.UtcDateTime.Date)
                    : DateOnly.FromDateTime(ISOWeekHelper.StartOfIsoWeek(row.Time.UtcDateTime));
                if (indexByBoundary.TryGetValue(boundary, out var idx))
                {
                    if (row.Type == WorkCompletedType)
                        points[idx] = points[idx] with { Completed = points[idx].Completed + 1 };
                    else
                        points[idx] = points[idx] with { Failed = points[idx].Failed + 1 };
                }
            }
            else if (row.Time >= previousFrom && row.Time < previousTo)
            {
                if (row.Type == WorkCompletedType)
                    previousTotal = previousTotal with { Completed = previousTotal.Completed + 1 };
                else
                    previousTotal = previousTotal with { Failed = previousTotal.Failed + 1 };
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
    /// Aggregates AI quality signals (first-time-right rate and per-stage
    /// rework rate) over a single range-driven primary window. Only issues
    /// whose status is <see cref="IssueStatus.Done"/> participate. Window
    /// membership is anchored on the <c>com.mohist.issue.work-completed</c>
    /// event time, matching <see cref="GetCompletionBucketsAsync"/>. Rates
    /// are computed from existing workflow-run state and durable check events;
    /// no new data collection is introduced. The primary window's length
    /// follows <paramref name="windowDays"/> (default <c>30</c>); the
    /// immediately-preceding window of the same length and the per-day trend
    /// over the primary window scale with the same length.
    /// </summary>
    public async Task<QualityMetricsResult> GetQualityAsync(
        string projectId,
        DateTimeOffset now,
        int? windowDays = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var primaryDays = windowDays ?? 30;
        var windowFrom = now.AddDays(-primaryDays);
        var windowTo = now;

        var previousFrom = now.AddDays(-2 * primaryDays);
        var previousTo = windowFrom;

        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var trendStart = today.AddDays(-(primaryDays - 1));
        var trendBoundaries = Enumerable.Range(0, primaryDays)
            .Select(i => trendStart.AddDays(i))
            .ToList();
        var trendIndexByBoundary = trendBoundaries
            .Select((b, i) => (Boundary: b, Index: i))
            .ToDictionary(t => t.Boundary, t => t.Index);
        var trendBuckets = new QualityTrendAccumulator[trendBoundaries.Count];
        for (var i = 0; i < trendBuckets.Length; i++)
            trendBuckets[i] = new QualityTrendAccumulator();

        var issues = await _loader.LoadProjectedAsync(db, projectId);

        var projectIssueIds = issues.Select(i => i.Id).ToList();

        var shipTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var runIdsByIssue = issues.ToDictionary(
            i => i.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        if (projectIssueIds.Count > 0)
        {
            var sourceSet = new HashSet<string>(
                projectIssueIds.Select(id => IssueSourcePrefix + id),
                StringComparer.Ordinal);
            var workStartAndComplete = await ScanIssueEventsByProjectSourceAsync(
                db, projectId, typeFilter: [WorkStartedType, WorkCompletedType], includeData: true);

            foreach (var e in workStartAndComplete)
            {
                if (!sourceSet.Contains(e.Source)) continue;

                var issueId = e.Source[IssueSourcePrefix.Length..];

                if (e.Type == WorkStartedType)
                {
                    var workflowRunId = ReadWorkflowRunId(e.Data);
                    if (!string.IsNullOrWhiteSpace(workflowRunId) && runIdsByIssue.TryGetValue(issueId, out var ids))
                        ids.Add(workflowRunId);
                }
                else if (e.Type == WorkCompletedType)
                {
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
        var (runs, eventFactsByRun) = await LoadAndPairWorkflowRunsAsync(db, allRunIds);

        var window = new QualityAccumulator();
        var previous = new QualityFirstTimeRightAccumulator();

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

            if (shipTime >= windowFrom && shipTime <= windowTo)
            {
                Accumulate(window, isFirstTimeRight, stageRework);

                var shipDay = DateOnly.FromDateTime(shipTime.UtcDateTime.Date);
                if (trendIndexByBoundary.TryGetValue(shipDay, out var trendIdx))
                    Accumulate(trendBuckets[trendIdx], isFirstTimeRight, stageRework);
            }
            else if (shipTime >= previousFrom && shipTime < previousTo)
            {
                Accumulate(previous, isFirstTimeRight);
            }
        }

        return new QualityMetricsResult(
            BuildWindow(windowFrom, windowTo, window),
            BuildPreviousWindow(previous),
            BuildTrend(windowFrom, windowTo, trendBoundaries, trendBuckets));
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
        DateTimeOffset now,
        int? windowDays = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var days = windowDays ?? 7;
        var windowFrom = now.AddDays(-days);
        var windowTo = now;

        // Load the project's issues via the shared read-model loader so
        // approval projection semantics stay shared with the read model.
        var issues = await _loader.LoadProjectedAsync(db, projectId);
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
        var median = ComputeMedian(samples);

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
    /// </summary>
    public async Task<DeliveryTimeResult> GetDeliveryTimesAsync(
        string projectId,
        DateTimeOffset now,
        int? windowDays = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var days = windowDays ?? 30;
        var windowFrom = now.AddDays(-days);
        var windowTo = now;

        var previousWindowFrom = now.AddDays(-2 * days);
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
            var workStartedEvents = await ScanIssueEventsByProjectSourceAsync(
                db, projectId, typeFilter: [WorkStartedType]);

            foreach (var e in workStartedEvents)
            {
                var issueId = e.Source[IssueSourcePrefix.Length..];
                if (!earliestWorkStartedByIssue.TryGetValue(issueId, out var existing) || e.Time < existing)
                {
                    earliestWorkStartedByIssue[issueId] = e.Time;
                }
            }
        }

        var points = new List<DeliveryTimePoint>(issuesById.Count);
        double previousCycleSum = 0;
        int previousCycleCount = 0;

        foreach (var issue in issuesById.Values)
        {
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
    /// (<c>IssueWorkStarted</c> / <c>IssueWorkCompleted</c>) — no new
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
        DateTimeOffset now,
        int? windowDays = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var days = windowDays ?? 30;
        var windowFrom = now.AddDays(-days);
        var windowTo = now;

        // Resolve the project's issue set as `IssueReadModel` so we can
        // feed `LoadWorkflowStatesAsync` (which expects the read-model
        // shape) for the approval-wait projection.
        var issueReadModels = await _loader.LoadProjectedAsync(db, projectId);

        if (issueReadModels.Count == 0)
        {
            return BuildEmptyStageDurationResult(windowFrom, windowTo);
        }

        var issuesById = issueReadModels.ToDictionary(i => i.Id, StringComparer.Ordinal);

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
        // Captures WorkStarted / WorkCompleted / Closed / Reopened so
        // the stage-duration surface and the stage-population snapshot
        // job produce the same "latest stage" verdict for the same
        // issue.
        var lifecycleEventsByIssue = new Dictionary<string, List<IssueStageAttribution.AttributionEvent>>(StringComparer.Ordinal);

        var lifecycleTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            WorkStartedType,
            WorkCompletedType,
            ClosedType,
            "com.mohist.issue.reopened",
        };
        var lifecycleEvents = await ScanIssueEventsByProjectSourceAsync(
            db, projectId, typeFilter: lifecycleTypes, includeData: true);

        foreach (var e in lifecycleEvents)
        {
            var issueId = e.Source[IssueSourcePrefix.Length..];

            // Record every lifecycle event the shared attribution
            // core consumes; the WorkStarted / WorkCompleted
            // branches below also drive the existing per-issue
            // run-id / earliest-start accumulators.
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
            else if (e.Type == WorkCompletedType)
            {
                var wrId = ReadWorkflowRunId(e.Data);
                if (!string.IsNullOrWhiteSpace(wrId) && runIdsByIssue.TryGetValue(issueId, out var ids))
                    ids.Add(wrId);
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

    /// <summary>
    /// Shared scan over <c>IssueEvents</c> constrained to a project's
    /// issue sources. SQLite cannot translate <c>DateTimeOffset</c>
    /// against the TEXT <c>Time</c> column, so we materialize all
    /// candidate rows and filter the project-source / type predicates
    /// in memory. At v1 volumes (≤ 30/12 buckets per project) the
    /// candidate set is small; profiling per design D-OQ2 will tell us
    /// whether we need an explicit index on (Type, Time) later.
    /// </summary>
    private async Task<List<IssueEventRowLite>> ScanIssueEventsByProjectSourceAsync(
        MohistDbContext db,
        string projectId,
        IReadOnlyCollection<string>? typeFilter = null,
        bool includeData = false)
    {
        var projectIssueIds = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => row.IssueId)
            .ToListAsync();

        if (projectIssueIds.Count == 0)
        {
            return new List<IssueEventRowLite>();
        }

        var projectSources = new HashSet<string>(
            projectIssueIds.Select(id => IssueSourcePrefix + id),
            StringComparer.Ordinal);

        var typeSet = typeFilter is null
            ? null
            : new HashSet<string>(typeFilter, StringComparer.Ordinal);

        // Pull Source/Id/Type/Time (and Data when the caller needs to
        // read workflowRunId off the payload). Anything wider is a
        // spec violation.
        var candidates = includeData
            ? await db.IssueEvents.AsNoTracking()
                .Select(e => new { e.Source, e.Id, e.Type, e.Time, Data = (JsonElement?)e.Data })
                .ToListAsync()
            : await db.IssueEvents.AsNoTracking()
                .Select(e => new { e.Source, e.Id, e.Type, e.Time, Data = (JsonElement?)null })
                .ToListAsync();

        return candidates
            .Where(r => projectSources.Contains(r.Source)
                && (typeSet is null || typeSet.Contains(r.Type)))
            .Select(r => new IssueEventRowLite(r.Source, r.Id, r.Type, r.Time, r.Data))
            .ToList();
    }

    private readonly record struct IssueEventRowLite(
        string Source,
        long Id,
        string Type,
        DateTimeOffset Time,
        JsonElement? Data);

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

                var stageHasRework = stage.Tasks.Any(IsRecoveryTask);
                if (stageHasRework)
                    isFirstTimeRight = false;

                stageRework[stage.Id] = stageRework.GetValueOrDefault(stage.Id) || stageHasRework;
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

    private static bool IsRecoveryTask(TaskRun task) =>
        task.DefinitionId.StartsWith("recover:", StringComparison.Ordinal)
        || task.Id.StartsWith("recover:", StringComparison.Ordinal);

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

    private static QualityPreviousWindow BuildPreviousWindow(QualityFirstTimeRightAccumulator accumulator)
    {
        var sampleCount = accumulator.SampleCount;
        double? firstTimeRightRate = sampleCount == 0
            ? null
            : (double)accumulator.FirstTimeRightCount / sampleCount;
        return new QualityPreviousWindow(sampleCount, firstTimeRightRate);
    }

    private QualityTrend BuildTrend(
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
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
            WindowFrom: windowFrom,
            WindowTo: windowTo,
            Points: points);
    }

    /// <summary>
    /// Shared workflow-run discovery + per-run event-fact loader. Used
    /// by both quality and stage-duration metrics to find the runs an
    /// issue participated in and the per-run durable event history.
    /// </summary>
    private async Task<(Dictionary<string, WorkflowRun> Runs, Dictionary<string, List<WorkflowRunEventFact>> EventFactsByRun)>
        LoadAndPairWorkflowRunsAsync(
            MohistDbContext db,
            IEnumerable<string> workflowRunIds)
    {
        var runs = await LoadWorkflowRunsAsync(db, workflowRunIds);
        var eventFactsByRun = await LoadWorkflowRunEventFactsAsync(db, workflowRunIds);
        return (runs, eventFactsByRun);
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
            var run = DeserializeRun(row.WorkflowRunId, row.State);
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

    private static string? ReadWorkflowRunId(JsonElement? data)
    {
        if (!data.HasValue || data.Value.ValueKind != JsonValueKind.Object) return null;
        if (data.Value.TryGetProperty("workflowRunId", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        if (data.Value.TryGetProperty("WorkflowRunId", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            return pascal.GetString();
        return null;
    }

    private static string? ReadWorkflowEventStage(JsonElement? data)
    {
        if (!data.HasValue || data.Value.ValueKind != JsonValueKind.Object) return null;
        if (data.Value.TryGetProperty("stage", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        if (data.Value.TryGetProperty("Stage", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            return pascal.GetString();
        return null;
    }

    private static string? ReadWorkflowEventCheckName(JsonElement? data)
    {
        if (!data.HasValue || data.Value.ValueKind != JsonValueKind.Object) return null;
        if (data.Value.TryGetProperty("checkName", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        if (data.Value.TryGetProperty("CheckName", out var pascal) && pascal.ValueKind == JsonValueKind.String)
            return pascal.GetString();
        return null;
    }

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

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(
        MohistDbContext db,
        IReadOnlyCollection<string> workflowRunIds)
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
            var run = DeserializeRun(row.WorkflowRunId, row.State);
            if (run is null) continue;
            var snapshot = WorkflowStatusMapper.BuildStatusView(run, definition: null);
            if (snapshot is not null)
                workflows[row.WorkflowRunId] = snapshot;
        }
        return workflows;
    }

    private async Task<Dictionary<string, WorkflowStatusView>> LoadWorkflowStatesAsync(
        MohistDbContext db,
        IReadOnlyCollection<IssueReadModel> issues)
    {
        var workflowRunIds = issues
            .Select(i => i.WorkflowRunId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await LoadWorkflowStatesAsync(db, workflowRunIds);
    }

    private static double ComputeMedian(List<double> sortedSamples)
    {
        var count = sortedSamples.Count;
        return count % 2 == 1
            ? sortedSamples[count / 2]
            : (sortedSamples[count / 2 - 1] + sortedSamples[count / 2]) / 2.0;
    }

    private static StageDurationResult BuildEmptyStageDurationResult(DateTimeOffset from, DateTimeOffset to) =>
        new(
            new StageDurationWindow(from, to),
            Array.Empty<StageDurationStageAggregate>(),
            null,
            new StageDurationWaitBreakout(null, null));

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
            projectDefaultId: await _loader.LoadProjectDefaultTemplateAsync(db, projectId),
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
    /// caller has already loaded (work-started / work-completed /
    /// closed / reopened); stage events come from the per-run
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

    private WorkflowRun? DeserializeRun(string workflowRunId, string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowRun>(WorkflowRunStore.MigrateLegacyWorkflowRunJson(json), JSON.Options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cannot include workflow run {WorkflowRunId} in issue metrics: persisted state is invalid. The run will be omitted from metrics until repaired.",
                workflowRunId);
            return null;
        }
    }
}
