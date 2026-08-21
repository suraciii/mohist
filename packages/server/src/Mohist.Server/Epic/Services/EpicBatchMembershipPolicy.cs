using Mohist.Server.Issue.Services;

namespace Mohist.Server.Epic.Services;

internal static class EpicBatchMembershipPolicy
{
    internal static EpicBatchMembershipRequest Resolve(
        IReadOnlyList<IssueReadModel> issues,
        IReadOnlyList<int> requestedNumbers)
    {
        var byNumber = issues.ToDictionary(issue => issue.Number);
        var resolved = new List<BatchMembershipRequestItem>(requestedNumbers.Count);
        var byIdentifier = new Dictionary<string, BatchMembershipRequestItem>(StringComparer.Ordinal);
        var seenNumbers = new HashSet<int>();

        foreach (var issueNumber in requestedNumbers)
        {
            var identifier = issueNumber.ToString();
            var item = byNumber.ContainsKey(issueNumber)
                ? new BatchMembershipRequestItem(identifier, issueNumber)
                : new BatchMembershipRequestItem(identifier, 0);
            byIdentifier[identifier] = item;
            if (item.IssueNumber > 0 && seenNumbers.Add(item.IssueNumber))
                resolved.Add(item);
        }

        return new EpicBatchMembershipRequest(
            requestedNumbers.Select(number => number.ToString()).ToArray(),
            resolved,
            byIdentifier);
    }

    internal static IReadOnlyList<BatchMembershipOutcome> Merge(
        EpicBatchMembershipRequest request,
        IReadOnlyList<BatchMembershipOutcome> outcomes,
        bool isUnlink)
    {
        var byIssueNumber = outcomes
            .Where(outcome => outcome.IssueNumber.HasValue)
            .GroupBy(outcome => outcome.IssueNumber!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        var seenIssueNumbers = new HashSet<int>();
        var results = new List<BatchMembershipOutcome>(request.RequestedIdentifiers.Count);
        foreach (var identifier in request.RequestedIdentifiers)
        {
            if (!request.ByIdentifier.TryGetValue(identifier, out var item) || item.IssueNumber <= 0)
            {
                results.Add(isUnlink
                    ? new BatchMembershipOutcome(identifier, "was-not-a-member")
                    : BatchMembershipOutcome.NotFound(identifier));
                continue;
            }

            if (!byIssueNumber.TryGetValue(item.IssueNumber, out var outcome))
            {
                results.Add(isUnlink
                    ? BatchMembershipOutcome.WasNotAMember(identifier, item.IssueNumber)
                    : BatchMembershipOutcome.NotFound(identifier));
                continue;
            }

            results.Add(seenIssueNumbers.Add(item.IssueNumber)
                ? outcome with { Identifier = identifier }
                : Duplicate(identifier, item, outcome, isUnlink));
        }

        return results;
    }

    private static BatchMembershipOutcome Duplicate(
        string identifier,
        BatchMembershipRequestItem item,
        BatchMembershipOutcome firstOutcome,
        bool isUnlink)
    {
        if (isUnlink)
            return BatchMembershipOutcome.WasNotAMember(identifier, item.IssueNumber);
        return firstOutcome.Status == "conflict"
            ? firstOutcome with { Identifier = identifier }
            : firstOutcome with { Identifier = identifier, Status = "already-linked" };
    }
}

internal sealed record EpicBatchMembershipRequest(
    IReadOnlyList<string> RequestedIdentifiers,
    IReadOnlyList<BatchMembershipRequestItem> ResolvedItems,
    IReadOnlyDictionary<string, BatchMembershipRequestItem> ByIdentifier);
