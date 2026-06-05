using Mohist.Server.Infrastructure.Data.Issue;

namespace Mohist.Server.Issue.Services;

internal static class IssueRowMapper
{
    public static IEnumerable<Domain.Issue> Deserialize(IEnumerable<IssueRow> rows)
    {
        foreach (var row in rows)
        {
            var issue = IssueSnapshot.DeserializeIssue(row.State);
            if (issue is not null)
                yield return issue;
        }
    }

    public static IReadOnlyList<Domain.Issue> ById(IEnumerable<IssueRow> rows, string projectId) =>
        Deserialize(rows)
            .Where(issue => issue.ProjectId == projectId)
            .ToList();

    public static IReadOnlyList<Domain.Issue> ByNumber(IEnumerable<IssueRow> rows, string projectId) =>
        Deserialize(rows)
            .Where(issue => issue.ProjectId == projectId)
            .ToList();

    public static Dictionary<int, Domain.Issue> ByNumber(
        IEnumerable<IssueRow> rows,
        string projectId,
        IEnumerable<int> issueNumbers)
    {
        var numbers = issueNumbers.ToHashSet();
        return Deserialize(rows)
            .Where(issue => issue.ProjectId == projectId && numbers.Contains(issue.Number))
            .ToDictionary(issue => issue.Number);
    }

    public static bool IsIssue(Domain.Issue? issue, string projectId, int number) =>
        issue is not null && issue.ProjectId == projectId && issue.Number == number;
}
