namespace Mohist.Server.Issue.Grains;

public interface IIssueCounterGrain : IGrainWithStringKey
{
    Task<int> NextAsync();
}

[GenerateSerializer]
public sealed record IssueCounterState([property: Id(0)] int Next);
