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

    public async Task<string> StartWorkflowAsync(WorkflowProjectContext? project = null)
    {
        EnsureIssue();
        _issue!.SetStage(IssueStage.Plan);
        _issue.SetRuntimeStatus(IssueRuntimeStatus.Active);
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
        var projectId = project?.Id ?? _issue.ProjectId;
        var projectName = project?.Name ?? _issue.ProjectId;
        var projectPath = project?.Path ?? ".";
        var baseBranch = project?.BaseBranch ?? "main";

        await variables.SetContextAsync("project", $$"""
        {
          "id": {{JsonString(projectId)}},
          "name": {{JsonString(projectName)}},
          "path": {{JsonString(projectPath)}},
          "baseBranch": {{JsonString(baseBranch)}},
          "defaultBranch": {{JsonString(baseBranch)}}
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
        _issue!.Update(title, body, null, null, null, null);
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public async Task ArchiveAsync()
    {
        EnsureIssue();
        _issue!.Archive();
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public async Task UnarchiveAsync()
    {
        EnsureIssue();
        _issue!.Unarchive();
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public async Task ReopenAsync()
    {
        EnsureIssue();
        _issue!.Reopen();
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public async Task UpdateFullAsync(UpdateIssueData data)
    {
        EnsureIssue();
        _issue!.Update(data.Title, data.Body, data.Labels, data.Priority, data.Model, data.StageModels);
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
        catch { }

        return new IssueWorkflowStatus(
            _issue.Id,
            _issue.Number,
            _issue.Title,
            _issue.Stage.ToString().ToLower(),
            _issue.RuntimeStatus.ToString().ToLower(),
            wrId,
            changeDir,
            workspacePath,
            wfStatus);
    }

    public async Task HydrateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, string? model = null, Dictionary<string, string>? stageModels = null)
    {
        if (_issue is not null)
            throw new InvalidOperationException($"Issue '{GrainKey}' already exists");

        _issue = new Issue.Domain.Issue(
            $"issue_{Guid.NewGuid():N}",
            projectId,
            number,
            title,
            body,
            labels,
            priority ?? "p2",
            model,
            stageModels);
        await _issueStore.SaveAsync(GrainKey, _issue);
    }

    public Task<IssueInfo> GetInfoAsync()
    {
        EnsureIssue();
        var info = new IssueInfo
        {
            Id = _issue!.Id,
            Number = _issue.Number,
            Title = _issue.Title,
            Body = _issue.Body,
            Stage = _issue.Stage.ToString().ToLower(),
            Status = _issue.RuntimeStatus.ToString().ToLower(),
            ProjectId = _issue.ProjectId,
            Labels = _issue.Labels,
            Priority = _issue.Priority,
            Model = _issue.Model,
            StageModels = _issue.StageModels,
            CreatedAt = _issue.CreatedAt.ToString("o"),
            UpdatedAt = _issue.UpdatedAt.ToString("o"),
            ArchivedAt = _issue.ArchivedAt?.ToString("o"),
            ApprovalState = _issue.ApprovalState,
            MergeState = _issue.MergeState?.ToString().ToLower(),
            RetryCount = _issue.RetryCount,
            ConflictRetryCount = _issue.ConflictRetryCount,
            BlockedReason = _issue.BlockedReason,
            WorkflowRunId = _issue.WorkflowRunId,
        };
        return Task.FromResult(info);
    }

    public Task SetStageAsync(string stage)
    {
        EnsureIssue();
        if (Enum.TryParse<IssueStage>(stage, true, out var s))
            _issue!.SetStage(s);
        return Task.CompletedTask;
    }

    public Task SetRuntimeStatusAsync(string status, string? reason = null)
    {
        EnsureIssue();
        if (Enum.TryParse<IssueRuntimeStatus>(status, true, out var s))
            _issue!.SetRuntimeStatus(s, reason);
        return Task.CompletedTask;
    }

    public Task SetApprovalStateAsync(ApprovalState? state)
    {
        EnsureIssue();
        _issue!.SetApprovalState(state);
        return Task.CompletedTask;
    }

    public Task SetMergeStateAsync(string? state)
    {
        EnsureIssue();
        if (state == null)
            _issue!.SetMergeState(null);
        else if (Enum.TryParse<MergeState>(state, true, out var s))
            _issue!.SetMergeState(s);
        return Task.CompletedTask;
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

[GenerateSerializer]
public record UpdateIssueData(
    [property: Id(0)] string? Title = null,
    [property: Id(1)] string? Body = null,
    [property: Id(2)] string[]? Labels = null,
    [property: Id(3)] string? Priority = null,
    [property: Id(4)] string? Model = null,
    [property: Id(5)] Dictionary<string, string>? StageModels = null
);
