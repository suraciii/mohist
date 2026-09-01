using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

/// <summary>
/// Unit tests for the shared stage-attribution core. The function is
/// pure (time-bounded event stream + ordered stage set → single
/// attributed stage) and is consumed by both the
/// <c>workflow-stage-duration-metrics</c> surface and the
/// stage-population snapshot job.
/// </summary>
public class IssueStageAttributionTests
{
    private static readonly string[] DefaultStageOrder = ["plan", "build", "check", "integrate"];

    private static readonly DateTimeOffset DayEnd = new(2026, 7, 1, 23, 59, 59, TimeSpan.Zero);

    [Fact]
    public void EmptyEventStream_AttributesAsBacklog()
    {
        // No events as of the day means the issue has had no work
        // start; spec: "an issue whose work has not started is
        // attributed to backlog".
        var result = IssueStageAttribution.Attribute(
            Array.Empty<IssueStageAttribution.AttributionEvent>(),
            DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Backlog, result.Kind);
    }

    [Fact]
    public void WorkStartedAndNoCompletion_AttributesAsLatestStage()
    {
        // In-flight: work started, no completion, latest
        // StageStarted is "build". Spec: "an in-flight issue is
        // attributed to the stage it most recently entered".
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 7, 1, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "plan", t: new DateTimeOffset(2026, 7, 1, 1, 0, 5, TimeSpan.Zero), id: 2),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 7, 1, 1, 30, 0, TimeSpan.Zero), id: 3),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Stage, result.Kind);
        Assert.Equal("build", result.Stage);
    }

    [Fact]
    public void WorkStartedBeforeDay_AttributesAsLatestStage()
    {
        // Work started before the day, StageStarted before the day —
        // the issue is still in-flight on the day.
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 30, 8, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "check", t: new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero), id: 2),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Stage, result.Kind);
        Assert.Equal("check", result.Stage);
    }

    [Fact]
    public void WorkCompletedAsOfDay_AttributesAsDone()
    {
        // Spec: "a completed issue is attributed to done".
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 30, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "plan", t: new DateTimeOffset(2026, 6, 30, 1, 0, 5, TimeSpan.Zero), id: 2),
            NewEvent(EventCatalog.ReverseDns.StageCompleted, stage: "plan", t: new DateTimeOffset(2026, 6, 30, 2, 0, 0, TimeSpan.Zero), id: 3),
            NewEvent(EventCatalog.ReverseDns.IssueCompleted, t: new DateTimeOffset(2026, 7, 1, 0, 30, 0, TimeSpan.Zero), id: 4),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Done, result.Kind);
    }

    [Fact]
    public void IssueCancelledAsOfDay_AttributesAsCancelled_ExcludedFromPopulation()
    {
        // Spec: "A cancelled issue is excluded from the population".
        // The durable event is com.mohist.issue.cancelled.
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 29, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.IssueCancelled, t: new DateTimeOffset(2026, 6, 30, 5, 0, 0, TimeSpan.Zero), id: 2),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Cancelled, result.Kind);
    }

    [Fact]
    public void ReattemptedBuild_LatestStageStartedWins()
    {
        // Spec: "A re-attempted stage attributes the issue to the
        // latest attempt only". The second StageStarted for "build"
        // supersedes the first; the issue is attributed to "build"
        // (the same stage id, but the second attempt's events are
        // the ones the rule picks).
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 28, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 28, 1, 0, 5, TimeSpan.Zero), id: 2),
            // First build attempt failed; rerun restarted build.
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 30, 4, 0, 0, TimeSpan.Zero), id: 3),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Stage, result.Kind);
        Assert.Equal("build", result.Stage);
    }

    [Fact]
    public void RerunFromPlan_LaterProgressInvalidated_AttributesToPlan()
    {
        // Spec: "A rerun-from-stage moves the issue back to the
        // rerun's stage". The issue had reached "check" via the
        // first run; a rerun-from-plan emits a new StageStarted
        // for "plan" that supersedes the earlier progress.
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 25, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "plan", t: new DateTimeOffset(2026, 6, 25, 1, 0, 5, TimeSpan.Zero), id: 2),
            NewEvent(EventCatalog.ReverseDns.StageCompleted, stage: "plan", t: new DateTimeOffset(2026, 6, 25, 2, 0, 0, TimeSpan.Zero), id: 3),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 26, 1, 0, 0, TimeSpan.Zero), id: 4),
            NewEvent(EventCatalog.ReverseDns.StageCompleted, stage: "build", t: new DateTimeOffset(2026, 6, 26, 2, 0, 0, TimeSpan.Zero), id: 5),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "check", t: new DateTimeOffset(2026, 6, 27, 1, 0, 0, TimeSpan.Zero), id: 6),
            // Rerun-from-plan: a fresh StageStarted for "plan"
            // invalidates the build/check progress.
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "plan", t: new DateTimeOffset(2026, 6, 30, 5, 0, 0, TimeSpan.Zero), id: 7),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Stage, result.Kind);
        Assert.Equal("plan", result.Stage);
    }

    [Fact]
    public void MultipleRuns_LatestRunWins()
    {
        // Spec: "The latest workflow run wins across the issue's
        // multiple runs". Two workflow runs: run-1 reached "build";
        // run-2 (newer) restarted at "plan" and reached "build"
        // again. The latest StageStarted is run-2's "build".
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 20, 1, 0, 5, TimeSpan.Zero), id: 2),
            // Run-1 ends; run-2 starts via a new work-started (different
            // workflowRunId would be carried in the CloudEvent data;
            // for the attribution core the events are already
            // pre-merged).
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "plan", t: new DateTimeOffset(2026, 6, 28, 1, 0, 5, TimeSpan.Zero), id: 3),
            NewEvent(EventCatalog.ReverseDns.StageCompleted, stage: "plan", t: new DateTimeOffset(2026, 6, 28, 2, 0, 0, TimeSpan.Zero), id: 4),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 29, 1, 0, 0, TimeSpan.Zero), id: 5),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Stage, result.Kind);
        Assert.Equal("build", result.Stage);
    }

    [Fact]
    public void NewerRunWorkStartedWithoutStage_DoesNotUseOldRunStage()
    {
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero), id: 1, runId: "wr_old"),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 20, 1, 0, 5, TimeSpan.Zero), id: 2, runId: "wr_old"),
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 29, 1, 0, 0, TimeSpan.Zero), id: 3, runId: "wr_new"),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.None, result.Kind);
    }

    [Fact]
    public void LateOldRunStageStarted_DoesNotOverrideActiveRun()
    {
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero), id: 1, runId: "wr_old"),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 20, 1, 0, 5, TimeSpan.Zero), id: 2, runId: "wr_old"),
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 29, 1, 0, 0, TimeSpan.Zero), id: 3, runId: "wr_new"),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "plan", t: new DateTimeOffset(2026, 6, 29, 1, 0, 5, TimeSpan.Zero), id: 4, runId: "wr_new"),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "check", t: new DateTimeOffset(2026, 6, 30, 1, 0, 0, TimeSpan.Zero), id: 5, runId: "wr_old"),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Stage, result.Kind);
        Assert.Equal("plan", result.Stage);
        Assert.Equal("wr_new", result.WorkflowRunId);
    }

    [Fact]
    public void DoneThenReopened_AttributesAsBacklog()
    {
        // A cancelled issue can be reopened (per Issue.Transitions:
        // Reopen). After reopen, no new work-started, so the issue
        // walks back to Backlog.
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 25, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.IssueCompleted, t: new DateTimeOffset(2026, 6, 26, 1, 0, 0, TimeSpan.Zero), id: 2),
            NewEvent(EventCatalog.ReverseDns.IssueCancelled, t: new DateTimeOffset(2026, 6, 27, 1, 0, 0, TimeSpan.Zero), id: 3),
            NewEvent("com.mohist.issue.reopened", t: new DateTimeOffset(2026, 6, 28, 1, 0, 0, TimeSpan.Zero), id: 4),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Backlog, result.Kind);
    }

    [Fact]
    public void ClosedAfterDone_AttributesAsCancelled()
    {
        // A done issue that subsequently gets closed (cancelled)
        // flips to cancelled per the latest-terminal-wins rule —
        // i.e. cancellation supersedes done in the durable ordering.
        // (This case is unusual — the domain forbids a Done → Close
        // transition — but the function must encode the rule
        // correctly anyway for the events as persisted.)
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.IssueCompleted, t: new DateTimeOffset(2026, 6, 25, 1, 0, 0, TimeSpan.Zero), id: 2),
            NewEvent(EventCatalog.ReverseDns.IssueCancelled, t: new DateTimeOffset(2026, 6, 26, 1, 0, 0, TimeSpan.Zero), id: 3),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.Cancelled, result.Kind);
    }

    [Fact]
    public void UnsortedEvents_AreOrderedByTimeThenId()
    {
        // The function sorts defensively by (Time, Id). Feeding
        // events out of order must produce the same result.
        var sorted = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 6, 30, 1, 0, 0, TimeSpan.Zero), id: 1),
            NewEvent(EventCatalog.ReverseDns.StageStarted, stage: "build", t: new DateTimeOffset(2026, 6, 30, 1, 30, 0, TimeSpan.Zero), id: 2),
        };
        var shuffled = new[]
        {
            sorted[1],
            sorted[0],
        };

        var a = IssueStageAttribution.Attribute(sorted, DefaultStageOrder, DayEnd);
        var b = IssueStageAttribution.Attribute(shuffled, DefaultStageOrder, DayEnd);

        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.Stage, b.Stage);
    }

    [Fact]
    public void WorkStartedNoStageEvents_AttributesAsNone()
    {
        // Defensive: work-started but no stage events observed
        // (vanishing edge case where the first stage transition is
        // in flight). The function returns None rather than
        // throwing; the snapshot job treats None as "not in any
        // bucket".
        var events = new[]
        {
            NewEvent(EventCatalog.ReverseDns.IssueWorkStarted, t: new DateTimeOffset(2026, 7, 1, 1, 0, 0, TimeSpan.Zero), id: 1),
        };

        var result = IssueStageAttribution.Attribute(events, DefaultStageOrder, DayEnd);

        Assert.Equal(IssueStageAttribution.Kind.None, result.Kind);
    }

    private static IssueStageAttribution.AttributionEvent NewEvent(
        string type,
        DateTimeOffset t,
        long id,
        string? stage = null,
        string? runId = null) =>
        new(type, t, id, stage, runId);
}
