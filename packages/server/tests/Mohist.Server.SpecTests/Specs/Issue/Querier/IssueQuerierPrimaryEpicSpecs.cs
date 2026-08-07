using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueQuerierPrimaryEpicSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierPrimaryEpicSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(EpicStatusName.Idle)]
    [InlineData(EpicStatusName.Running)]
    [InlineData(EpicStatusName.Paused)]
    public async Task PrimaryEpic_NonTerminalCurrentAffiliation_ProjectsEpic(string status)
    {
        var project = NewProject();
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedEpicAsync(db, project.Id, 7, status);
        await SeedIssueAsync(db, project.Id, 42, epicNumber: 7);

        var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .ListAsync(project.Id, project));

        Assert.Equal(42, item.Number);
        Assert.NotNull(item.Epic);
        Assert.Equal(7, item.Epic!.Number);
        Assert.Equal("Epic 7", item.Epic.Title);
        Assert.Equal(status, item.Epic.Status);
    }

    [Theory]
    [InlineData(EpicStatusName.Done)]
    [InlineData(EpicStatusName.Closed)]
    public async Task PrimaryEpic_TerminalCurrentAffiliation_ProjectsNull(string status)
    {
        var project = NewProject();
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedEpicAsync(db, project.Id, 7, status);
        await SeedIssueAsync(db, project.Id, 42, epicNumber: 7);

        var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .ListAsync(project.Id, project));

        Assert.Null(item.Epic);
    }

    [Fact]
    public async Task PrimaryEpic_CurrentAffiliationAfterMove_ProjectsNewEpicOnly()
    {
        var project = NewProject();
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedEpicAsync(db, project.Id, 7, EpicStatusName.Closed);
        await SeedEpicAsync(db, project.Id, 9, EpicStatusName.Idle);
        await SeedIssueAsync(db, project.Id, 42, epicNumber: 9);

        var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .ListAsync(project.Id, project));

        Assert.NotNull(item.Epic);
        Assert.Equal(9, item.Epic!.Number);
    }

    [Fact]
    public async Task List_WithEpicNumber_ReturnsOnlyIssuesInThatEpic()
    {
        var project = NewProject();
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedEpicAsync(db, project.Id, 7, EpicStatusName.Idle);
        await SeedEpicAsync(db, project.Id, 9, EpicStatusName.Idle);
        await SeedIssueAsync(db, project.Id, 42, epicNumber: 7);
        await SeedIssueAsync(db, project.Id, 43, epicNumber: 9);

        var filtered = await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .ListAsync(project.Id, project, epicNumber: 7);

        var item = Assert.Single(filtered);
        Assert.Equal(42, item.Number);
        Assert.Equal(7, item.Epic?.Number);
    }

    private static ProjectInfo NewProject() => new()
    {
        Id = $"proj-primary-{Guid.NewGuid():N}",
        Name = "Primary Epic",
    };

    private static async Task SeedIssueAsync(MohistDbContext db, string projectId, int number, int? epicNumber)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {number}",
            Priority = "p2",
            Status = IssueStatus.Backlog,
            EpicNumber = epicNumber,
        };
        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
            EpicNumber = epicNumber,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedEpicAsync(MohistDbContext db, string projectId, int number, string status)
    {
        db.Epics.Add(new EpicRow
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {number}",
            Description = "",
            Priority = "p1",
            Status = status,
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
