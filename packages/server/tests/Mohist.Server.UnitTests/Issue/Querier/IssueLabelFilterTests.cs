using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Querier;

[Collection("MohistDb")]
public class IssueLabelFilterTests
{
    private readonly MohistDbFixture _fixture;

    public IssueLabelFilterTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListAsync_FiltersByKeyValueLabel()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-kv-{Guid.NewGuid():N}", Name = "KV Project" };

        var issueA = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_a_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Stream frontend",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        var issueB = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_b_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 2,
            Title = "Stream backend",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "backend",
            },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow { IssueId = issueA.Id, State = IssueStore.Serialize(issueA) });
        db.Issues.Add(new IssueRow { IssueId = issueB.Id, State = IssueStore.Serialize(issueB) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var frontendHits = await service.ListAsync(project.Id, project, label: "stream=frontend");
        var frontendItem = Assert.Single(frontendHits);
        Assert.Equal(issueA.Number, frontendItem.Number);

        var backendHits = await service.ListAsync(project.Id, project, label: "stream=backend");
        var backendItem = Assert.Single(backendHits);
        Assert.Equal(issueB.Number, backendItem.Number);

        var missingHits = await service.ListAsync(project.Id, project, label: "stream=missing");
        Assert.Empty(missingHits);

        var keyMissHits = await service.ListAsync(project.Id, project, label: "missing=anything");
        Assert.Empty(keyMissHits);
    }

    [Fact]
    public async Task ListAsync_WithMultipleKeyValueLabels_RequiresAllFilters()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-kv-multi-{Guid.NewGuid():N}", Name = "KV Multi Project" };

        var match = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_multi_match_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 1,
            Title = "Frontend auth",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = "frontend",
                ["module"] = "auth",
            },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };
        var missingModule = new Mohist.Server.Issue.Domain.Issue
        {
            Id = $"issue_kv_multi_miss_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Number = 2,
            Title = "Frontend only",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" },
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Backlog,
        };

        db.Issues.Add(new IssueRow { IssueId = match.Id, State = IssueStore.Serialize(match) });
        db.Issues.Add(new IssueRow { IssueId = missingModule.Id, State = IssueStore.Serialize(missingModule) });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueQuerier>();

        var listed = await service.ListWithLabelFiltersAsync(
            project.Id,
            project,
            stage: null,
            labels: ["stream=frontend", "module=auth"],
            priority: null,
            archived: null,
            all: null);

        var item = Assert.Single(listed);
        Assert.Equal(match.Number, item.Number);
    }

    [Fact]
    public void ParseLabelFilter_SplitsOnFirstEquals()
    {
        var (key, value) = IssueQuerier.ParseLabelFilter("stream=frontend");
        Assert.Equal("stream", key);
        Assert.Equal("frontend", value);

        var withEqualsInValue = IssueQuerier.ParseLabelFilter("k=v=w");
        Assert.Equal("k", withEqualsInValue.Key);
        Assert.Equal("v=w", withEqualsInValue.Value);

        var noEquals = IssueQuerier.ParseLabelFilter("justatoken");
        Assert.Null(noEquals.Key);
        Assert.Equal("justatoken", noEquals.Value);
    }

    [Fact]
    public void LabelFilterTokens_SplitsCommaJoinedLegacyQuery()
    {
        Assert.Equal(
            new[] { "stream=frontend", "module=auth" },
            IssueQuerier.LabelFilterTokens("stream=frontend,module=auth"));
    }
}
