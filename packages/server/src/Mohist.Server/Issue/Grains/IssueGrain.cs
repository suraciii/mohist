using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Storage;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public class IssueGrain : Grain, IIssueGrain
{
    private Issue.Domain.Issue? _issue;
    private readonly IStateStore<Issue.Domain.Issue> _issueStore;
    private readonly IEventStore _events;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly ILogger<IssueGrain> _log;

    public IssueGrain(IStateStore<Issue.Domain.Issue> issueStore, IEventStore events, IssueWorkflowProfileRegistry profiles, ILogger<IssueGrain> log)
    {
        _issueStore = issueStore;
        _events = events;
        _profiles = profiles;
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

        var wrId = $"wr_{Guid.NewGuid():N}";
        _issue!.StartWorkflow(wrId);
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_started", "workflow-started", "Issue workflow started", new { workflowRunId = wrId });

        var projectId = project?.Id ?? _issue.ProjectId;
        var projectName = project?.Name ?? _issue.ProjectId;
        var projectPath = project?.Path ?? ".";
        var baseBranch = project?.BaseBranch ?? "main";
        var correlation = new WorkflowCorrelationContext(projectId, "issue", _issue.Id, _issue.Number);
        var projectContext = new WorkflowProjectContext(projectId, projectName, projectPath, baseBranch);
        var profile = _profiles.Get(_issue.WorkflowProfileId);

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StartAsync(
            profile.Definition,
            correlation,
            new WorkflowStartInput(profile.BuildVariables(wrId, _issue, projectContext)));

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

    public async Task CompleteWorkflowAsync(string workflowRunId)
    {
        if (_issue is null) return;
        if (_issue!.WorkflowRunId != workflowRunId) return;
        if (_issue.Stage == IssueStage.Done) return;

        _issue.Complete();
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_completed", "completed", "Issue completed", new { workflowRunId });
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
        var projection = _profiles.Get(_issue.WorkflowProfileId).ProjectWorkflowState(_issue, wfStatus);

        return new IssueWorkflowStatus(
            _issue.Id,
            _issue.Number,
            _issue.Title,
            projection.IssueStage,
            projection.RuntimeStatus,
            wrId,
            projection.ChangeDir,
            null,
            wfStatus);
    }

    public async Task HydrateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, string? model = null, Dictionary<string, string>? stageModels = null, string? workflowProfileId = null)
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
            stageModels,
            workflowProfileId);
        await _issueStore.SaveAsync(GrainKey, _issue);
        await AppendIssueEventAsync("issue_created", "created", "Issue created", new { title, priority = priority ?? "p2", labels = labels ?? [] });
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
            Stage: IssueDomainNames.Stage(_issue.Stage),
            Status: status,
            Message: message,
            Payload: payload));
    }

    private void EnsureIssue()
    {
        if (_issue is null)
            throw new InvalidOperationException($"Issue '{GrainKey}' not found");
    }

    private static string IssueRuntimeSummary(IssueStage status, IssueAttention? attention) =>
        status switch
        {
            IssueStage.Done => "done",
            IssueStage.Cancelled => "cancelled",
            _ when attention?.Reason is IssueAttentionReasons.Blocked or IssueAttentionReasons.WorkflowFailed => "blocked",
            _ when attention is not null => "attention",
            _ => "active",
        };

    private async Task<IssuePrerequisiteSummary?> LoadIssueSummaryAsync(int issueNumber)
    {
        if (_issue is null) return null;
        try
        {
            var prerequisite = await _issueStore.LoadAsync($"{_issue.ProjectId}:{issueNumber}");
            return prerequisite is null ? null : ToPrerequisiteSummary(prerequisite);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IssuePrerequisiteSummary ToPrerequisiteSummary(Issue.Domain.Issue issue) => new()
    {
        IssueId = issue.Id,
        Number = issue.Number,
        Title = issue.Title,
        Completed = issue.Stage == IssueStage.Done,
        Stage = IssueDomainNames.Stage(issue.Stage),
        Status = IssueRuntimeSummary(issue.Stage, issue.Attention),
    };

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
