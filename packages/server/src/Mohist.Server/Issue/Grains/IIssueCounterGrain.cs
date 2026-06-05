namespace Mohist.Server.Issue.Grains;

public interface IIssueCounterGrain : IGrainWithStringKey
{
    Task<int> NextAsync();
}
