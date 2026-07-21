using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Authoritative home for <see cref="AgentSession"/> DTO projections and
/// the label-filter dictionary builder shared by the read-side siblings
/// (<see cref="AgentSessionQuerier"/>, <see cref="AgentActivityFeedAssembler"/>,
/// <see cref="AgentUsageReporter"/>). Centralises the "byte-alignment"
/// invariant that previously lived only in prose comments: every
/// consumer now invokes the same method, so the projections cannot drift
/// (issue-327 T-003, issue-370 T-001 / design D1, D6).
/// </summary>
/// <remarks>
/// Pure (no DB, no clock, no DI) so each method is a one-token swap for
/// the <c>internal static</c> on <see cref="AgentSessionQuerier"/> it
/// replaces; matches the <c>IssueRowMapper</c> /
/// <c>WorkflowStatusMapper</c> precedent. The mapper also houses
/// <see cref="Labels"/> (the single shared label-filter dictionary
/// builder) so the three callers no longer each carry their own copy
/// (design D6).
/// </remarks>
internal static class AgentSessionDtoMapper
{
    /// <summary>
    /// Projects the session's <see cref="AgentSession.Status.UsageSummary"/>
    /// into an <see cref="AgentUsageDto"/>, attaching the bounded
    /// <see cref="ContextUsageHistoryEntryDto"/> trend so the activity
    /// surface can render a context-usage mini-chart (issue-245 T-002 /
    /// design D5).
    /// </summary>
    internal static AgentUsageDto ToUsageDto(AgentSession s) =>
        ToUsageDto(AgentSessionJsonHelper.Usage(s), BuildUsageHistoryDto(s));

