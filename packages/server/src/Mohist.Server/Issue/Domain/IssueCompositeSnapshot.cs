namespace Mohist.Server.Issue.Domain;

public sealed record ChildSnapshot(
    int Number,
    IssueStatus Status);

public sealed class IssueEmptyCompositeSnapshotException(int issueNumber)
    : InvalidOperationException($"Issue #{issueNumber} is not a parent; composite transitions require a non-empty children snapshot");

public sealed class IssueParentHasNonTerminalChildrenException : InvalidOperationException
{
    public IssueParentHasNonTerminalChildrenException(int issueNumber, IReadOnlyCollection<int> nonTerminalChildNumbers)
        : base($"Issue #{issueNumber} cannot close while children [{string.Join(", ", nonTerminalChildNumbers)}] are non-terminal")
    {
        NonTerminalChildNumbers = nonTerminalChildNumbers;
    }

    public IReadOnlyCollection<int> NonTerminalChildNumbers { get; }
}
