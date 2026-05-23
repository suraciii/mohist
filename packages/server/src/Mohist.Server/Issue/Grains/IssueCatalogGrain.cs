using Mohist.Server.Issue.Domain;
using Mohist.Server.Storage;

namespace Mohist.Server.Issue.Grains;

public class IssueCatalogGrain : Grain, IIssueCatalogGrain
{
    private readonly List<IssueInfo> _issues = [];
    private readonly ILogger<IssueCatalogGrain> _log;

    public IssueCatalogGrain(ILogger<IssueCatalogGrain> log)
    {
        _log = log;
    }

    private string ProjectId => this.GetPrimaryKeyString();

    public async Task<IssueInfo> CreateAsync(string title, string? body, string[]? labels, string? priority)
    {
        var counter = GrainFactory.GetGrain<IIssueCounterGrain>(ProjectId);
        var number = await counter.NextAsync();

        var grainKey = $"{ProjectId}:{number}";
        var issueGrain = GrainFactory.GetGrain<IIssueGrain>(grainKey);
        await issueGrain.HydrateAsync(ProjectId, number, title, body, labels, priority);

        var info = await issueGrain.GetInfoAsync();
        _issues.Add(info);

        _log.LogInformation("Issue #{Number} created in project {Project}", number, ProjectId);
        return info;
    }

    public Task<List<IssueInfo>> ListAsync()
    {
        return Task.FromResult(_issues.ToList());
    }
}
