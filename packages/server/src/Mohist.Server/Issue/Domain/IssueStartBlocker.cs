using Orleans;

namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public abstract record IssueStartBlocker
{
    [GenerateSerializer]
    public sealed record Draft : IssueStartBlocker;

    [GenerateSerializer]
    public sealed record WaitingFor([property: Id(0)] int PrerequisiteNumber) : IssueStartBlocker;

    private IssueStartBlocker() { }
}
