using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Issue.Services;

internal static class IssueCompositeStartPolicy
{
    public static IReadOnlyList<IssueChildCompositeInfo> SelectStartable(
        IReadOnlyList<IssueChildCompositeInfo> children)
    {
        var doneNumbers = children
            .Where(child => child.Status == IssueStatus.Done)
            .Select(child => child.Number)
            .ToHashSet();

        return children
            .Where(child => IsStartable(child, doneNumbers))
            .ToArray();
    }

    private static bool IsStartable(
        IssueChildCompositeInfo child,
        IReadOnlySet<int> doneNumbers)
    {
        return !child.IsDraft
            && !child.IsArchived
            && child.WorkflowRunId is null
            && child.Status == IssueStatus.Backlog
            && !string.IsNullOrWhiteSpace(child.RepositoryRef)
            && child.PrerequisiteNumbers.All(doneNumbers.Contains);
    }
}
