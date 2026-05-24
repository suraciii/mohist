using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Storage;
using Mohist.Server.Workflow.Grains;
using System.Text.Json;

namespace Mohist.Server.Issue.Grains;

public class IssueGrain : Grain, IIssueGrain
{
    private Issue.Domain.Issue? _issue;
    private readonly IStateStore<Issue.Domain.Issue> _issueStore;
    private readonly IEventStore _events;
    private readonly ILogger<IssueGrain> _log;

    public IssueGrain(IStateStore<Issue.Domain.Issue> issueStore, IEventStore events, ILogger<IssueGrain> log)
    {
        _issueStore = issueStore;
        _events = events;
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
        var eligibility = await GetStartEligibilityAsync();
        if (!eligibility.Startable)
            throw new InvalidOperationException(eligibility.Message ?? "Issue is waiting for prerequisites");

        _issue!.SetStage(IssueStage.Plan);
        _issue.SetRuntimeStatus(IssueRuntimeStatus.Active);
        var wrId = $"wr_{Guid.NewGuid():N}";
        _issue.SetWorkflowRunId(wrId);
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_started", "workflow-started", "Issue workflow started", new { workflowRunId = wrId });

        var projectId = project?.Id ?? _issue.ProjectId;
        var projectName = project?.Name ?? _issue.ProjectId;
        var projectPath = project?.Path ?? ".";
        var baseBranch = project?.BaseBranch ?? "main";
        var correlation = new WorkflowCorrelationContext(projectId, "issue", _issue.Id, _issue.Number);

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StartAsync(
            MohistPipeline.Definition,
            correlation,
            new WorkflowStartInput(BuildWorkflowVariables(wrId, _issue, projectId, projectName, projectPath, baseBranch)));

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
        await AppendIssueEventAsync("issue_closed", "closed", "Issue closed");
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
        await AppendIssueEventAsync("issue_updated", "updated", "Issue updated");
    }

    public async Task ArchiveAsync()
    {
        EnsureIssue();
        _issue!.Archive();
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_archived", "archived", "Issue archived");
    }

    public async Task UnarchiveAsync()
    {
        EnsureIssue();
        _issue!.Unarchive();
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_unarchived", "active", "Issue unarchived");
    }

    public async Task ReopenAsync()
    {
        EnsureIssue();
        _issue!.Reopen();
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_reopened", "active", "Issue reopened");
    }

    public async Task UpdateFullAsync(UpdateIssueData data)
    {
        EnsureIssue();
        _issue!.Update(data.Title, data.Body, data.Labels, data.Priority, data.Model, data.StageModels);
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_updated", "updated", "Issue updated");
    }

    public async Task<IssueWorkflowStatus?> GetWorkflowStatusAsync()
    {
        EnsureIssue();

        var wrId = _issue!.WorkflowRunId;
        if (wrId is null) return null;

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        var wfStatus = await wfGrain.GetStatusAsync();

        return new IssueWorkflowStatus(
            _issue.Id,
            _issue.Number,
            _issue.Title,
            ResolveWorkflowStage(wfStatus, _issue.Stage.ToString().ToLower()),
            ResolveWorkflowRuntimeStatus(wfStatus, _issue.RuntimeStatus.ToString().ToLower()),
            wrId,
            wfStatus?.ChangeDir,
            null,
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
        await AppendIssueEventAsync("issue_created", "created", "Issue created", new { title, priority = priority ?? "p2", labels = labels ?? [] });
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
            PrerequisiteNumbers = _issue.PrerequisiteNumbers,
        };
        return Task.FromResult(info);
    }

    public async Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber)
    {
        if (_issue is null)
            return IssuePrerequisiteResult.IssueNotFound();
        if (prerequisiteNumber == _issue.Number)
            return IssuePrerequisiteResult.Circular();
        if (await LoadIssueSummaryAsync(prerequisiteNumber) is null)
            return IssuePrerequisiteResult.PrerequisiteNotFound(prerequisiteNumber);

        _issue.AddPrerequisite(prerequisiteNumber);
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_prerequisite_added", "updated", $"Prerequisite #{prerequisiteNumber} added", new { prerequisiteNumber });
        return IssuePrerequisiteResult.Added();
    }

    public async Task RemovePrerequisiteAsync(int prerequisiteNumber)
    {
        EnsureIssue();
        _issue!.RemovePrerequisite(prerequisiteNumber);
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_prerequisite_removed", "updated", $"Prerequisite #{prerequisiteNumber} removed", new { prerequisiteNumber });
    }

    public async Task<IssueStartEligibility> GetStartEligibilityAsync()
    {
        EnsureIssue();
        var prerequisites = new List<IssuePrerequisiteSummary>();
        foreach (var prerequisiteNumber in _issue!.PrerequisiteNumbers)
        {
            var summary = await LoadIssueSummaryAsync(prerequisiteNumber);
            if (summary is not null)
                prerequisites.Add(summary);
        }

        return IssueStartEligibility.FromPrerequisites(prerequisites.ToArray());
    }

    private Task AppendIssueEventAsync(string type, string? status, string? message, object? payload = null)
    {
        if (_issue is null) return Task.CompletedTask;
        return _events.AppendAsync(new EventInput(
            _issue.ProjectId,
            _issue.Number,
            "issue",
            type,
            IssueId: _issue.Id,
            WorkflowRunId: _issue.WorkflowRunId,
            Stage: _issue.Stage.ToString().ToLower(),
            Status: status,
            Message: message,
            Payload: payload));
    }

    private void EnsureIssue()
    {
        if (_issue is null)
            throw new InvalidOperationException($"Issue '{GrainKey}' not found");
    }

    private async Task<IssuePrerequisiteSummary?> LoadIssueSummaryAsync(int issueNumber)
    {
        if (_issue is null) return null;
        try
        {
            var info = await GrainFactory.GetGrain<IIssueGrain>($"{_issue.ProjectId}:{issueNumber}").GetInfoAsync();
            return ToPrerequisiteSummary(info);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IssuePrerequisiteSummary ToPrerequisiteSummary(IssueInfo issue) => new()
    {
        IssueId = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Delivered = issue.Stage == "done" || issue.Status == "completed" || issue.MergeState == "merged",
        Stage = issue.Stage,
        Status = issue.Status,
        MergeState = issue.MergeState,
    };

    private static string ResolveWorkflowStage(WorkflowStatusSnapshot? workflow, string fallback) => workflow?.Status switch
    {
        "Passed" => "done",
        null => fallback,
        _ => workflow.CurrentStage ?? fallback,
    };

    private static string ResolveWorkflowRuntimeStatus(WorkflowStatusSnapshot? workflow, string fallback) => workflow?.Status switch
    {
        "Passed" => "completed",
        "Failed" => "blocked",
        "Paused" => "paused",
        null => fallback,
        _ => "active",
    };

    private static string BuildWorkflowVariables(
        string workflowRunId,
        Issue.Domain.Issue issue,
        string projectId,
        string projectName,
        string projectPath,
        string baseBranch)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = projectId, name = projectName, path = projectPath, baseBranch, defaultBranch = baseBranch }, WorkflowVariableJson.Options),
            ["artifacts"] = JsonSerializer.SerializeToElement(new { changeDir = $"openspec/changes/{issue.Number}-{Slug(issue.Title)}" }, WorkflowVariableJson.Options),
            ["model"] = JsonSerializer.SerializeToElement(new { @default = issue.Model ?? "", stage = issue.StageModels ?? new Dictionary<string, string>() }, WorkflowVariableJson.Options),
            ["vars"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>(), WorkflowVariableJson.Options),
        };
        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "issue" : slug;
    }

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
