using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueQuerierPrimaryEpicSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierPrimaryEpicSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        Assert.NotNull(item.PrimaryEpic);
        Assert.Equal(7, item.PrimaryEpic!.Number);
        Assert.Equal("Epic 7", item.PrimaryEpic.Title);
        Assert.Equal(status, item.PrimaryEpic.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        Assert.Null(item.PrimaryEpic);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        Assert.NotNull(item.PrimaryEpic);
        Assert.Equal(9, item.PrimaryEpic!.Number);
    }

    private static ProjectInfo NewProject() => new()
    {
        Id = $"proj-primary-{Guid.NewGuid():N}",
        Name = "Primary Epic",
    };

    private static async Task SeedIssueAsync(MohistDbContext db, string projectId, int number, int? epicNumber)
    {
        var issue = new Issue
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
