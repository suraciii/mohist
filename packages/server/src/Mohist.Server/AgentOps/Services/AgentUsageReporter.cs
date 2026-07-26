using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.AgentOps.Services;

/// <summary>
/// Reports usage and cost figures for the
/// <c>GET /api/projects/{projectRef}/agent/usage</c> and
/// <c>GET /api/projects/{projectRef}/agent/cost</c> endpoints
///. Composes the usage timeseries
/// (with cumulative-cost-per-ship projection), the cumulative cost rollup
/// (total / today), and the windowed cost figures. Pure refactor: each
/// response is byte-for-byte identical to the pre-split core-querier
/// implementation.
/// </summary>
/// <remarks>
/// Previously these methods (and their private helpers
/// <c>ComputePreWindowSpendAsync</c>, <c>ComputeCumulativeCostPerShipAsync</c>,
/// <c>LoadCompletedIssueCountsAsync</c>, <c>BuildFigure</c>,
/// <c>HasUsage</c>, and the <c>UsageBucketData</c> accumulator) lived on
/// the core <see cref="AgentSessionQuerier"/> together with five unrelated
/// concerns. Splitting the usage/cost reporting out keeps the core
/// querier a pure query service and gives the reporting logic a
/// navigable home of its own.
/// </remarks>
public sealed class AgentUsageReporter : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly AgentSessionQuery _sessionQuery;
    private readonly TimeProvider _timeProvider;

    public AgentUsageReporter(
        IDbContextFactory<MohistDbContext> dbFactory,
        AgentSessionQuery sessionQuery,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _sessionQuery = sessionQuery;
        _timeProvider = timeProvider;
    }

    public async Task<AgentUsageTimeseriesDto> GetUsageTimeseriesAsync(string projectId, int? windowDays = null, CancellationToken ct = default)
    {
        var now = Now();
        var days = windowDays ?? 7;
        var (bucketKind, bucketCount, bucketSizeDays) = ResolveUsageBucketGranularity(days);
        var rangeTo = now.Date.AddDays(1);
        var rangeFrom = rangeTo.AddDays(-days);

        var windowSessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedAscending,
            from: rangeFrom,
            to: rangeTo,
            ct: ct);

        var buckets = new UsageBucketData[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            var start = rangeFrom.AddDays(i * bucketSizeDays);
            var end = i == bucketCount - 1
                ? rangeTo
                : rangeFrom.AddDays((i + 1) * bucketSizeDays);
            buckets[i] = new UsageBucketData(start, end);
        }

        foreach (var record in windowSessions)
        {
            var usage = AgentSessionJsonHelper.Usage(record.Session);
            if (!HasUsage(usage)) continue;

            var createdAt = record.Session.Status.CreatedAt;
            var bucketIndex = (int)((createdAt.Date - rangeFrom.Date).TotalDays / bucketSizeDays);
            if (bucketIndex < 0 || bucketIndex >= bucketCount) continue;

            var bucket = buckets[bucketIndex];
            var costAmount = usage.CostAmount ?? 0d;
            bucket.InputTokens += usage.InputTokens ?? 0;
            bucket.OutputTokens += usage.OutputTokens ?? 0;
            bucket.TotalTokens += usage.TotalTokens ?? 0;
            bucket.CostAmount += costAmount;
            bucket.CostCurrency ??= usage.CostCurrency;
            bucket.SampleCount++;
        }

        var preWindow = await ComputePreWindowSpendAsync(projectId, rangeFrom, ct);

        var cumulative = await ComputeCumulativeCostPerShipAsync(
            projectId, rangeFrom, preWindow.Spend, preWindow.Samples, preWindow.Currency, buckets, bucketSizeDays, ct);

        return new AgentUsageTimeseriesDto(
            rangeFrom,
            rangeTo,
            bucketKind,
            buckets.Select(b => b.ToDto()).ToList(),
            cumulative);
    }

    private static (string Kind, int Count, int SizeDays) ResolveUsageBucketGranularity(int days)
    {
        if (days >= MetricsRange.NinetyDayCount) return ("week", (int)Math.Ceiling(days / 7.0), 7);
        return ("day", days, 1);
    }

    private sealed record PreWindowSpendResult(double Spend, int Samples, string? Currency);

    private async Task<PreWindowSpendResult> ComputePreWindowSpendAsync(string projectId, DateTime rangeFrom, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.LabelProjectId == projectId && s.CreatedAt < rangeFrom)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        double spend = 0;
        int samples = 0;
        string? currency = null;

        foreach (var row in rows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null) continue;

            var usage = AgentSessionJsonHelper.Usage(session);
            if (!HasUsage(usage)) continue;

            spend += usage.CostAmount ?? 0d;
            samples++;
            currency ??= usage.CostCurrency;
        }

        return new PreWindowSpendResult(spend, samples, currency);
    }

    private async Task<IReadOnlyList<CumulativeCostPerShipPointDto>> ComputeCumulativeCostPerShipAsync(
        string projectId,
        DateTime rangeFrom,
        double preWindowSpend,
        int preWindowSamples,
        string? currency,
        UsageBucketData[] buckets,
        int bucketSizeDays,
        CancellationToken ct)
    {
        List<DateTime> shippedDates;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.Issues.AsNoTracking()
                .Where(row => row.ProjectId == projectId)
                .ToListAsync(ct);

            shippedDates = IssueRowMapper.Deserialize(rows)
                .Where(issue => issue.Status == IssueStatus.Done && issue.CompletedAt.HasValue)
                .Select(issue => issue.CompletedAt!.Value)
                .ToList();
        }

        var preWindowShipped = shippedDates.Count(d => d < rangeFrom);
        var result = new List<CumulativeCostPerShipPointDto>(buckets.Length);
        double cumulativeCost = preWindowSpend;
        int cumulativeSamples = preWindowSamples;
        var cumulativeShipped = preWindowShipped;
        string? resolvedCurrency = currency;

        for (var i = 0; i < buckets.Length; i++)
        {
            var dayStart = rangeFrom.AddDays((long)i * bucketSizeDays);
            var dayEnd = rangeFrom.AddDays((long)(i + 1) * bucketSizeDays);

            cumulativeCost += buckets[i].CostAmount;
            cumulativeSamples += buckets[i].SampleCount;
            resolvedCurrency ??= buckets[i].CostCurrency;

            var dayShipped = shippedDates.Count(d => d >= dayStart && d < dayEnd);
            cumulativeShipped += dayShipped;

            double? costForDay = cumulativeSamples > 0 || cumulativeShipped > 0 ? cumulativeCost : null;
            double? costPerShip = cumulativeShipped > 0
                ? cumulativeCost / cumulativeShipped
                : null;

            result.Add(new CumulativeCostPerShipPointDto(
                dayEnd,
                costForDay,
                cumulativeSamples > 0 ? resolvedCurrency : null,
                cumulativeShipped,
                costPerShip));
        }

        return result;
    }

    public async Task<AgentCostRollupRawData> GetCostRollupAsync(string projectId, CancellationToken ct = default)
    {
        var allSessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedAscending,
            ct: ct);

        var todayStart = Now().Date;
        var todayEnd = todayStart.AddDays(1);

        double totalCost = 0d;
        int totalSamples = 0;
        string? totalCurrency = null;

        double todayCost = 0d;
        int todaySamples = 0;
        string? todayCurrency = null;

        foreach (var record in allSessions)
        {
            var usage = AgentSessionJsonHelper.Usage(record.Session);
            if (!HasUsage(usage)) continue;

            var costAmount = usage.CostAmount ?? 0d;

            totalCost += costAmount;
            totalSamples++;
            totalCurrency ??= usage.CostCurrency;

            var createdAt = record.Session.Status.CreatedAt;
            if (createdAt >= todayStart && createdAt < todayEnd)
            {
                todayCost += costAmount;
                todaySamples++;
                todayCurrency ??= usage.CostCurrency;
            }
        }

        return new AgentCostRollupRawData(
            new AgentCostMetricDto(totalSamples > 0 ? totalCost : null, totalCurrency, totalSamples),
            new AgentCostMetricDto(todaySamples > 0 ? todayCost : null, todayCurrency, todaySamples));
    }

    /// <summary>
    /// Computes windowed (current + previous) spend and per-issue cost for
    /// the agent-cost surface. Both windows
    /// are 30 days when <paramref name="windowDays"/> is <c>null</c> (the
    /// Dashboard back-compat default); when a value is supplied, both
    /// windows scale to that length and the previous window is the same
    /// length, immediately preceding. Both advance with the current time.
    /// Spend is the sum of per-session
    /// <see cref="AgentUsageSummary.CostAmount"/> over sessions whose
    /// creation time falls in the window; per-issue cost is the window's
    /// spend divided by the count of issues completed (reached
    /// <see cref="IssueStatus.Done"/>) within the window. Each metric's
    /// emptiness is evaluated independently per metric per window:
    /// no sessions ⟹ empty spend; no completed issues ⟹ empty per-issue
    /// cost. The two emptiness states share no fallback — a window with
    /// spend but no completed issues returns a real spend and an empty
    /// per-issue cost, and vice-versa.
    /// </summary>
    public async Task<AgentCostWindowedData> GetCostWindowedAsync(string projectId, int? windowDays = null, CancellationToken ct = default)
    {
        var now = Now();
        var days = windowDays ?? 30;
        var currentFrom = now.Date.AddDays(-(days - 1));
        var currentTo = now.Date.AddDays(1);
        var previousFrom = currentFrom.AddDays(-days);
        var previousTo = currentFrom;

        var sessions = await _sessionQuery.ListByLabelsAsync(
            AgentSessionDtoMapper.Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedAscending,
            ct: ct);

        double currentSpend = 0d;
        int currentSamples = 0;
        string? currentCurrency = null;

        double previousSpend = 0d;
        int previousSamples = 0;
        string? previousCurrency = null;

        foreach (var record in sessions)
        {
            var usage = AgentSessionJsonHelper.Usage(record.Session);
            if (!HasUsage(usage)) continue;

            var createdAt = record.Session.Status.CreatedAt;
            var costAmount = usage.CostAmount ?? 0d;

            if (createdAt >= currentFrom && createdAt < currentTo)
            {
                currentSpend += costAmount;
                currentSamples++;
                currentCurrency ??= usage.CostCurrency;
            }
            else if (createdAt >= previousFrom && createdAt < previousTo)
            {
                previousSpend += costAmount;
                previousSamples++;
                previousCurrency ??= usage.CostCurrency;
            }
        }

        var (currentCompleted, previousCompleted) = await LoadCompletedIssueCountsAsync(
            projectId, currentFrom, currentTo, previousFrom, previousTo, ct);

        return new AgentCostWindowedData(
            BuildFigure(currentSpend, currentSamples, currentCurrency, currentCompleted),
            BuildFigure(previousSpend, previousSamples, previousCurrency, previousCompleted));
    }

    private static AgentCostWindowedFigure BuildFigure(
        double spend,
        int sessionSamples,
        string? currency,
        int completedIssues)
    {
        var spendDto = new AgentCostMetricDto(
            sessionSamples > 0 ? spend : null,
            currency,
            sessionSamples);

        AgentCostMetricDto perIssueCostDto;
        if (completedIssues <= 0)
        {
            perIssueCostDto = new AgentCostMetricDto(null, currency, 0);
        }
        else if (sessionSamples <= 0)
        {
            // Genuine-empty: no spend recorded in the window. Per-issue cost
            // has no numerator to divide by; surface the empty result rather
            // than fabricating a 0.0 to match a fabricated spend.
            perIssueCostDto = new AgentCostMetricDto(null, currency, 0);
        }
        else
        {
            perIssueCostDto = new AgentCostMetricDto(spend / completedIssues, currency, 1);
        }

        return new AgentCostWindowedFigure(spendDto, perIssueCostDto);
    }

    private async Task<(int CurrentCount, int PreviousCount)> LoadCompletedIssueCountsAsync(
        string projectId,
        DateTime currentFrom,
        DateTime currentTo,
        DateTime previousFrom,
        DateTime previousTo,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .ToListAsync(ct);

        var current = 0;
        var previous = 0;
        foreach (var issue in IssueRowMapper.Deserialize(rows))
        {
            if (issue.Status != IssueStatus.Done) continue;
            var completedAt = issue.CompletedAt;
            if (!completedAt.HasValue) continue;

            if (completedAt.Value >= currentFrom && completedAt.Value < currentTo)
                current++;
            else if (completedAt.Value >= previousFrom && completedAt.Value < previousTo)
                previous++;
        }
        return (current, previous);
    }

    private static bool HasUsage(AgentUsageSummary usage)
    {
        return usage.InputTokens.HasValue
            || usage.OutputTokens.HasValue
            || usage.TotalTokens.HasValue
            || usage.CostAmount.HasValue;
    }

    private sealed class UsageBucketData
    {
        private readonly DateTime _bucketStart;
        private readonly DateTime _bucketEnd;

        public long InputTokens;
        public long OutputTokens;
        public long TotalTokens;
        public double CostAmount;
        public string? CostCurrency;
        public int SampleCount;

        public UsageBucketData(DateTime bucketStart, DateTime bucketEnd)
        {
            _bucketStart = bucketStart;
            _bucketEnd = bucketEnd;
        }

        public UsageBucketDto ToDto() => new(
            _bucketStart,
            _bucketEnd,
            InputTokens,
            OutputTokens,
            TotalTokens,
            CostAmount,
            CostCurrency);
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
