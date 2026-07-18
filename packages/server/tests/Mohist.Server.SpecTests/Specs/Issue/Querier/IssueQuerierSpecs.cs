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

    [Fact]
    public async Task Reads_ProjectParentAndChildSummary_AndParentFilter()
    {
        var project = NewProject("parents");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Child one", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Child two", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 4, "Other");
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);
        var child = await querier.GetAsync(project.Id, 2, project);
        var children = await querier.ListWithLabelFiltersAsync(project.Id, project, null, null, null, null, null, null, 1);
        var empty = await querier.ListWithLabelFiltersAsync(project.Id, project, null, null, null, null, null, null, 99);

        Assert.Equal((1, "Parent"), (child!.ParentIssueRef!.Number, child.ParentIssueRef.Title));
        Assert.Equal((true, 2), (parent!.ChildIssuesSummary!.HasChildren, parent.ChildIssuesSummary.Count));
        Assert.Equal((2, 0, 0, 0), (parent.ChildIssuesSummary.BacklogCount, parent.ChildIssuesSummary.InProgressCount, parent.ChildIssuesSummary.DoneCount, parent.ChildIssuesSummary.CancelledCount));
        Assert.Equal([2, 3], children.Select(issue => issue.Number).ToArray());
        Assert.Empty(empty);
    }

    [Fact]
    public async Task ChildIssuesSummary_ComputesPerStatusBreakdownFromSameGroupBy()
    {
        var project = NewProject("status-breakdown");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        await SeedIssueAsync(db, project.Id, 1, "Parent");
        await SeedIssueAsync(db, project.Id, 2, "Backlog child", status: "backlog", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 3, "Backlog child two", status: "backlog", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 4, "In-progress child", status: "inProgress", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 5, "Done child", status: "done", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 6, "Cancelled child", status: "cancelled", parentIssueNumber: 1);
        await SeedIssueAsync(db, project.Id, 7, "Unrelated parent", status: "done");
        await SeedIssueAsync(db, project.Id, 8, "Unrelated child of unrelated parent", status: "done", parentIssueNumber: 7);
        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var parent = await querier.GetAsync(project.Id, 1, project);
        var unrelated = await querier.GetAsync(project.Id, 7, project);
        var orphanChild = await querier.GetAsync(project.Id, 8, project);

        Assert.NotNull(parent!.ChildIssuesSummary);
        Assert.Equal(5, parent.ChildIssuesSummary.Count);
        Assert.Equal(2, parent.ChildIssuesSummary.BacklogCount);
        Assert.Equal(1, parent.ChildIssuesSummary.InProgressCount);
        Assert.Equal(1, parent.ChildIssuesSummary.DoneCount);
        Assert.Equal(1, parent.ChildIssuesSummary.CancelledCount);

        Assert.NotNull(unrelated!.ChildIssuesSummary);
        Assert.Equal(1, unrelated.ChildIssuesSummary.Count);
        Assert.Equal((0, 0, 1, 0), (unrelated.ChildIssuesSummary.BacklogCount, unrelated.ChildIssuesSummary.InProgressCount, unrelated.ChildIssuesSummary.DoneCount, unrelated.ChildIssuesSummary.CancelledCount));

        Assert.NotNull(orphanChild!.ParentIssueRef);
        Assert.Equal(7, orphanChild.ParentIssueRef.Number);
        Assert.Null(orphanChild.ChildIssuesSummary);
    }

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
        Assert.Equal((project.Id, 17, "Workflow issue"), (issue!.ProjectId, issue.Number, issue.Title));
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
        string? workflowRunId = null,
        int? parentIssueNumber = null)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = Enum.Parse<IssueStatus>(status, ignoreCase: true),
            Priority = priority,
            WorkflowRunId = workflowRunId,
            ParentIssueNumber = parentIssueNumber,
            Labels = label is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { [label[0]] = label[1] },
        };
        db.Issues.Add(new IssueRow
        {
            State = IssueStore.Serialize(issue),
            ParentIssueNumber = issue.ParentIssueNumber,
        });
        await db.SaveChangesAsync();
    }
}
