namespace Mohist.Server.Issue.Grains;

public class IssueCounterGrain : Grain, IIssueCounterGrain
{
    private int _next;

    public override Task OnActivateAsync(CancellationToken ct)
    {
        _next = 1;
        return Task.CompletedTask;
    }

    public Task<int> NextAsync()
    {
        return Task.FromResult(_next++);
    }
}
