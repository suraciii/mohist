namespace Mohist.Server.Issue.Domain;

public abstract record IssueStartBlocker
{
    public sealed record Draft : IssueStartBlocker;

    public sealed record ParentHasChildren : IssueStartBlocker;

    public sealed record WaitingFor(int PrerequisiteNumber) : IssueStartBlocker;

    private IssueStartBlocker() { }
}
