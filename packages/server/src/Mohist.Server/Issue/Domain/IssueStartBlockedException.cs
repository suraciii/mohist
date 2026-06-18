namespace Mohist.Server.Issue.Domain;

public sealed class IssueStartBlockedException : InvalidOperationException
{
    public IssueStartBlocker Blocker { get; }

    public IssueStartBlockedException(IssueStartBlocker blocker, string message)
        : base(message)
    {
        Blocker = blocker;
    }
}
