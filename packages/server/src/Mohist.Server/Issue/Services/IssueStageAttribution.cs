using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Shared stage-attribution core used by both
/// <see cref="IssueMetricsQuerier.GetStageDurationsAsync"/> (the
/// <c>workflow-stage-duration-metrics</c> surface) and the
/// stage-population snapshot job. The two surfaces must agree on the
/// issue's latest stage under the same
/// <em>latest-attempt, latest-run-wins, invalidate-on-restart</em>
/// idiom, so the rule lives in a single pure function rather than two
/// drifting copies.
/// <para>
/// Input is a time-bounded event stream (<see cref="AttributionEvent"/>)
/// that has already been filtered to <c>Time &lt;= dayEndUtc</c> by the
/// caller — SQLite cannot translate <see cref="DateTimeOffset"/>
/// comparisons against the TEXT <c>Time</c> column, so the day bound
/// must be applied in LINQ-to-objects after materialization (the
/// established pattern, see <c>IssueQuerier.cs</c> completion-buckets
/// loader).
/// </para>
/// <para>
/// Lifecycle events use the durable CloudEvents <c>type</c> strings from
/// <see cref="EventCatalog.ReverseDns"/>; per-run <c>StageStarted</c>
/// / <c>StageCompleted</c> events also use those strings. <c>Stage</c>
/// is only set for stage events — lifecycle events carry no stage id.
/// </para>
/// </summary>
public static class IssueStageAttribution
{
    /// <summary>
    /// Discriminated attribution result. <see cref="Kind.Backlog"/>,
    /// <see cref="Kind.Done"/>, and <see cref="Kind.Cancelled"/> are the
    /// three terminal-of-the-pipeline states; <see cref="Kind.Stage"/>
    /// means the issue is in-flight and currently attributed to the
    /// named workflow stage. <see cref="Kind.None"/> is a defensive
    /// sentinel for the vanishing edge case where work has started but
    /// no <c>StageStarted</c> has been observed yet — the snapshot job
    /// treats this as "not in any bucket" (drops the issue for that
    /// day).
    /// </summary>
    public enum Kind
    {
        Backlog,
        Done,
        Cancelled,
        Stage,
        None,
    }

    public readonly record struct Attribution(Kind Kind, string? Stage = null, string? WorkflowRunId = null)
    {
        public static readonly Attribution Backlog = new(Kind.Backlog);
        public static readonly Attribution Done = new(Kind.Done);
        public static readonly Attribution Cancelled = new(Kind.Cancelled);
        public static readonly Attribution None = new(Kind.None);
        public static Attribution OfStage(string stage, string? workflowRunId = null) =>
            new(Kind.Stage, stage, workflowRunId);
    }

    /// <summary>
    /// One time-bounded event for the issue. <see cref="Type"/> is a
    /// CloudEvents <c>type</c> string (a <c>com.mohist.*</c> value from
    /// <see cref="EventCatalog.ReverseDns"/>). <see cref="Stage"/> is
    /// only set for per-run <c>StageStarted</c> / <c>StageCompleted</c>
    /// events; lifecycle events carry no stage id.
    /// </summary>
    public readonly record struct AttributionEvent(
        string Type,
        DateTimeOffset Time,
        long Id,
        string? Stage,
        string? WorkflowRunId = null);

