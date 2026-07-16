using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQuerierSpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetAndListAsync_UseProjectScopedIssueNumber()
    {
        var projectA = NewProject("A");
        var projectB = NewProject("B");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, projectA.Id, 42, "Project A issue");
        await SeedIssueAsync(db, projectB.Id, 42, "Project B issue");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var detail = await querier.GetAsync(projectA.Id, 42, projectA);
        var list = await querier.ListAsync(projectB.Id, projectB);

        Assert.NotNull(detail);
        Assert.Equal((projectA.Id, 42, "Project A issue"), (detail!.ProjectId, detail.Number, detail.Title));
        var item = Assert.Single(list);
        Assert.Equal((projectB.Id, 42, "Project B issue"), (item.ProjectId, item.Number, item.Title));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_FiltersCurrentIssueStateByStatusLabelAndPriority()
    {
        var project = NewProject("filters");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Frontend", "backlog", "p1", ["stream", "frontend"]);
        await SeedIssueAsync(db, project.Id, 2, "Backend", "backlog", "p1", ["stream", "backend"]);
        await SeedIssueAsync(db, project.Id, 3, "Done", "done", "p1", ["stream", "frontend"]);
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var result = await querier.ListAsync(
            project.Id,
            project,
            stage: "backlog",
            label: "stream=frontend",
            priority: "p1");

        var item = Assert.Single(result);
        Assert.Equal((1, "Frontend"), (item.Number, item.Title));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListAsync_UsesBuiltInWorkflowProfileWhenIssueHasNoSelection()
    {
        var project = NewProject("profile");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Profile");

        var item = Assert.Single(await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .ListAsync(project.Id, project));

        Assert.Equal(IssueWorkflowProfiles.LocalId, item.WorkflowProfileId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetIssueRefForWorkflowRunAsync_ReturnsScopedIssueReadContext()
    {
        var project = NewProject("workflow");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 17, "Workflow issue", workflowRunId: "wr_17");

        var issue = await scope.ServiceProvider.GetRequiredService<IssueQuerier>()
            .GetIssueRefForWorkflowRunAsync("wr_17");

        Assert.NotNull(issue);
        Assert.Equal((17, "Workflow issue"), (issue!.Number, issue.Title));
    }

    private static ProjectInfo NewProject(string name) => new()
    {
        Id = $"proj-query-{name}-{Guid.NewGuid():N}",
        Name = name,
    };

    private static async Task SeedIssueAsync(
        MohistDbContext db,
        string projectId,
        int number,
        string title,
        string status = "backlog",
        string priority = "p2",
        string[]? label = null,
        string? workflowRunId = null)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = Enum.Parse<IssueStatus>(status, ignoreCase: true),
            Priority = priority,
            WorkflowRunId = workflowRunId,
            Labels = label is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { [label[0]] = label[1] },
        };
        db.Issues.Add(new IssueRow { State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();
    }
}
