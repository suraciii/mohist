using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using IssueEntity = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

/// <summary>
/// Fake-based specs covering issue-179 / T-002: <c>primaryEpic</c>
/// projection reflects the issue's NON-TERMINAL epic membership.
/// <c>MohistDbFixture</c> fakes every external system (in-memory SQLite,
/// no Orleans silo, no HTTP) so these run as plain EF-backed unit-style
/// service tests.
/// </summary>
[Collection("MohistDb")]
public class IssueQuerierPrimaryEpicSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierPrimaryEpicSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Theory]
    [InlineData("idle")]
    [InlineData("running")]
    [InlineData("paused")]
    public async Task PrimaryEpic_IssueInNonTerminalEpic_ProjectsThatEpic(string epicStatus)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"proj-prim-{suffix}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = projectId, Name = $"Project {epicStatus}" };
        var issue = SeedIssue(db, projectId, $"issue_active_{suffix}", number: 1);
        var epic = SeedEpic(db, projectId, $"epic_active_{suffix}", number: 1, status: epicStatus);
        SeedEpicLink(db, projectId, epic.Id, issue.Id, issue.Number);

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list, i => i.Id == issue.Id);
        Assert.NotNull(item.PrimaryEpic);
        Assert.Equal(epic.Id, item.PrimaryEpic!.Id);
        Assert.Equal(epic.Number, item.PrimaryEpic.Number);
        Assert.Equal(epic.Title, item.PrimaryEpic.Title);
        Assert.Equal(epicStatus, item.PrimaryEpic.Status);
        Assert.Equal(epic.Priority, item.PrimaryEpic.Priority);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PrimaryEpic_IssueOnlyInTerminalEpics_ProjectsNull()
    {
        // After T-001, an issue whose memberships are all terminal
        // (done/closed) must project a null primaryEpic. The terminal
        // link rows are preserved (membership history) but do not
        // surface as the issue's active epic.
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"proj-prim-terminal-{suffix}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = projectId, Name = "Project terminal" };
        var issue = SeedIssue(db, projectId, $"issue_terminal_only_{suffix}", number: 1);
        SeedEpic(db, projectId, $"epic_done_{suffix}", number: 1, status: "done");
        SeedEpic(db, projectId, $"epic_closed_{suffix}", number: 2, status: "closed");
        SeedEpicLink(db, projectId, $"epic_done_{suffix}", issue.Id, issue.Number);
        SeedEpicLink(db, projectId, $"epic_closed_{suffix}", issue.Id, issue.Number);

        // Sanity: the terminal memberships are present in the table
        // (T-001 made close non-destructive, so the closed-epic row
        // survives).
        var linkCount = await db.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == projectId && l.IssueId == issue.Id);
        Assert.Equal(2, linkCount);

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list, i => i.Id == issue.Id);
        Assert.Null(item.PrimaryEpic);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Theory]
    [InlineData("done")]
    [InlineData("closed")]
    public async Task PrimaryEpic_IssueInSingleTerminalEpic_ProjectsNull(string epicStatus)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"proj-prim-single-{suffix}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = projectId, Name = $"Project single-{epicStatus}" };
        var issue = SeedIssue(db, projectId, $"issue_single_{suffix}", number: 1);
        SeedEpic(db, projectId, $"epic_single_{suffix}", number: 1, status: epicStatus);
        SeedEpicLink(db, projectId, $"epic_single_{suffix}", issue.Id, issue.Number);

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list, i => i.Id == issue.Id);
        Assert.Null(item.PrimaryEpic);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PrimaryEpic_RehomedFromTerminalToNewNonTerminalEpic_FollowsNewEpic()
    {
        // The re-homing scenario from the spec: an issue is first
        // linked to a terminal epic (whose row is retained, not
        // destroyed); then it is linked to a non-terminal epic. The
        // primaryEpic projection must reference the new non-terminal
        // epic and must NOT reference the terminal epic.
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"proj-prim-rehome-{suffix}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = projectId, Name = "Project rehome" };
        var issue = SeedIssue(db, projectId, $"issue_rehome_{suffix}", number: 1);
        var terminalEpic = SeedEpic(db, projectId, $"epic_terminal_rehome_{suffix}", number: 1, status: "closed");
        var activeEpic = SeedEpic(db, projectId, $"epic_active_rehome_{suffix}", number: 2, status: "idle");
        SeedEpicLink(db, projectId, terminalEpic.Id, issue.Id, issue.Number);
        SeedEpicLink(db, projectId, activeEpic.Id, issue.Id, issue.Number);

        // Sanity: both link rows are present (T-001 keeps terminal
        // memberships intact on re-homing).
        var links = await db.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == projectId && l.IssueId == issue.Id)
            .ToListAsync();
        Assert.Equal(2, links.Count);

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list, i => i.Id == issue.Id);
        Assert.NotNull(item.PrimaryEpic);
        Assert.Equal(activeEpic.Id, item.PrimaryEpic!.Id);
        Assert.Equal(activeEpic.Number, item.PrimaryEpic.Number);
        Assert.NotEqual(terminalEpic.Id, item.PrimaryEpic.Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PrimaryEpic_TerminalMembershipBeforeRehome_SeenAsNullIfActiveLinkIsRemoved()
    {
        // Once the active non-terminal link is unlinked (the explicit
        // unlink operation), the projection must revert to null since
        // only the terminal membership remains. Asserts the projection
        // reacts to the membership set as a whole, not a cached value.
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"proj-prim-unlinked-{suffix}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = projectId, Name = "Project unlinked" };
        var issue = SeedIssue(db, projectId, $"issue_unlinked_{suffix}", number: 1);
        var terminalEpic = SeedEpic(db, projectId, $"epic_terminal_unlinked_{suffix}", number: 1, status: "done");
        var activeEpic = SeedEpic(db, projectId, $"epic_active_unlinked_{suffix}", number: 2, status: "idle");
        SeedEpicLink(db, projectId, terminalEpic.Id, issue.Id, issue.Number);
        SeedEpicLink(db, projectId, activeEpic.Id, issue.Id, issue.Number);

        // Unlink the active link on a fresh context to avoid the
        // tracked-entity collision from the seeding path.
        using (var writeScope = _fixture.Services.CreateScope())
        {
            var writeDb = writeScope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var toRemove = await writeDb.EpicIssues
                .SingleAsync(l => l.EpicId == activeEpic.Id && l.IssueId == issue.Id);
            writeDb.EpicIssues.Remove(toRemove);
            await writeDb.SaveChangesAsync();
        }

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list, i => i.Id == issue.Id);
        Assert.Null(item.PrimaryEpic);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task PrimaryEpic_IssueWithoutAnyEpicMembership_StaysNull()
    {
        // Guard against regression: an issue with no epic link must
        // still project null primaryEpic (the filter must not invent
        // a value when the epicLinks set is empty).
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"proj-prim-none-{suffix}";
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = projectId, Name = "Project noepic" };
        SeedIssue(db, projectId, $"issue_noepic_{suffix}", number: 1);

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var list = await service.ListAsync(project.Id, project);

        var item = Assert.Single(list);
        Assert.Null(item.PrimaryEpic);
    }

    private static IssueEntity SeedIssue(
        MohistDbContext db,
        string projectId,
        string issueId,
        int number)
    {
        var issue = new IssueEntity
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {issueId}",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = IssueStatus.Backlog,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        db.SaveChanges();
        return issue;
    }

    private static EpicRow SeedEpic(
        MohistDbContext db,
        string projectId,
        string epicId,
        int number,
        string status)
    {
        var row = new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {epicId}",
            Description = "",
            Priority = "p1",
            Status = status,
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        };
        db.Epics.Add(row);
        db.SaveChanges();
        return row;
    }

    private static void SeedEpicLink(
        MohistDbContext db,
        string projectId,
        string epicId,
        string issueId,
        int issueNumber)
    {
        db.EpicIssues.Add(new EpicIssueRow
        {
            ProjectId = projectId,
            EpicId = epicId,
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = TestTime.UtcNow,
        });
        db.SaveChanges();
    }
}