    /// <summary>
    /// Compute the single attributed stage for the issue as of
    /// <paramref name="dayEndUtc"/>. The rule walks the time-bounded
    /// event stream in the durable <c>(Time, Id)</c> append-only
    /// order and projects the issue's current state:
    /// <list type="bullet">
    /// <item><description><c>IssueWorkStarted</c>
    /// (<c>com.mohist.issue.work-started</c>) flips state to in-progress;</description></item>
    /// <item><description><c>IssueCompleted</c>
    /// (<c>com.mohist.issue.completed</c>) flips state to done;</description></item>
    /// <item><description><c>IssueCancelled</c>
    /// (<c>com.mohist.issue.cancelled</c>) flips state to cancelled;</description></item>
    /// <item><description><c>IssueReopened</c>
    /// (<c>com.mohist.issue.reopened</c>) flips state back to backlog.</description></item>
    /// </list>
    /// Once the projected state is known:
    /// <list type="number">
    /// <item><description>state = cancelled → <see cref="Kind.Cancelled"/>
    /// (excluded from the flow population);</description></item>
    /// <item><description>state = backlog (no <c>IssueWorkStarted</c> as
    /// of the day) → <see cref="Kind.Backlog"/>;</description></item>
    /// <item><description>state = done → <see cref="Kind.Done"/>;</description></item>
    /// <item><description>state = in-progress → the stage id of the
    /// latest <c>StageStarted</c>
    /// (<c>com.mohist.workflow.stage.started</c>) as of the day, or
    /// <see cref="Kind.None"/> when no stage has been entered yet.</description></item>
    /// </list>
    /// The "latest <c>StageStarted</c>" determination uses the same
    /// <c>(Time, Id)</c> append-only ordering — a later
    /// <c>StageStarted</c> for the same stage id supersedes an earlier
    /// one (invalidate-on-restart) and a later <c>StageStarted</c>
    /// from a different run supersedes the earlier run's progress
    /// (latest-run-wins); both fall out of the same ordering for free.
    /// </summary>
    /// <param name="events">Already-filtered, time-bounded event stream
    /// for the issue; the caller is responsible for trimming
    /// <c>Time &lt;= dayEndUtc</c> in memory after materialization.
    /// The stream need not be pre-sorted — the function sorts by
    /// <c>(Time, Id)</c> defensively.</param>
    /// <param name="stageOrder">Ordered workflow stage set; an observed
    /// <c>StageStarted</c> for a stage id outside this set is still
    /// attributed (the rule still applies) — the set is documented but
    /// not validated by this function.</param>
    /// <param name="dayEndUtc">UTC end-of-day for the snapshot day.
    /// Unused by the rule itself — kept in the signature so callers
    /// document the time bound that produced the input stream.</param>
    public static Attribution Attribute(
        IReadOnlyList<AttributionEvent> events,
        IReadOnlyList<string> stageOrder,
        DateTimeOffset dayEndUtc)
    {
        _ = stageOrder;
        _ = dayEndUtc;

        if (events is null) throw new ArgumentNullException(nameof(events));
        if (stageOrder is null) throw new ArgumentNullException(nameof(stageOrder));

        if (events.Count == 0) return Attribution.Backlog;

        var sorted = new AttributionEvent[events.Count];
        for (var i = 0; i < sorted.Length; i++) sorted[i] = events[i];
        Array.Sort(sorted, static (a, b) =>
        {
            var byTime = a.Time.CompareTo(b.Time);
            return byTime != 0 ? byTime : a.Id.CompareTo(b.Id);
        });

        // Project the issue's current state by walking events in
        // (Time, Id) order. Each lifecycle event flips the state in
        // place; an issue that has never been started stays in
        // Backlog; an issue that has been reopened walks back to
        // Backlog so a later IssueWorkStarted re-enters InProgress.
        var state = State.Backlog;
        string? latestStage = null;
        string? activeRunId = null;

        for (var i = 0; i < sorted.Length; i++)
        {
            var e = sorted[i];
            if (string.Equals(e.Type, EventCatalog.ReverseDns.IssueWorkStarted, StringComparison.Ordinal))
            {
                state = State.InProgress;
                latestStage = null;
                activeRunId = string.IsNullOrWhiteSpace(e.WorkflowRunId) ? null : e.WorkflowRunId;
            }
            else if (string.Equals(e.Type, EventCatalog.ReverseDns.IssueCompleted, StringComparison.Ordinal))
            {
                state = State.Done;
            }
            else if (string.Equals(e.Type, EventCatalog.ReverseDns.IssueCancelled, StringComparison.Ordinal))
            {
                state = State.Cancelled;
            }
            else if (string.Equals(e.Type, "com.mohist.issue.reopened", StringComparison.Ordinal))
            {
                state = State.Backlog;
                latestStage = null;
                activeRunId = null;
            }
            else if (string.Equals(e.Type, EventCatalog.ReverseDns.StageStarted, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(e.Stage))
            {
                if (activeRunId is null
                    || string.Equals(e.WorkflowRunId, activeRunId, StringComparison.Ordinal))
                {
                    latestStage = e.Stage;
                }
            }
        }

        return state switch
        {
            State.Cancelled => Attribution.Cancelled,
            State.Backlog => Attribution.Backlog,
            State.Done => new Attribution(Kind.Done, WorkflowRunId: activeRunId),
            State.InProgress => latestStage is null ? Attribution.None : Attribution.OfStage(latestStage, activeRunId),
            _ => Attribution.None,
        };
    }

    private enum State { Backlog, InProgress, Done, Cancelled }
}
