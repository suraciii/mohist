using System.Text.Json;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Storage;
using Mohist.Server.Variables.Grains;
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

        var variables = GrainFactory.GetGrain<IVariableScopeGrain>(wrId);
        await variables.SetContextAsync("issue", $$"""
        {
          "id": {{JsonString(_issue.Id)}},
          "number": {{_issue.Number}},
          "title": {{JsonString(_issue.Title)}},
          "body": {{JsonString(_issue.Body ?? "")}}
        }
        """);
        await variables.SetContextAsync("project", $$"""
        {
          "id": {{JsonString(_issue.ProjectId)}},
          "path": ".",
          "defaultBranch": "main"
        }
        """);
        await variables.SetContextAsync("artifacts", $$"""
        {
          "changeDir": "openspec/changes/{{_issue.Number}}-{{Slug(_issue.Title)}}"
        }
        """);
        await variables.SetContextAsync("vars", """
        {
          "planHealthCommand": "npm ci && npm run typecheck",
          "buildHealthCommand": "npm ci && npm run build",
          "checkHealthCommand": "npm ci && npm run build && npm test",
          "integrateHealthCommand": "npm ci && npm run build && npm test",
          "projectPath": "."
        }
        """);

        var backlog = GrainFactory.GetGrain<IWorkflowBacklogGrain>(WorkflowBacklogKeys.Key);
        await backlog.RegisterAsync(wrId);

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

    public async Task<IssueWorkflowStatus?> GetWorkflowStatusAsync()
    {
        EnsureIssue();

        var wrId = _issue!.WorkflowRunId;
        if (wrId is null) return null;

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        var wfStatus = await wfGrain.GetStatusAsync();

        var scope = GrainFactory.GetGrain<IVariableScopeGrain>(wrId);
        var variables = await scope.SnapshotAsync(new VariableSnapshotRequest(wrId, "", "", null, null));

        string? changeDir = null;
        string? workspacePath = null;
        try
        {
            using var doc = JsonDocument.Parse(variables);
            if (doc.RootElement.TryGetProperty("artifacts", out var artifacts) &&
                artifacts.TryGetProperty("changeDir", out var cd))
                changeDir = cd.GetString();
            if (doc.RootElement.TryGetProperty("workspace", out var ws) &&
                ws.TryGetProperty("path", out var wp))
                workspacePath = wp.GetString();
        }
        catch
        {
        }

        return new IssueWorkflowStatus(
            _issue.Id,
            _issue.Number,
            _issue.Title,
            _issue.Status.ToString(),
            wrId,
            changeDir,
            workspacePath,
            wfStatus);
    }

    private void EnsureIssue()
    {
        if (_issue is null)
            throw new InvalidOperationException($"Issue '{GrainKey}' not found");
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "issue" : slug;
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
