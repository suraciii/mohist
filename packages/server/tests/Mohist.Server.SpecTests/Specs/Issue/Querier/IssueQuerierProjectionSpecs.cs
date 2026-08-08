using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

/// <summary>
/// Calculation specs for the issue-projection read paths that
/// <c>IssueApiSpecs</c> covered at HTTP scope. The querier
/// (<see cref="IssueQuerier.GetAsync"/>,
/// <see cref="IssueQuerier.ListAsync"/>) is driven directly via
/// <c>MohistDbFixture</c> (no web host, no HTTP round-trip). Specs seed
/// projects/issues straight into <c>MohistDbContext</c> and assert the
/// projection that the read model surfaces.
///
/// Five calculation cases sunk from
/// <c>Specs/Issue/Api/IssueApiSpecs.cs</c> cover:
/// <list type="bullet">
/// <item><c>IssueQuerier.GetAsync</c> leaves <c>Epic</c> unset when the
///   issue carries no <c>EpicNumber</c>; the additive field only
///   surfaces when a link is present.</item>
/// <item><c>IssueQuerier.ListAsync</c> returns the issues belonging to
///   the project filtered by their lifecycle status — the same source
///   data the <c>GET /api/projects/{ref}/status</c> route aggregates
///   into <c>issuesByStatus</c>. Sinking the bucketing here keeps the
///   route layer responsible only for the aggregation glue.</item>
/// <item>The lifecycle status surface stays on the canonical set
///   (<c>backlog</c>, <c>in_progress</c>, <c>done</c>,
///   <c>cancelled</c>) — legacy stage names (<c>plan</c>,
///   <c>build</c>, <c>check</c>) never leak into the projection,
///   matching the API spec's negative assertions on
///   <c>issuesByStatus</c>.</item>
/// </list>
/// The route contract (404 legacy collection route, 404 project
/// route, list scoping by route project, JSON envelope shape on
/// system info / system update) stays in <c>IssueApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class IssueQuerierProjectionSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierProjectionSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetAsync_OmitsEpicField_WhenIssueIsNotLinked()
    {
        var project = NewProject("no-epic");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var solo = NewDomainIssue(project.Id, 1, "Solo issue", IssueStatus.Backlog);
        db.Issues.Add(new IssueRow { ProjectId = project.Id, Number = solo.Number, State = IssueStore.Serialize(solo) });
        await db.SaveChangesAsync();

        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await querier.GetAsync(project.Id, solo.Number, project);

        Assert.NotNull(detail);
        Assert.Null(detail!.Epic);
    }

    [Fact]
    public async Task ListAsync_ProjectsLifecycleStatusBuckets_ForProjectStatusAggregation()
    {
        // The /api/projects/{ref}/status route aggregates
        // issuesByStatus from IssueQuerier.ListAsync(all: true). The
        // projection itself must surface the canonical lifecycle set:
        // backlog, in_progress, done, cancelled — never the legacy
        // stage names (plan, build, check). The route layer is
        // responsible for the bucket arithmetic; this spec only
        // asserts the underlying list keeps the lifecycle status
        // surface clean.
        var project = NewProject("status-bucket");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.Issues.Add(new IssueRow { ProjectId = project.Id, Number = 1, State = IssueStore.Serialize(NewDomainIssue(project.Id, 1, "Backlog", IssueStatus.Backlog)) });
        db.Issues.Add(new IssueRow { ProjectId = project.Id, Number = 2, State = IssueStore.Serialize(NewDomainIssue(project.Id, 2, "In progress", IssueStatus.InProgress)) });
        db.Issues.Add(new IssueRow { ProjectId = project.Id, Number = 3, State = IssueStore.Serialize(NewDomainIssue(project.Id, 3, "Done", IssueStatus.Done)) });
        db.Issues.Add(new IssueRow { ProjectId = project.Id, Number = 4, State = IssueStore.Serialize(NewDomainIssue(project.Id, 4, "Cancelled", IssueStatus.Cancelled)) });
        await db.SaveChangesAsync();

        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var issues = await querier.ListAsync(project.Id, project, all: true);

        Assert.Equal(4, issues.Count);
        var statuses = issues.Select(issue => issue.Status).OrderBy(status => status, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "backlog", "cancelled", "done", "in_progress" }, statuses);

        // The aggregation the route performs must collapse to the
        // four canonical buckets — no legacy stage names ever leak
        // through the projection.
        var issuesByStatus = issues
            .GroupBy(issue => issue.Status)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(1, issuesByStatus["backlog"]);
        Assert.Equal(1, issuesByStatus["in_progress"]);
        Assert.Equal(1, issuesByStatus["done"]);
        Assert.Equal(1, issuesByStatus["cancelled"]);
        Assert.False(issuesByStatus.ContainsKey("plan"));
        Assert.False(issuesByStatus.ContainsKey("build"));
        Assert.False(issuesByStatus.ContainsKey("check"));
    }

    [Fact]
    public async Task GetAsync_ProjectsIssueRowWithBacklogStatus_ForReadModelSurface()
    {
        // Regression guard for the read-model surface: GetAsync
        // returns an IssueReadModel whose Status reflects the
        // canonical backlog string, not the C# enum name. The API
        // spec's Comments_RoundTripThroughIssueDetailShape test
        // covered this at HTTP scope; sinking the calculation here
        // keeps the projection invariant locked to the querier.
        var project = NewProject("status-shape");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var solo = NewDomainIssue(project.Id, 1, "Backlog issue", IssueStatus.Backlog);
        db.Issues.Add(new IssueRow { ProjectId = project.Id, Number = solo.Number, State = IssueStore.Serialize(solo) });
        await db.SaveChangesAsync();

        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await querier.GetAsync(project.Id, solo.Number, project);

        Assert.NotNull(detail);
        Assert.Equal("backlog", detail!.Status);
        Assert.Equal("active", detail.Health);
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        Id = $"proj-qproj-{name}-{Guid.NewGuid():N}",
        Name = name,
        Repositories =
        {
            new RepositoryInfo
            {
                Name = "origin",
                GitUrl = "git@example.com:mohist-local.git",
                BaseBranch = "main",
                IsDefault = true,
            },
        },
    };

    private static DomainIssue NewDomainIssue(string projectId, int number, string title, IssueStatus status) =>
        new()
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = status,
            Priority = "p2",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
        };
}
