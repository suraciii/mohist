using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Domain;
using Mohist.Server.Storage.Db;
using Mohist.Server.Storage.Db.Entities;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class IssueQueryServiceSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueQueryServiceSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_ReadsIssueStateWithoutCallingIssueGrain()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = "proj-1", Name = "Project One", Path = "/tmp/project" };
        var issue = new Issue.Domain.Issue("issue_1", project.Id, 1, "Query me", labels: ["bug"], priority: "p1");
        issue.MarkReady();
        db.GrainStates.Add(new GrainState
        {
            Key = $"{project.Id}:1",
            Type = typeof(Issue.Domain.Issue).FullName!,
            JsonState = System.Text.Json.JsonSerializer.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQueryService>();

        var list = await service.ListAsync(project.Id, project, stage: "todo", label: "bug");

        var item = Assert.Single(list);
        Assert.Equal("Query me", item.Title);
        Assert.Equal("todo", item.Stage);
        Assert.Equal("Project One", item.ProjectName);
    }
}
