using Mohist.Server.Issue.Storage;

namespace Mohist.Server.Issue.Querying;

internal static class IssueStateReader
{
    public static IEnumerable<IssueStateEntry> Deserialize(IEnumerable<IssueStateRow> rows)
    {
        foreach (var row in rows)
        {
            var issue = IssueSnapshot.DeserializeIssue(row.StateJson);
            if (issue is not null)
                yield return new IssueStateEntry(row.Key, issue);
        }
    }

    public static Domain.Issue? SelectCanonicalOrDefault(IEnumerable<IssueStateEntry> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return null;
        return list.FirstOrDefault(row => row.Key == row.Issue.Id).Issue ?? list[0].Issue;
    }

    public static Domain.Issue SelectCanonical(IEnumerable<IssueStateEntry> rows) =>
        SelectCanonicalOrDefault(rows)
        ?? throw new InvalidOperationException("Cannot select a canonical issue from an empty row set.");

    public static IReadOnlyList<Domain.Issue> SelectCanonicalById(IEnumerable<IssueStateRow> rows, string projectId) =>
        Deserialize(rows)
            .Where(row => row.Issue.ProjectId == projectId)
            .GroupBy(row => row.Issue.Id, StringComparer.Ordinal)
            .Select(SelectCanonical)
            .ToList();

    public static IReadOnlyList<Domain.Issue> SelectCanonicalByNumber(IEnumerable<IssueStateRow> rows, string projectId) =>
        Deserialize(rows)
            .Where(row => row.Issue.ProjectId == projectId)
            .GroupBy(row => row.Issue.Number)
            .Select(SelectCanonical)
            .ToList();

    public static Dictionary<int, Domain.Issue> SelectCanonicalByNumber(
        IEnumerable<IssueStateRow> rows,
        string projectId,
        IEnumerable<int> issueNumbers)
    {
        var numbers = issueNumbers.ToHashSet();
        return Deserialize(rows)
            .Where(row => row.Issue.ProjectId == projectId && numbers.Contains(row.Issue.Number))
            .GroupBy(row => row.Issue.Number)
            .Select(SelectCanonical)
            .ToDictionary(issue => issue.Number);
    }

    public static bool IsIssue(Domain.Issue? issue, string projectId, int number) =>
        issue is not null && issue.ProjectId == projectId && issue.Number == number;
}

internal readonly record struct IssueStateEntry(string Key, Domain.Issue Issue);
