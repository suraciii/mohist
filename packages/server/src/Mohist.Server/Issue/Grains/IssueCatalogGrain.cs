using Mohist.Server.Issue.Domain;
using Mohist.Server.Storage;

namespace Mohist.Server.Issue.Grains;

public class IssueCatalogGrain : Grain, IIssueCatalogGrain
{
    private readonly List<IssueInfo> _issues = [];
    private readonly IStateStore<IssueCatalogState> _store;
    private readonly ILogger<IssueCatalogGrain> _log;

    public IssueCatalogGrain(IStateStore<IssueCatalogState> store, ILogger<IssueCatalogGrain> log)
    {
        _store = store;
        _log = log;
    }

    private string ProjectId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        var state = await _store.LoadAsync(ProjectId);
        if (state is not null)
            _issues.AddRange(state.Issues);
    }

    public async Task<IssueInfo> CreateAsync(
        string title,
        string? body,
        string[]? labels,
        string? priority,
        string? model = null,
        Dictionary<string, string>? stageModels = null)
    {
        var counter = GrainFactory.GetGrain<IIssueCounterGrain>(ProjectId);
        var number = await counter.NextAsync();

        var grainKey = $"{ProjectId}:{number}";
        var issueGrain = GrainFactory.GetGrain<IIssueGrain>(grainKey);
        await issueGrain.HydrateAsync(ProjectId, number, title, body, labels, priority, model, stageModels);

        var info = await issueGrain.GetInfoAsync();
        _issues.Add(info);
        await SaveAsync();

        _log.LogInformation("Issue #{Number} created in project {Project}", number, ProjectId);
        return info;
    }

    public Task<List<IssueInfo>> ListAsync(string? stage = null, string? label = null, string? priority = null, bool? archived = null, bool? all = null)
    {
        var query = _issues.AsEnumerable();

        if (archived == true)
            query = query.Where(i => i.ArchivedAt != null);
        else if (all != true)
            query = query.Where(i => i.ArchivedAt == null);

        if (!string.IsNullOrEmpty(stage))
            query = query.Where(i => string.Equals(i.Stage, stage, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(label))
            query = query.Where(i => i.Labels.Contains(label, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(i => string.Equals(i.Priority, priority, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(query.ToList());
    }

    public Task<IssueInfo?> GetByNumberAsync(int number)
    {
        var issue = _issues.FirstOrDefault(i => i.Number == number);
        return Task.FromResult(issue);
    }

    public async Task<IssueInfo?> RemoveAsync(int number)
    {
        var issue = _issues.FirstOrDefault(i => i.Number == number);
        if (issue != null)
        {
            _issues.Remove(issue);
            await SaveAsync();
        }
        return issue;
    }

    private Task SaveAsync() => _store.SaveAsync(ProjectId, new IssueCatalogState(_issues));
}