    /// <summary>
    /// Projects a standalone <see cref="AgentUsageSummary"/> into an
    /// <see cref="AgentUsageDto"/> with no usage-history trend. Used by
    /// the read-side paths that already know the history is empty
    /// (e.g. summary cards where the bounded history is intentionally
    /// omitted).
    /// </summary>
    internal static AgentUsageDto ToUsageDto(AgentUsageSummary u) =>
        new(u.InputTokens, u.OutputTokens, u.TotalTokens, u.CachedReadTokens, u.ThoughtTokens,
            u.CostAmount, u.CostCurrency, u.ContextWindowUsed, u.ContextWindowSize,
            AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize),
            ContextHealthClassifier.Classify(AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize)));

    /// <summary>
    /// Projects an <see cref="AgentUsageSummary"/> together with an
    /// explicit usage-history trend (the form used by the activity feed
    /// where the bounded history is sourced from
    /// <see cref="AgentSessionDtoMapper.BuildUsageHistoryDto"/>).
    /// </summary>
    internal static AgentUsageDto ToUsageDto(AgentUsageSummary u, IReadOnlyList<ContextUsageHistoryEntryDto>? history) =>
        new(u.InputTokens, u.OutputTokens, u.TotalTokens, u.CachedReadTokens, u.ThoughtTokens,
            u.CostAmount, u.CostCurrency, u.ContextWindowUsed, u.ContextWindowSize,
            AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize),
            ContextHealthClassifier.Classify(AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize)),
            history);

    /// <summary>
    /// Builds the <see cref="ContextUsageHistoryEntryDto"/> projection
    /// from <see cref="AgentSession.Status.ContextUsageHistory"/>. Returns
    /// <c>null</c> when the session has not yet recorded any usage
    /// (grain never thinned a sample) so the wire stays quiet for
    /// historical/legacy sessions. An empty list is projected as
    /// <c>null</c> for the same reason (issue-245 T-002 / design D5).
    /// </summary>
    internal static IReadOnlyList<ContextUsageHistoryEntryDto>? BuildUsageHistoryDto(AgentSession domainSession)
    {
        var history = domainSession.Status.ContextUsageHistory;
        if (history is null || history.Count == 0) return null;

        return history
            .Select(e => new ContextUsageHistoryEntryDto(e.At.ToString("o"), e.Percent))
            .ToList();
    }

    /// <summary>
    /// Projects an <see cref="AgentSessionTranscriptSummary"/> (the
    /// per-session roll-up produced by
    /// <see cref="TranscriptEventSummaryProjector"/>) into an
    /// <see cref="AgentEventSummaryDto"/>. Returns an all-null DTO when
    /// the session has no events to summarise so callers can drop the
    /// field on the wire. The context-exhaustion and
    /// suspected-context-exhaustion flags remain <c>null</c> (not
    /// <c>false</c>) when the failure category is neither
    /// <see cref="ContextExhaustionClassifier.ContextExhaustionCategory"/>
    /// nor
    /// <see cref="ContextExhaustionClassifier.SuspectedContextExhaustionCategory"/>,
    /// preserving the pre-change wire shape.
    /// </summary>
    internal static AgentEventSummaryDto ToEventSummaryDto(AgentSessionTranscriptSummary? s) =>
        s is null
            ? new AgentEventSummaryDto(null, null, null, null, null, null)
            : new(
                s.ResolvedModel,
                s.FailureCategory,
                string.Equals(s.FailureCategory, ContextExhaustionClassifier.ContextExhaustionCategory, StringComparison.Ordinal) ? true : null,
                string.Equals(s.FailureCategory, ContextExhaustionClassifier.SuspectedContextExhaustionCategory, StringComparison.Ordinal) ? true : null,
                s.ToolCallCount,
                s.ToolErrorCount);

    /// <summary>
    /// Builds the <see cref="RuntimeSessionLineageEntryDto"/> projection
    /// from <see cref="AgentSession.Status.RuntimeSessionLineage"/>. When
    /// the grain hasn't yet recorded an explicit lineage (legacy
    /// rehydration) but the session is currently bound, a single entry is
    /// synthesized so the UI can still distinguish "no chain at all"
    /// (historical single binding) from "real chain" (>=2 entries).
    /// Returns <c>null</c> only when there is truly nothing to surface.
    /// </summary>
    internal static IReadOnlyList<RuntimeSessionLineageEntryDto>? BuildLineageDto(AgentSession domainSession)
    {
        var lineage = domainSession.Status.RuntimeSessionLineage;
        if (lineage is not null && lineage.Count > 0)
        {
            return lineage
                .Select(e => new RuntimeSessionLineageEntryDto(
                    e.AgentRuntimeSessionId,
                    e.Runtime,
                    e.BoundAt.ToString("o")))
                .ToList();
        }

        if (!string.IsNullOrEmpty(domainSession.Status.AgentRuntimeSessionId))
        {
            var boundAt = domainSession.Status.BoundAt ?? domainSession.Status.CreatedAt;
            return
            [
                new RuntimeSessionLineageEntryDto(
                    domainSession.Status.AgentRuntimeSessionId,
                    domainSession.Runtime.Runtime,
                    boundAt.ToString("o"))
            ];
        }

        return null;
    }

    /// <summary>
    /// Projects a single transcript part into the
    /// <see cref="TranscriptEventProjection"/> shape consumed by the
    /// latest-event loader and the event-summary batch path. The
    /// payload of <c>text</c> and <c>reasoning</c> part types is
    /// rewritten to a serialized <c>{ text }</c> object so both callers
    /// observe identical projected events; all other part types pass
    /// through verbatim (issue-327 T-002, issue-370 T-001 / design D1).
    /// <see cref="TranscriptEventProjection.TurnId"/> is propagated so the
    /// latest-fact reducer can apply the (turn sequence, part sequence,
    /// part id) total order the AgentJob-owned close contract depends on
    /// (issue-449 design decision 4).
    /// </summary>
    internal static TranscriptEventProjection ToProjection(string sessionId, AgentSessionTranscriptPartRow part) => new()
    {
        Id = part.Id,
        TurnId = part.TurnId,
        SessionId = sessionId,
        Sequence = part.Sequence,
        Type = part.Type,
        PayloadJson = part.Type is "text" or "reasoning"
            ? JSON.Serialize(new { text = part.Text })
            : part.PayloadJson,
        CreatedAt = part.LastSeenAt,
    };

    /// <summary>
    /// Builds the label-filter dictionary consumed by
    /// <see cref="AgentSessionQuery.ListByLabelsAsync"/>. Skips any pair
    /// whose key or value is null/empty/whitespace and uses ordinal
    /// (case-sensitive) comparison so a downstream index lookup is
    /// case-correct (issue-327 T-003, issue-370 T-001 / design D6).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Labels(params (string Key, string? Value)[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            result[key] = value;
        }
        return result;
    }
}
