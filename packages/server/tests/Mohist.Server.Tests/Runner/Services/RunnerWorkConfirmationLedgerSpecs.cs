using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Xunit;

namespace Mohist.Server.Tests.AgentOps;

/// <summary>
/// The per-work execution confirmation ledger: only a poll that names a work key
/// renews that work's confirmation, the confirmation keeps its own source time,
/// and a confirmation never outlives the evidence window by more than the
/// retention that keeps a stale time reportable.
/// </summary>
[Trait("level", "L0")]
public sealed class RunnerWorkConfirmationLedgerSpecs
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Generation = "generation-1";

    [Fact]
    public void Renew_NamesWorkAtThePollTime()
    {
        var confirmations = RunnerWorkConfirmationLedger.Renew(
            previous: null,
            reportedWorkKeys: ["workflow:run-1:work-1"],
            Generation,
            Now);

        var confirmation = Assert.Single(confirmations);
        Assert.Equal("workflow:run-1:work-1", confirmation.WorkKey);
        Assert.Equal(Now, confirmation.ConfirmedAt);
        Assert.Equal(Generation, confirmation.ProcessGeneration);
    }

    [Fact]
    public void Renew_KeepsUnreportedWorkAtItsOriginalTime()
    {
        var previous = new[]
        {
            new RunnerWorkConfirmation("workflow:run-1:work-1", Now.AddMinutes(-4), Generation),
        };

        var confirmations = RunnerWorkConfirmationLedger.Renew(
            previous,
            reportedWorkKeys: ["workflow:run-1:work-2"],
            Generation,
            Now);

        Assert.Equal(2, confirmations.Count);
        var unchanged = Assert.Single(confirmations, entry => entry.WorkKey == "workflow:run-1:work-1");
        Assert.Equal(Now.AddMinutes(-4), unchanged.ConfirmedAt);
        var renewed = Assert.Single(confirmations, entry => entry.WorkKey == "workflow:run-1:work-2");
        Assert.Equal(Now, renewed.ConfirmedAt);
    }

    [Fact]
    public void Renew_DoesNotDuplicateAKeyReportedInBothSets()
    {
        var previous = new[]
        {
            new RunnerWorkConfirmation("workflow:run-1:work-1", Now.AddMinutes(-4), Generation),
        };

        var confirmations = RunnerWorkConfirmationLedger.Renew(
            previous,
            reportedWorkKeys: ["workflow:run-1:work-1"],
            Generation,
            Now);

        var confirmation = Assert.Single(confirmations);
        Assert.Equal(Now, confirmation.ConfirmedAt);
    }

    [Fact]
    public void Renew_DropsUnreportedWorkBeyondRetention()
    {
        var previous = new[]
        {
            new RunnerWorkConfirmation(
                "workflow:run-1:work-1",
                Now - RunnerWorkConfirmationLedger.Retention - TimeSpan.FromSeconds(1),
                Generation),
        };

        var confirmations = RunnerWorkConfirmationLedger.Renew(
            previous,
            reportedWorkKeys: [],
            Generation,
            Now);

        Assert.Empty(confirmations);
    }

    [Fact]
    public void Renew_IgnoresBlankAndDuplicateReportedKeys()
    {
        var confirmations = RunnerWorkConfirmationLedger.Renew(
            previous: null,
            reportedWorkKeys: ["", "  ", "workflow:run-1:work-1", "workflow:run-1:work-1"],
            Generation,
            Now);

        Assert.Single(confirmations);
    }

    [Fact]
    public void Stamp_MatchesTheOwnerLedgerRowByWorkKey()
    {
        var works = new[]
        {
            Work("work-1", "run-1"),
            Work("work-2", "run-1"),
        };
        var confirmations = new[]
        {
            new RunnerWorkConfirmation("workflow:run-1:work-1", Now.AddSeconds(-30), Generation),
        };

        var stamped = RunnerWorkConfirmationLedger.Stamp(works, confirmations, Generation);

        Assert.Equal(Now.AddSeconds(-30), stamped[0].ConfirmedAt);
        Assert.Null(stamped[1].ConfirmedAt);
    }

    [Fact]
    public void Stamp_IgnoresConfirmationsFromAnotherProcess()
    {
        var works = new[] { Work("work-1", "run-1") };
        var confirmations = new[]
        {
            new RunnerWorkConfirmation("workflow:run-1:work-1", Now.AddSeconds(-30), "generation-0"),
        };

        var stamped = RunnerWorkConfirmationLedger.Stamp(works, confirmations, Generation);

        Assert.Null(Assert.Single(stamped).ConfirmedAt);
    }

    [Fact]
    public void Stamp_ReturnsTheSameItemsWhenNoConfirmationApplies()
    {
        var works = new[] { Work("work-1", "run-1") };

        var stamped = RunnerWorkConfirmationLedger.Stamp(works, confirmations: null, Generation);

        Assert.Same(works, stamped);
    }

    private static RunnerActiveWorkItem Work(string workId, string runId) =>
        new(
            workId,
            WorkDispatchOwnerKinds.Workflow,
            runId,
            "task",
            Stage: "build",
            Title: "Task",
            ProcessGeneration: Generation);
}
