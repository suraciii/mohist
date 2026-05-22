using Mohist.Server.Issue.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public class IssueGrain : Grain, IIssueGrain
{
    private Issue.Domain.Issue? _issue;
    private readonly IStateStore<Issue.Domain.Issue> _issueStore;
    private readonly ILogger<IssueGrain> _log;

    public IssueGrain(IStateStore<Issue.Domain.Issue> issueStore, ILogger<IssueGrain> log)
    {
        _issueStore = issueStore;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _issue = await _issueStore.LoadAsync(GrainKey);
    }

    public async Task<string> StartWorkflowAsync()
    {
        EnsureIssue();
        _issue!.Open();
        var wrId = $"wr_{_issue.ProjectId}_{_issue.Number}";
        _issue.SetWorkflowRunId(wrId);
        await _issueStore.SaveAsync(GrainKey, _issue);

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StartAsync(MohistPipeline.Definition);

        var registry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Key);
        var runnerId = await registry.FindRunnerAsync([]);
        if (runnerId is not null)
        {
            var runner = GrainFactory.GetGrain<IRunnerGrain>(runnerId);
            await runner.AssignWorkflowAsync(wrId);
            await wfGrain.AssignRunnerAsync(runnerId);
        }

        _log.LogInformation("Issue {Key} started workflow {WrId}", GrainKey, wrId);
        return wrId;
    }

    public async Task CloseAsync()
    {
        EnsureIssue();
        if (_issue!.WorkflowRunId is { } wrId)
        {
            var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
            await wfGrain.PauseAsync("issue-closed");
        }
        _issue.Close();
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public Task<string?> GetWorkflowRunIdAsync()
    {
        EnsureIssue();
        return Task.FromResult(_issue!.WorkflowRunId);
    }

    public async Task UpdateAsync(string title, string? body)
    {
        EnsureIssue();
        _issue!.UpdateTitle(title);
        _issue.UpdateBody(body);
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public async Task ArchiveAsync()
    {
        EnsureIssue();
        _issue!.Archive();
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    private void EnsureIssue()
    {
        if (_issue is null)
            throw new InvalidOperationException($"Issue '{GrainKey}' not found");
    }
}
