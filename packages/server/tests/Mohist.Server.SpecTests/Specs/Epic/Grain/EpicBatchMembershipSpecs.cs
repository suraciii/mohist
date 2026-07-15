using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using System.Data;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

/// <summary>
/// Fake-based specs covering batch link / unlink with per-issue
/// outcomes, partial-failure semantics, de-duplication, idempotency,
/// the cross-epic active-membership invariant, the post-commit
/// event persistence inherited from T-001, and the issue-392 wake-up
/// behavior for done epics (open issue wakes, closed epic rejects).
/// </summary>
public class EpicBatchMembershipSpecs
{
    private const string ProjectId = "project_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_NewIssues_AllLinked()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);
        await SeedIssueAsync(database, issueId: "issue_c", issueNumber: 3);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
            new BatchMembershipRequestItem("3", "issue_c", 3),
        ], ProjectId);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("linked", o.Status));
        Assert.Equal(new[] { "1", "2", "3" }, outcomes.Select(o => o.Identifier).ToArray());

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .ToListAsync();
        Assert.Equal(3, links.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_SameInternalIdRequestedTwice_AreDeduplicatedToOneLink()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        // The HTTP layer resolves the two distinct identifier strings
        // ("1" and "issue_a") to the same issue. After dedup they
        // collapse to a single entry pointing to one link attempt — the
        // grain never sees the duplicate. Even if a duplicate did
        // slip through here, the grain's own internal-id dedup means
        // the issue would still be linked at most once.
        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("issue_a", "issue_a", 1),
        ], ProjectId);

        Assert.Single(outcomes);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_a");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_IssueInOtherNonTerminalEpic_ReportedAsConflict()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_first", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_second", status: "running", number: 2);
        await SeedIssueAsync(database, issueId: "issue_conflict", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_clean", issueNumber: 2);

        var firstGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_first");
        await firstGrain.LinkIssueAsync("issue_conflict", 1, ProjectId);

        var secondGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_second");
        var outcomes = await secondGrain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_conflict", 1),
            new BatchMembershipRequestItem("2", "issue_clean", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        var conflict = outcomes.Single(o => o.Identifier == "1");
        Assert.Equal("conflict", conflict.Status);
        Assert.Equal("epic_first", conflict.OwningEpicId);
        var clean = outcomes.Single(o => o.Identifier == "2");
        Assert.Equal("linked", clean.Status);

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_second")
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal("issue_clean", links[0].IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_AlreadyLinkedIssue_ReportedAsAlreadyLinked_NoDuplicate()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_a", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("already-linked", outcome.Status);

        await using var verify = database.CreateDbContext();
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_a");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_AllTerminalMemberships_ClaimedWithoutConflict()
    {
        // Per issue-392, linking to a `closed` epic is rejected outright,
        // so the "terminal-only" epic half of this scenario uses `done`.
        // The issue is also seeded as terminal so the wake-up branch in
        // LinkIssueAsync does not flip the done epic to running and
        // consume the active slot.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_terminal", status: "done", number: 1);
        await SeedEpicAsync(database, epicId: "epic_active", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_terminal_only", issueNumber: 1, status: IssueStatus.Done);

        var terminalGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_terminal");
        await terminalGrain.LinkIssueAsync("issue_terminal_only", 1, ProjectId);

        var activeGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active");
        var outcomes = await activeGrain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_terminal_only", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("linked", outcome.Status);

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.IssueId == "issue_terminal_only")
            .ToListAsync();
        Assert.Equal(2, links.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_TerminalTargetPreservesExistingActiveAffiliationSnapshot()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_active", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_done", status: "done", number: 2);
        await SeedIssueAsync(database, issueId: "issue_terminal", issueNumber: 1, status: IssueStatus.Done);

        var activeGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_active");
        await activeGrain.LinkIssueAsync("issue_terminal", 1, ProjectId);

        var doneGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_done");
        var outcomes = await doneGrain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_terminal", 1)], ProjectId);

        Assert.Equal("linked", Assert.Single(outcomes).Status);
        await using var verify = database.CreateDbContext();
        var issue = await verify.Issues.SingleAsync(row => row.IssueId == "issue_terminal");
        Assert.Equal("epic_active", issue.EpicId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssuesAsync_RemovesOnlyRequestedMembers_RemainingIntact()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);
        await SeedIssueAsync(database, issueId: "issue_c", issueNumber: 3);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);
        await grain.LinkIssueAsync("issue_b", 2, ProjectId);
        await grain.LinkIssueAsync("issue_c", 3, ProjectId);

        var outcomes = await grain.UnlinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("unlinked", o.Status));

        await using var verify = database.CreateDbContext();
        var remaining = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .ToListAsync();
        var remainingIds = remaining.Select(r => r.IssueId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "issue_c" }, remainingIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssuesAsync_NotMember_ReportedAsWasNotAMember()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);

        var outcomes = await grain.UnlinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal("unlinked", outcomes.First(o => o.Identifier == "1").Status);
        Assert.Equal("was-not-a-member", outcomes.First(o => o.Identifier == "2").Status);

        await using var verify = database.CreateDbContext();
        var remaining = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1" && l.IssueId == "issue_a")
            .ToListAsync();
        Assert.Empty(remaining);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_EmptyInput_ReturnsEmptyOutcomes()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var outcomes = await grain.LinkIssuesAsync(Array.Empty<BatchMembershipRequestItem>(), ProjectId);

        Assert.Empty(outcomes);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_OnTerminalEpic_RecordsIssueLinkedEvent()
    {
        // Per issue-392, linking to a `closed` epic is rejected outright
        // (EpicClosedCannotLinkException). To preserve the "terminal
        // epic accepts links without affecting status" test surface here,
        // we use a `done` target and a terminal issue (so the batch
        // path's per-item `targetIsTerminal` check (still pre-issue-392)
        // observes `done` as terminal and records the issue-linked event
        // without inserting an active-membership row). The batch path's
        // wake-up behaviour for `done` + open issue is the subject of T-002.
        var store = new RecordingEventStore();
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_t", status: "done", number: 1);
        await SeedIssueAsync(database, issueId: "issue_t", issueNumber: 1, status: IssueStatus.Done);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var grain = new EpicGrain(
            database.Factory,
            new NullGrainFactory(),
            time,
            store,
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:epic_t",
        };

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_t", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("linked", outcome.Status);

        var stored = await store.ListEpicEventsAsync("epic_t");
        var evt = Assert.Single(stored);
        Assert.Equal("com.mohist.epic.issue-linked", evt.Envelope.Type);
        Assert.Contains("issue_t", evt.Envelope.Data.ToString());
        Assert.Equal(time.GetUtcNow(), evt.Envelope.Time);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_WhenActiveMembershipInsertFails_RollsBackLinkAndEventAtomically()
    {
        // With atomic event persistence (recovery events appended into the
        // same DbContext transaction), a SaveChanges failure rolls back both
        // the membership row and the EpicIssueLinked event row. The conflict
        // outcome is still surfaced. The link is not committed.
        var store = new RecordingEventStore();
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: "idle", number: 1);
        await SeedEpicAsync(database, epicId: "epic_owner", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_race", issueNumber: 1);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var grain = new EpicGrain(
            database.CreateFactory(new InsertConflictingActiveIssueBeforeSaveInterceptor(ProjectId, "issue_race", "epic_owner", 1)),
            new NullGrainFactory(),
            time,
            store,
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = $"{ProjectId}:epic_target",
        };

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_race", 1)], ProjectId);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("conflict", outcome.Status);
        Assert.Equal("epic_owner", outcome.OwningEpicId);

        // The link row is not committed — the SaveChanges failure rolls back
        // the entire transaction (membership row + event row are atomic).
        await using var verify = database.CreateDbContext();
        var targetLinks = await verify.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == ProjectId && link.EpicId == "epic_target" && link.IssueId == "issue_race")
            .ToListAsync();
        Assert.Empty(targetLinks);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_DoneEpic_BatchWithOpenIssue_WakesToRunning_Atomically()
    {
        // Spec: 'Batch containing at least one open issue wakes a done
        // epic to running' — the wake-up is persisted atomically with the
        // successful link(s).
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Done, number: 1);
        await SeedIssueAsync(database, issueId: "issue_open", issueNumber: 1, status: IssueStatus.Backlog);
        await SeedIssueAsync(database, issueId: "issue_open_b", issueNumber: 2, status: IssueStatus.Backlog);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_target");

        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_open", 1),
            new BatchMembershipRequestItem("2", "issue_open_b", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("linked", o.Status));

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_target");
        Assert.Equal(EpicStatusName.Running, row.Status);
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == "epic_target")
            .ToListAsync();
        Assert.Equal(2, active.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_DoneEpic_BatchWithOnlyTerminalIssues_StaysDone_NoWake()
    {
        // Spec: 'Batch containing only terminal issues leaves a done epic
        // done' — no wake, no active-membership rows inserted.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Done, number: 1);
        await SeedIssueAsync(database, issueId: "issue_done", issueNumber: 1, status: IssueStatus.Done);
        await SeedIssueAsync(database, issueId: "issue_cancelled", issueNumber: 2, status: IssueStatus.Cancelled);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_target");

        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_done", 1),
            new BatchMembershipRequestItem("2", "issue_cancelled", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("linked", o.Status));

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_target");
        Assert.Equal(EpicStatusName.Done, row.Status);
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == "epic_target")
            .ToListAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_ClosedEpic_ThrowsEpicClosedCannotLinkException_NoRowsCreated()
    {
        // Spec: 'Batch link to a closed epic is rejected as a whole' —
        // the domain throws before the loop, no per-item outcomes are
        // produced and no link rows are created.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Closed, number: 1);
        await SeedIssueAsync(database, issueId: "issue_open", issueNumber: 1, status: IssueStatus.Backlog);
        await SeedIssueAsync(database, issueId: "issue_open_b", issueNumber: 2, status: IssueStatus.Backlog);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_target");

        var ex = await Assert.ThrowsAsync<EpicClosedCannotLinkException>(
            () => grain.LinkIssuesAsync(
            [
                new BatchMembershipRequestItem("1", "issue_open", 1),
                new BatchMembershipRequestItem("2", "issue_open_b", 2),
            ], ProjectId));
        Assert.Equal("epic_target", ex.EpicId);

        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_target")
            .ToListAsync());
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == "epic_target")
            .ToListAsync());
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_target");
        Assert.Equal(EpicStatusName.Closed, row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_DoneEpic_MixedOpenAndTerminalBatch_WakesOnce_OnlyFirstOpenLinkWakes()
    {
        // Acceptance: within one batch, wake fires at most once (on the
        // first open link); subsequent open links in the same batch do
        // not re-invoke WakeFromDone. The live row.Status observation
        // (refreshed by MapToRow after each commit) makes later items
        // take the normal non-terminal path.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Done, number: 1);
        await SeedIssueAsync(database, issueId: "issue_terminal_first", issueNumber: 1, status: IssueStatus.Done);
        await SeedIssueAsync(database, issueId: "issue_open_first", issueNumber: 2, status: IssueStatus.Backlog);
        await SeedIssueAsync(database, issueId: "issue_open_second", issueNumber: 3, status: IssueStatus.Backlog);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_target");

        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_terminal_first", 1),
            new BatchMembershipRequestItem("2", "issue_open_first", 2),
            new BatchMembershipRequestItem("3", "issue_open_second", 3),
        ], ProjectId);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal("linked", o.Status));

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_target");
        Assert.Equal(EpicStatusName.Running, row.Status);
        // Both open issues got active rows; the terminal-issue link did not.
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == "epic_target")
            .OrderBy(a => a.IssueNumber)
            .ToListAsync();
        Assert.Equal(2, active.Count);
        Assert.Equal(new[] { 2, 3 }, active.Select(a => a.IssueNumber).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_DoneEpic_BatchWake_PartialFailureLeavesEpicRunning()
    {
        // Design risk: per-item persistence means a batch to a `done`
        // epic that wakes on item 1 then fails on item 3 leaves the
        // epic `running` with items 2 linked and 3 unlinked. This is
        // the correct outcome — the epic *does* have open work; the
        // failed item returns a conflict outcome and the caller can
        // retry just that item.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Done, number: 1);
        await SeedEpicAsync(database, epicId: "epic_owner", status: EpicStatusName.Running, number: 2);
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1, status: IssueStatus.Backlog);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2, status: IssueStatus.Backlog);
        // issue_b is already actively owned by epic_owner — the second
        // batch item will hit the cross-aggregate ownership invariant.
        await using (var seed = database.CreateDbContext())
        {
            seed.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = ProjectId,
                IssueId = "issue_b",
                EpicId = "epic_owner",
                IssueNumber = 2,
            });
            await seed.SaveChangesAsync();
        }

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_target");

        var outcomes = await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_a", 1),
            new BatchMembershipRequestItem("2", "issue_b", 2),
        ], ProjectId);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal("linked", outcomes[0].Status);
        Assert.Equal("conflict", outcomes[1].Status);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_target");
        Assert.Equal(EpicStatusName.Running, row.Status);
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_target")
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal("issue_a", links[0].IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_DoneEpic_BatchWake_AutopilotStartsNewlyLinkedOpenIssue()
    {
        // Spec: 'Autopilot advances the newly linked open issue after
        // wake-up' applied to the batch path: TryStartNextAsync fires
        // once after the loop when the epic ended `running` and was
        // `done` at entry.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Done, number: 1);
        await SeedIssueAsync(database, issueId: "issue_open", issueNumber: 1, status: IssueStatus.Backlog, canStart: true);
        await SeedIssueAsync(database, issueId: "issue_open_b", issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_target");

        await grain.LinkIssuesAsync(
        [
            new BatchMembershipRequestItem("1", "issue_open", 1),
            new BatchMembershipRequestItem("2", "issue_open_b", 2),
        ], ProjectId);

        // Both issues are open + startable + non-in-progress; the
        // batch tail-calls TryStartNextAsync exactly once and selects
        // the earliest one.
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("issue_open", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_RunningEpic_BatchWithStartableIssue_AdvancesNewlyLinkedIssue()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Running, number: 1);
        await SeedIssueAsync(database, issueId: "issue_open", issueNumber: 1, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:epic_target");

        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("1", "issue_open", 1)], ProjectId);

        Assert.Equal("linked", Assert.Single(outcomes).Status);
        Assert.Equal(["issue_open"], grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssuesAsync_IdleEpic_BatchWithOnlyTerminalIssues_MarksDoneAndReleasesActiveMemberships()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: EpicStatusName.Idle, number: 1);
        await SeedIssueAsync(database, issueId: "issue_existing", issueNumber: 1, status: IssueStatus.Done);
        await SeedIssueAsync(database, issueId: "issue_cancelled", issueNumber: 2, status: IssueStatus.Cancelled);
        await using (var seed = database.CreateDbContext())
        {
            seed.EpicIssues.Add(new EpicIssueRow
            {
                ProjectId = ProjectId,
                EpicId = "epic_target",
                IssueId = "issue_existing",
                IssueNumber = 1,
                CreatedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
            });
            seed.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = ProjectId,
                EpicId = "epic_target",
                IssueId = "issue_existing",
                IssueNumber = 1,
            });
            await seed.SaveChangesAsync();
        }

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_target");
        var outcomes = await grain.LinkIssuesAsync(
            [new BatchMembershipRequestItem("2", "issue_cancelled", 2)], ProjectId);

        Assert.Equal("linked", Assert.Single(outcomes).Status);
        await using var verify = database.CreateDbContext();
        var epic = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_target");
        Assert.Equal(EpicStatusName.Done, epic.Status);
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
            .Where(row => row.ProjectId == ProjectId && row.EpicId == "epic_target")
            .ToListAsync());
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
        IssueStatus status = IssueStatus.Backlog,
        bool canStart = true)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            Priority = "p2",
            IsDraft = !canStart,
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

        public TestDbContextFactory CreateFactory(params IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_connection);
            if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
            return new TestDbContextFactory(builder.Options);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class InsertConflictingActiveIssueBeforeSaveInterceptor(
        string projectId,
        string issueId,
        string ownerEpicId,
        int issueNumber) : SaveChangesInterceptor
    {
        private bool _inserted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_inserted || eventData.Context is not MohistDbContext db)
                return result;

            var claimsTargetIssue = db.ChangeTracker.Entries<EpicActiveIssueRow>()
                .Any(entry => entry.State == EntityState.Added
                    && entry.Entity.ProjectId == projectId
                    && entry.Entity.IssueId == issueId
                    && entry.Entity.EpicId != ownerEpicId);
            if (!claimsTargetIssue)
                return result;

            _inserted = true;
            var connection = db.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
                    VALUES ($projectId, $issueId, $epicId, $issueNumber, $createdAt)
                    """;
                AddParameter(command, "$projectId", projectId);
                AddParameter(command, "$issueId", issueId);
                AddParameter(command, "$epicId", ownerEpicId);
                AddParameter(command, "$issueNumber", issueNumber);
                AddParameter(command, "$createdAt", new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                if (shouldClose) await connection.CloseAsync();
            }

            return result;
        }

        private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

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
    /// Test double for <see cref="IGrainFactory"/> that records every
    /// <c>IIssueGrain.StartWorkAsync</c> invocation, so batch wake-up
    /// tests can assert the post-loop autopilot tail-call fires exactly
    /// once when the batch woke a done epic.
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

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
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
        public Task SetEpicAffiliationAsync(string? epicId) => throw new NotSupportedException();
    }
}
