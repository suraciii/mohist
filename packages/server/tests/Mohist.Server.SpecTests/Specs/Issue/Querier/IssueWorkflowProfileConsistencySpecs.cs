using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueWorkflowProfileConsistencySpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueWorkflowProfileConsistencySpecs(MohistDbFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(null, IssueWorkflowProfiles.LocalId)]
    [InlineData("mohist/github-pr", "mohist/github-pr")]
    public async Task GetAndListAsync_ProjectTheSameEffectiveWorkflowProfile(
        string? selectedProfile,
        string expectedProfile)
    {
        var project = new ProjectInfo
        {
            Id = $"proj-profile-{Guid.NewGuid():N}",
            Name = "Profile consistency",
        };
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = new DomainIssue
        {
            ProjectId = project.Id,
            Number = 42,
            Title = "Profile selection",
            Priority = "p2",
            Status = IssueStatus.Backlog,
            WorkflowProfileId = selectedProfile,
        };
        db.Issues.Add(new IssueRow { State = IssueStore.Serialize(issue) });
        await db.SaveChangesAsync();

        var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
        var detail = await querier.GetAsync(project.Id, 42, project);
        var listItem = Assert.Single(await querier.ListAsync(project.Id, project));

        Assert.NotNull(detail);
        Assert.Equal(expectedProfile, detail!.WorkflowProfileId);
        Assert.Equal(expectedProfile, listItem.WorkflowProfileId);
    }
}
