using Orleans;

namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public sealed class IssueStartBlockedException : InvalidOperationException
{
    [Id(0)]
    public IssueStartBlocker Blocker { get; }

    public IssueStartBlockedException(IssueStartBlocker blocker, string message)
        : base(message)
    {
        Blocker = blocker;
    }
}
