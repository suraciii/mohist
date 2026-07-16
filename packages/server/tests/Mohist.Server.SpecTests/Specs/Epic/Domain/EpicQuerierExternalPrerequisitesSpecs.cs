using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicQuerierExternalPrerequisitesSpecs
{
    [Fact]
    public void BuildExternalPrerequisites_ReturnsEmptyWhenNoPrerequisites()
    {
        var issue = ReadModel(1, "A", prerequisiteNumbers: []);
        var members = new HashSet<int> { 1 };
        var byNumber = new Dictionary<int, IssueReadModel> { [1] = issue };

        var result = EpicQuerier.BuildExternalPrerequisites(issue, members, byNumber);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildExternalPrerequisites_InternalPrerequisitesAreExcluded()
    {
        var a = ReadModel(1, "A");
        var b = ReadModel(2, "B", prerequisiteNumbers: [1]);
        var members = new HashSet<int> { 1, 2 };
        var byNumber = new Dictionary<int, IssueReadModel> { [1] = a, [2] = b };

        var result = EpicQuerier.BuildExternalPrerequisites(b, members, byNumber);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildExternalPrerequisites_ResolvesExternalPrereqToSummary()
    {
        var member = ReadModel(1, "Member", prerequisiteNumbers: [42]);
        var external = ReadModel(42, "External upstream", status: "in_progress", health: "active", stage: "in_progress");
        var members = new HashSet<int> { 1 };
        var byNumber = new Dictionary<int, IssueReadModel> { [1] = member, [42] = external };

        var result = EpicQuerier.BuildExternalPrerequisites(member, members, byNumber);

        var ghost = Assert.Single(result);
        Assert.Equal(42, ghost.Number);
        Assert.Equal("External upstream", ghost.Title);
        Assert.Equal("in_progress", ghost.Stage);
        Assert.Equal("in_progress", ghost.Status);
    }

    [Fact]
    public void BuildExternalPrerequisites_UnresolvablePrereqDegradesToMinimalRef()
    {
        var member = ReadModel(1, "Member", prerequisiteNumbers: [999_999]);
        var members = new HashSet<int> { 1 };
        var byNumber = new Dictionary<int, IssueReadModel> { [1] = member };

        var result = EpicQuerier.BuildExternalPrerequisites(member, members, byNumber);

        var ghost = Assert.Single(result);
        Assert.Equal(999_999, ghost.Number);
        Assert.Equal("", ghost.Title);
        Assert.Equal("", ghost.Stage);
        Assert.Equal("", ghost.Status);
    }

    [Fact]
    public void BuildExternalPrerequisites_DeduplicatesPrereqNumbers()
    {
        var member = ReadModel(1, "Member", prerequisiteNumbers: [42, 42, 42]);
        var external = ReadModel(42, "External upstream");
        var members = new HashSet<int> { 1 };
        var byNumber = new Dictionary<int, IssueReadModel> { [1] = member, [42] = external };

        var result = EpicQuerier.BuildExternalPrerequisites(member, members, byNumber);

        var ghost = Assert.Single(result);
        Assert.Equal(42, ghost.Number);
    }

    [Fact]
    public void BuildExternalPrerequisites_MixedInternalAndExternal()
    {
        var a = ReadModel(1, "A");
        var b = ReadModel(2, "B");
        var external = ReadModel(99, "External X");
        var member = ReadModel(3, "Member", prerequisiteNumbers: [1, 2, 99]);
        var members = new HashSet<int> { 1, 2, 3 };
        var byNumber = new Dictionary<int, IssueReadModel>
        {
            [1] = a,
            [2] = b,
            [3] = member,
            [99] = external,
        };

        var result = EpicQuerier.BuildExternalPrerequisites(member, members, byNumber);

        var ghost = Assert.Single(result);
        Assert.Equal(99, ghost.Number);
        Assert.Equal("External X", ghost.Title);
    }

    private static IssueReadModel ReadModel(
        int number,
        string title,
        string status = "backlog",
        string health = "active",
        string? stage = null,
        int[]? prerequisiteNumbers = null) =>
        new()
        {
            Number = number,
            Title = title,
            Status = status,
            Health = health,
            WorkflowStage = stage,
            PrerequisiteNumbers = prerequisiteNumbers ?? [],
        };
}
