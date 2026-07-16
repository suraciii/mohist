using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Orleans;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

/// <summary>
/// Fake-based specs covering issue-179: non-destructive close and
/// status-aware membership uniqueness. Exercises the
/// <c>EpicGrain.LinkIssueAsync</c> / <c>UnlinkIssueAsync</c> /
/// <c>SetStatusAsync("closed")</c> paths and the <c>EpicQuerier</c>
/// detail/list reads against an in-memory SQLite seeded via
/// <c>TestDbContextFactory</c> (no real database, no real Orleans).
/// </summary>
public class EpicMembershipSpecs
{
    private const string ProjectId = "project_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_NewIssue_AddsEpicIssueRow()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var link = await verify.EpicIssues.AsNoTracking()
            .SingleAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1");
        Assert.Equal("issue_1", link.IssueId);
        Assert.Equal(1, link.IssueNumber);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_SameEpicTwice_IsIdempotentAndDoesNotCreateDuplicate()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_1");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_SameEpicAfterClose_IsIdempotentAndKeepsMembership()
    {
        // Idempotency must also work when the epic is already terminal.
        // The link remains as a single row.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);
        await grain.SetStatusAsync("closed");

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_1");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_IssueInTerminalEpic_CanLinkToNewNonTerminalEpic_AndKeepsTerminalMembership()
    {
        // Issue-179: terminal-epic memberships do NOT block re-homing
        // an issue into a new non-terminal epic; the terminal row stays
        // so the membership history is preserved. Per issue-392 the
        // terminal epic is `done` (linking to `closed` is rejected
        // outright) and the linked issue must be terminal so the
        // wake-up branch does not flip the done epic to running and
        // consume the active-membership slot before the active epic
        // can re-home the issue.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_terminal", status: "done", number: 1);
        await SeedEpicAsync(database, epicId: "epic_active", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Done);

        var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_terminal");
        await terminalGrain.LinkIssueAsync("issue_1", 1, ProjectId);
        // Capture the terminal membership row directly so we can prove
        // it survives the re-link.
        await using (var verify = database.CreateDbContext())
        {
            var before = await verify.EpicIssues.AsNoTracking()
                .SingleAsync(l => l.EpicId == "epic_terminal" && l.IssueId == "issue_1");
            Assert.Equal("epic_terminal", before.EpicId);
        }

        var activeGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active");
        await activeGrain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verifyAfter = database.CreateDbContext();
        var terminalRow = await verifyAfter.EpicIssues.AsNoTracking()
            .SingleAsync(l => l.EpicId == "epic_terminal" && l.IssueId == "issue_1");
        Assert.Equal("epic_terminal", terminalRow.EpicId);
        var activeRow = await verifyAfter.EpicIssues.AsNoTracking()
            .SingleAsync(l => l.EpicId == "epic_active" && l.IssueId == "issue_1");
        Assert.Equal("epic_active", activeRow.EpicId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_IssueInTerminalEpic_CanLinkToRunningOrPausedEpic()
    {
        // Both "running" and "paused" are non-terminal; the terminal-only
        // exception must apply to each. Per issue-392, a `done` epic
        // holding a non-terminal issue wakes itself to running and
        // therefore consumes the active-membership slot — a second
        // non-terminal epic CANNOT claim the same issue. To still
        // exercise the "re-home from terminal to running/paused" path
        // here, the linked issue must be terminal (no wake, so the
        // done epic stays `done` and does not consume an active slot).
        await RunReHomeFromTerminalAsync("running");
        await RunReHomeFromTerminalAsync("paused");

        async Task RunReHomeFromTerminalAsync(string targetStatus)
        {
            var database = CreateDatabase();
            await SeedEpicAsync(database, epicId: "epic_done", status: "done", number: 1);
            await SeedEpicAsync(database, epicId: $"epic_{targetStatus}", status: targetStatus, number: 2);
            await SeedIssueAsync(database, issueId: $"issue_{targetStatus}", issueNumber: 1, status: IssueStatus.Done);

            var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_done");
            await terminalGrain.LinkIssueAsync($"issue_{targetStatus}", 1, ProjectId);

            var activeGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_{targetStatus}");
            await activeGrain.LinkIssueAsync($"issue_{targetStatus}", 1, ProjectId);

            await using var verify = database.CreateDbContext();
            var rows = await verify.EpicIssues.AsNoTracking()
                .Where(l => l.IssueId == $"issue_{targetStatus}")
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.EpicId == "epic_done");
            Assert.Contains(rows, r => r.EpicId == $"epic_{targetStatus}");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_IssueInTerminalEpic_LinkToAnotherTerminalEpic_AlsoAllowed()
    {
        // Sanity: linking into a second terminal epic is also fine —
        // there is no invariant on terminal memberships. Per
        // issue-392, however, two outcomes now restrict this scenario:
        //   1) linking to a `closed` epic is rejected outright, so the
        //      closed-link half is exercised separately (see
        //      EpicMembershipSpecs and EpicLifecycleSpecs);
        //   2) linking an open issue to a `done` epic wakes that epic
        //      to running, which consumes the active-membership slot,
        //      so a SECOND `done` epic cannot link the same open issue
        //      (it would be rejected by the active-membership invariant).
        // To exercise the "terminal-link to two terminal epics" path
        // without triggering either outcome, both epics must be `done`
        // and the issue must already be terminal (no wake).
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_done_1", status: "done", number: 1);
        await SeedEpicAsync(database, epicId: "epic_done_2", status: "done", number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Done);

        var grainDone1 = CreateGrain(database.Factory, $"{ProjectId}:epic_done_1");
        var grainDone2 = CreateGrain(database.Factory, $"{ProjectId}:epic_done_2");

        await grainDone1.LinkIssueAsync("issue_1", 1, ProjectId);
        await grainDone2.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var rows = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.IssueId == "issue_1")
            .OrderBy(r => r.EpicId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "epic_done_1", "epic_done_2" }, rows.Select(r => r.EpicId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_SecondNonTerminalMembership_ThrowsDuplicate()
    {
        // The non-terminal uniqueness invariant: an issue may belong to
        // at most one non-terminal epic. The duplicate check raises
        // InvalidOperationException with the existing-epic id in the
        // message so the HTTP layer can map it to DUPLICATE_EPIC_MEMBERSHIP.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_first", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_second", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);

        var firstGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_first");
        var secondGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_second");

        await firstGrain.LinkIssueAsync("issue_1", 1, ProjectId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondGrain.LinkIssueAsync("issue_1", 1, ProjectId));
        Assert.Contains("epic_first", ex.Message);

        await using var verify = database.CreateDbContext();
        var rows = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.IssueId == "issue_1")
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("epic_first", rows[0].EpicId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_TerminalPlusSecondNonTerminal_KeepsTerminalRowAndAllowsSequelAfterAutoDone()
    {
        // Linking a terminal issue (Done) to a non-terminal epic (idle
        // or running) auto-marks the target epic done via the
        // link-time recompute introduced in #363. After the auto-done
        // transition the active ownership is released, so a subsequent
        // link to another non-terminal epic succeeds. The terminal
        // membership on the `done` epic is preserved untouched as part
        // of the membership history.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_terminal", status: "done", number: 1);
        await SeedEpicAsync(database, epicId: "epic_active_a", status: "idle", number: 2);
        await SeedEpicAsync(database, epicId: "epic_active_b", status: "running", number: 3);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Done);

        var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_terminal");
        var activeAGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active_a");
        var activeBGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active_b");

        await terminalGrain.LinkIssueAsync("issue_1", 1, ProjectId);
        await activeAGrain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using (var verifyAfterAutoDone = database.CreateDbContext())
        {
            var activeARow = await verifyAfterAutoDone.Epics.AsNoTracking()
                .SingleAsync(e => e.Id == "epic_active_a");
            Assert.Equal("done", activeARow.Status);
        }

        await activeBGrain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var rows = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.IssueId == "issue_1")
            .OrderBy(r => r.EpicId)
            .ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal("epic_active_a", rows[0].EpicId);
        Assert.Equal("epic_active_b", rows[1].EpicId);
        Assert.Equal("epic_terminal", rows[2].EpicId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_RunningEpic_StartableIssueAdvancesViaLinkTimeRecompute()
    {
        // Spec D5 / acceptance: a startable issue linked to a running
        // epic with a free in-progress slot SHALL be advanced via
        // TryStartNext as part of the link-time recompute. This
        // preserves the readiness behavior previously supplied by the
        // poll-driven sweep, deleted in #363.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, issueId: "issue_done", issueNumber: 1, status: IssueStatus.Done);
        await SeedIssueAsync(database, issueId: "issue_open", issueNumber: 2, status: IssueStatus.Backlog);
        await SeedLinkAsync(database, "epic_1", "issue_done", 1);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_1");

        await grain.LinkIssueAsync("issue_open", 2, ProjectId);

        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("issue_open", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_IdleEpic_AllCompleteMembershipAtLinkTime_AutoMarksDone()
    {
        // Spec D5 / acceptance: an idle epic whose members are all
        // complete at link time SHALL be marked done via the link-time
        // recompute. This preserves the all-complete-idle behavior
        // previously supplied by the poll-driven sweep.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_done_a", issueNumber: 1, status: IssueStatus.Done);
        await SeedIssueAsync(database, issueId: "issue_done_b", issueNumber: 2, status: IssueStatus.Done);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_1");

        await grain.LinkIssueAsync("issue_done_a", 1, ProjectId);
        await grain.LinkIssueAsync("issue_done_b", 2, ProjectId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_1");
        Assert.Equal("done", row.Status);
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.EpicId == "epic_1")
            .ToListAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ActiveMembershipSlot_PreventsTwoNonTerminalOwners_WhenPrechecksRace()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_active_a", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_active_b", status: "running", number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);

        await using (var first = database.CreateDbContext())
        {
            first.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = ProjectId,
                IssueId = "issue_1",
                EpicId = "epic_active_a",
                IssueNumber = 1,
            });
            await first.SaveChangesAsync();
        }

        await using var second = database.CreateDbContext();
        second.EpicActiveIssues.Add(new EpicActiveIssueRow
        {
            ProjectId = ProjectId,
            IssueId = "issue_1",
            EpicId = "epic_active_b",
            IssueNumber = 1,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssueAsync_RemovesOnlyThatMembership_AndLeavesOthersIntact()
    {
        // Even after the unique-index relaxation, UnlinkIssueAsync must
        // still remove exactly one link and leave other memberships
        // (including terminal-epic ones) untouched. Per issue-392 the
        // terminal epic is `done` (linking to `closed` is rejected
        // outright) and the linked issue must be terminal so the
        // wake-up branch does not flip the done epic to running and
        // consume the active-membership slot.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_terminal", status: "done", number: 1);
        await SeedEpicAsync(database, epicId: "epic_active", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Done);

        var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_terminal");
        var activeGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active");
        await terminalGrain.LinkIssueAsync("issue_1", 1, ProjectId);
        await activeGrain.LinkIssueAsync("issue_1", 1, ProjectId);

        await activeGrain.UnlinkIssueAsync("issue_1", ProjectId);

        await using var verify = database.CreateDbContext();
        var remaining = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.IssueId == "issue_1")
            .ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("epic_terminal", remaining[0].EpicId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssueAsync_OnMultiMemberEpic_RemovesOnlyTheSpecifiedMembership()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);
        await grain.LinkIssueAsync("issue_b", 2, ProjectId);

        await grain.UnlinkIssueAsync("issue_a", ProjectId);

        await using var verify = database.CreateDbContext();
        var remaining = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("issue_b", remaining[0].IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task SetStatusAsync_Closed_PreservesEpicIssueRows()
    {
        // Issue-179 / design D1: closing an epic is non-destructive.
        // The EpicIssueRow set is unchanged; EpicQuerier (which is
        // status-agnostic in its read-model) continues to surface the
        // membership history. The HTTP-driven EpicLifecycleSpecs covers
        // the full EpicQuerier.GetAsync round-trip.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_2", issueNumber: 2);
        await SeedLinkAsync(database, "epic_1", "issue_1", 1);
        await SeedLinkAsync(database, "epic_1", "issue_2", 2);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var dto = await grain.SetStatusAsync("closed");

        Assert.Equal("closed", dto.Status);

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .OrderBy(l => l.IssueId)
            .ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(new[] { "issue_1", "issue_2" }, links.Select(l => l.IssueId));
        // The EpicQuerier.ListAsync single SQL join reads EpicIssues
        // directly. Verifying the rows are still here proves the read
        // model will surface them.
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task SetStatusAsync_Closed_ReleasesActiveMembershipSlot_ButKeepsHistoryRows()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await grain.SetStatusAsync("closed");

        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking().ToListAsync());
        Assert.Single(await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_1")
            .ToListAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task SetStatusAsync_Done_PreservesEpicIssueRows()
    {
        // Symmetric guarantee for the Done transition (which was always
        // non-destructive but its assert didn't exist).
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Done);
        await SeedLinkAsync(database, "epic_1", "issue_1", 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var dto = await grain.SetStatusAsync("done");

        Assert.Equal("done", dto.Status);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_1");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_IncludesClosedEpicWithRetainedMembers()
    {
        // The EpicQuerier.ListAsync read-model uses a single SQL
        // join (LEFT JOIN EpicIssues/Issues). Closing an epic must
        // surface it in the listing along with its preserved
        // members; before this change, the list would have shown
        // the closed epic with zero members.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "closed");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_2", issueNumber: 2);
        await SeedLinkAsync(database, "epic_1", "issue_1", 1);
        await SeedLinkAsync(database, "epic_1", "issue_2", 2);

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync(ProjectId);

        var closed = Assert.Single(result);
        Assert.Equal("closed", closed.Status);
        Assert.Equal(2, closed.Progress.TotalIssueCount);
        Assert.Equal(0, closed.Progress.DeliveredCount);
        Assert.False(closed.Progress.ReadyToMarkDone);
    }

    private static EpicGrain CreateGrain(TestDbContextFactory factory, string grainKey) =>
        new(
            factory,
            new NullGrainFactory(),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            new NoopEventStore(),
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = grainKey,
        };

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string epicId = "epic_1",
        int number = 1,
        string status = "idle",
        string? pauseReason = null)
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {epicId}",
            Description = "",
            Priority = "p2",
            Status = status,
            PauseReason = pauseReason,
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string issueId = "issue_1",
        int issueNumber = 1,
        IssueStatus status = IssueStatus.Backlog)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            Priority = "p2",
            IsDraft = false,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLinkAsync(TestDatabase database, string epicId, string issueId, int issueNumber, string projectId = ProjectId)
    {
        await using var db = database.CreateDbContext();
        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = projectId,
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    /// <summary>
    /// Minimal stand-in for <see cref="IssueQuerier"/> so we can construct
    /// <see cref="EpicQuerier"/> without dragging the full DI graph into
    /// unit specs. The ListAsync path used here is the raw-SQL path that
    /// does NOT call into IssueQuerier, so a throwing stub is sufficient.
    /// </summary>
    private sealed class ThrowingIssueQuerier : IssueQuerier
    {
        public ThrowingIssueQuerier() : base(null!, null!, null!, null!, null!, null!) { }

        public new Task<List<IssueReadModel>> ListAsync(
            string projectId,
            Mohist.Server.Project.Services.ProjectInfo? project = null,
            string? stage = null,
            string? label = null,
            string? priority = null,
            bool? archived = null,
            bool? all = null) =>
            throw new InvalidOperationException(
                "IssueQuerier.ListAsync should not be invoked on the EpicQuerier.ListAsync path.");
    }

    /// <summary>
    /// No-op Orleans grain factory: the membership paths under test
    /// (<c>LinkIssueAsync</c> / <c>UnlinkIssueAsync</c> / closed
    /// <c>SetStatusAsync</c>) never touch issue grains, so we don't
    /// need to record or simulate any inter-grain calls.
    /// </summary>
    private sealed class NullGrainFactory : IGrainFactory
    {
        public IEpicGrain GetEpicGrain(string grainKey) => throw new NotSupportedException();
        public IIssueGrain GetIssueGrain(string issueId) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    /// <summary>
    /// Recording Orleans grain factory for the link-time recompute
    /// specs: captures every <c>IIssueGrain.StartWorkAsync</c> so the
    /// recompute path's autopilot advance can be observed without the
    /// full workflow runtime.
    /// </summary>
    private sealed class RecordingGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        public List<string> IssueStartCalls { get; } = [];

        public RecordingGrainFactory(IDbContextFactory<MohistDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public IEpicGrain GetEpicGrain(string grainKey) =>
            new EpicGrain(
                _dbFactory,
                this,
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)),
                new NoopEventStore(),
                NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };

        public IIssueGrain GetIssueGrain(string issueId) => new RecordingIssueGrain(this, issueId);

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IEpicGrain))
                return (TGrainInterface)(object)GetEpicGrain(primaryKey);
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)GetIssueGrain(primaryKey);
            throw new NotSupportedException(typeof(TGrainInterface).FullName);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IEpicGrain))
                return GetEpicGrain(grainPrimaryKey);
            if (grainInterfaceType == typeof(IIssueGrain))
                return GetIssueGrain(grainPrimaryKey);
            throw new NotSupportedException(grainInterfaceType.FullName);
        }
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    private sealed class RecordingIssueGrain : IIssueGrain
    {
        private readonly RecordingGrainFactory _owner;
        public RecordingIssueGrain(RecordingGrainFactory owner, string issueId)
        {
            _owner = owner;
            IssueId = issueId;
        }

        public string IssueId { get; }

        public Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null)
            => throw new NotSupportedException();
        public async Task<string> StartWorkAsync(Mohist.Server.Issue.Grains.WorkflowProjectContext? project = null)
        {
            _owner.IssueStartCalls.Add(IssueId);
            return "wr_test";
        }
        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(Mohist.Server.Issue.Grains.UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Grains.IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Grains.IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Services.IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Grains.IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
    }
}
