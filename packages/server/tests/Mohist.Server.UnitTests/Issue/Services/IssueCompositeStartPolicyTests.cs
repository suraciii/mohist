using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Services;

public class IssueCompositeStartPolicyTests
{
    [Fact]
    public void SelectStartable_ReturnsOnlyEligibleChildrenInSnapshotOrder()
    {
        var children = new[]
        {
            Child(1, IssueStatus.Done),
            Child(2, IssueStatus.Backlog),
            Child(3, IssueStatus.Backlog, prerequisites: [1]),
            Child(4, IssueStatus.Backlog, isDraft: true),
            Child(5, IssueStatus.Backlog, workflowRunId: "wr_active"),
            Child(6, IssueStatus.InProgress),
            Child(7, IssueStatus.Backlog, repositoryRef: null),
            Child(8, IssueStatus.Backlog, prerequisites: [999]),
            Child(9, IssueStatus.Backlog, isArchived: true),
        };

        var selected = IssueCompositeStartPolicy.SelectStartable(children);

        Assert.Equal([2, 3], selected.Select(child => child.Number));
    }

    [Fact]
    public void SelectStartable_RequiresEveryPrerequisiteToBeDoneInTheSnapshot()
    {
        var children = new[]
        {
            Child(1, IssueStatus.Done),
            Child(2, IssueStatus.Cancelled),
            Child(3, IssueStatus.Backlog, prerequisites: [1, 2]),
        };

        Assert.Empty(IssueCompositeStartPolicy.SelectStartable(children));
    }

    [Fact]
    public void SelectStartable_EmptySnapshot_ReturnsEmptySelection()
    {
        Assert.Empty(IssueCompositeStartPolicy.SelectStartable([]));
    }

    private static IssueChildCompositeInfo Child(
        int number,
        IssueStatus status,
        bool isDraft = false,
        int[]? prerequisites = null,
        string? workflowRunId = null,
        string? repositoryRef = "main",
        bool isArchived = false) =>
        new(
            Number: number,
            Status: status,
            IsDraft: isDraft,
            PrerequisiteNumbers: prerequisites ?? [],
            WorkflowRunId: workflowRunId,
            RepositoryRef: repositoryRef,
            IsArchived: isArchived);
}
