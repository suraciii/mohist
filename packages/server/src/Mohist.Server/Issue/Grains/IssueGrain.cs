using System.Text.Json;
using Mohist.Server.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Project.Queries;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Grains;

public class IssueGrain : Grain, IIssueGrain
{
    private IssueAggregate? _aggregate;
    private readonly IssueStateStore _issueStore;
    private readonly IEventStore _events;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly Config.ConfigService _config;
    private readonly ILogger<IssueGrain> _log;

    public IssueGrain(IssueStateStore issueStore, IEventStore events, IssueWorkflowProfileRegistry profiles, Config.ConfigService config, ILogger<IssueGrain> log)
    {
        _issueStore = issueStore;
        _events = events;
        _profiles = profiles;
        _config = config;
        _log = log;
    }

    private Issue.Domain.Issue? Issue => _aggregate?.Issue;
    private IssueWorkflowProfile? Profile => _aggregate?.Profile;
    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _aggregate = await _issueStore.LoadAsync(GrainKey);
    }

    public async Task<string> StartWorkAsync(WorkflowProjectContext? project = null)
    {
        EnsureIssue();
        var eligibility = await GetStartEligibilityAsync();
        if (!eligibility.Startable)
            throw new InvalidOperationException(eligibility.Message ?? "Issue is waiting for prerequisites");

        var issue = Issue!;
        var wrId = $"wr_{Guid.NewGuid():N}";
        issue.StartWorkflow(wrId);

        var repo = issue.Repository;
        var projectId = project?.Id ?? issue.ProjectId;
        var projectName = project?.Name ?? issue.ProjectId;
        var projectPath = repo?.Path ?? project?.Path ?? ".";
        var baseBranch = repo?.BaseBranch ?? project?.BaseBranch ?? "main";
        var correlation = new WorkflowCorrelationContext(projectId, "issue", issue.Id, issue.Number);
        var projectContext = new WorkflowProjectContext(
            projectId,
            projectName,
            projectPath,
            baseBranch,
            repo?.Name,
            repo?.Remote,
            repo?.Path,
            repo?.BaseBranch);

        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var definition = Profile?.Definition ?? defaultProfile.Definition;
        var stageVariables = BuildStageVariablesFromDefinition(definition);

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StartAsync(
            definition,
            correlation,
            new WorkflowStartInput(
                BuildVariables(wrId, issue, projectContext, definition),
                stageVariables));

        await SaveAsync();
        await AppendIssueEventAsync("issue_started", "workflow-started", "Issue workflow started", new { workflowRunId = wrId });
        _log.LogInformation("Issue {Key} started workflow {WrId}", GrainKey, wrId);
        return wrId;
    }

    public async Task CancelAsync()
    {
        EnsureIssue();
        if (Issue!.WorkflowRunId is { } wrId)
        {
            var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
            await wfGrain.PauseAsync("issue-closed");
        }
        Issue.Close();
        await SaveAsync();
        await AppendIssueEventAsync("issue_closed", "closed", "Issue closed");
    }

    public async Task CompleteWorkAsync(string workflowRunId)
    {
        if (Issue is null) return;
        if (Issue.WorkflowRunId != workflowRunId) return;
        if (Issue.Stage == IssueStage.Done) return;

        Issue.Complete();
        await SaveAsync();
        await AppendIssueEventAsync("issue_completed", "completed", "Issue completed", new { workflowRunId });
    }

    public async Task UpdateAsync(string title, string? body)
    {
        EnsureIssue();
        Issue!.Update(title, body, null, null);
        await SaveAsync();
        await AppendIssueEventAsync("issue_updated", "updated", "Issue updated");
    }

    public async Task ArchiveAsync()
    {
        EnsureIssue();
        Issue!.Archive();
        await SaveAsync();
        await AppendIssueEventAsync("issue_archived", "archived", "Issue archived");
    }

    public async Task UnarchiveAsync()
    {
        EnsureIssue();
        Issue!.Unarchive();
        await SaveAsync();
        await AppendIssueEventAsync("issue_unarchived", "active", "Issue unarchived");
    }

    public async Task ReopenAsync()
    {
        EnsureIssue();
        Issue!.Reopen();
        await SaveAsync();
        await AppendIssueEventAsync("issue_reopened", "active", "Issue reopened");
    }

    public async Task UpdateFullAsync(UpdateIssueData data)
    {
        EnsureIssue();
        Issue!.Update(data.Title, data.Body, data.Labels, data.Priority);
        await SaveAsync();
        await AppendIssueEventAsync("issue_updated", "updated", "Issue updated");
    }

    public async Task<IssueWorkflowStatus?> GetWorkflowStatusAsync()
    {
        EnsureIssue();

        var wrId = Issue!.WorkflowRunId;
        if (wrId is null) return null;

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        var wfStatus = await wfGrain.GetStatusAsync();
        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var projection = defaultProfile.ProjectWorkflowState(Issue, wfStatus);

        return new IssueWorkflowStatus(
            Issue.Id,
            Issue.Number,
            Issue.Title,
            projection.IssueStage,
            projection.RuntimeStatus,
            wrId,
            projection.ChangeDir,
            null,
            wfStatus);
    }

    public async Task CreateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, RepositoryInfo? repository = null)
    {
        if (_aggregate is not null)
            throw new InvalidOperationException($"Issue '{GrainKey}' already exists");

        var issue = new Issue.Domain.Issue(
            $"issue_{Guid.NewGuid():N}",
            projectId,
            number,
            title,
            body,
            labels,
            priority ?? "p2",
            repository);

        var globalAgentConfig = await _config.GetAgentConfigAsync();
        var globalStageAgentConfigs = await _config.GetStageAgentConfigsAsync();
        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var profile = IssueWorkflowProfile.CopyFrom(
            IssueWorkflowProfiles.DefaultId,
            defaultProfile.Definition,
            globalAgentConfig,
            globalStageAgentConfigs);

        _aggregate = new IssueAggregate(issue, profile);
        await SaveAsync();
        await AppendIssueEventAsync("issue_created", "created", "Issue created", new { title, priority = priority ?? "p2", labels = labels ?? [] });
    }

    public async Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber)
    {
        if (Issue is null)
            return IssuePrerequisiteResult.IssueNotFound();
        if (prerequisiteNumber == Issue.Number)
            return IssuePrerequisiteResult.Circular();
        if (await LoadIssueSummaryAsync(prerequisiteNumber) is null)
            return IssuePrerequisiteResult.PrerequisiteNotFound(prerequisiteNumber);

        Issue.AddPrerequisite(prerequisiteNumber);
        await SaveAsync();
        await AppendIssueEventAsync("issue_prerequisite_added", "updated", $"Prerequisite #{prerequisiteNumber} added", new { prerequisiteNumber });
        return IssuePrerequisiteResult.Added();
    }

    public async Task RemovePrerequisiteAsync(int prerequisiteNumber)
    {
        EnsureIssue();
        Issue!.RemovePrerequisite(prerequisiteNumber);
        await SaveAsync();
        await AppendIssueEventAsync("issue_prerequisite_removed", "updated", $"Prerequisite #{prerequisiteNumber} removed", new { prerequisiteNumber });
    }

    public async Task<IssueStartEligibility> GetStartEligibilityAsync()
    {
        EnsureIssue();
        var prerequisites = new List<IssuePrerequisiteSummary>();
        foreach (var prerequisiteNumber in Issue!.PrerequisiteNumbers)
        {
            var summary = await LoadIssueSummaryAsync(prerequisiteNumber);
            if (summary is not null)
                prerequisites.Add(summary);
        }

        return IssueStartEligibility.FromPrerequisites(prerequisites.ToArray());
    }

    private string BuildVariables(string workflowRunId, Issue.Domain.Issue issue, WorkflowProjectContext project, Workflow.Domain.Definition.WorkflowDefinition definition)
    {
        var profile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var prompts = profile is MohistDefaultIssueWorkflowProfile defaultProfile
            ? defaultProfile.LoadPrompts()
            : new Dictionary<string, string>();

        var repo = issue.Repository;
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name, path = project.Path, baseBranch = project.BaseBranch, defaultBranch = project.BaseBranch }, WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(new { name = repo?.Name, path = repo?.Path, remote = repo?.Remote, baseBranch = repo?.BaseBranch }, WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeName(issue.Number), WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
            ["prompts"] = JsonSerializer.SerializeToElement(prompts, WorkflowVariableJson.Options),
        };

        if (definition.Variables is not null)
            variables["vars"] = JsonSerializer.SerializeToElement(
                definition.Variables.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? JsonSerializer.Deserialize<object?>(kv.Value.Value.GetRawText(), WorkflowVariableJson.Options) : null),
                WorkflowVariableJson.Options);

        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
    }

    private static Dictionary<string, Dictionary<string, string>>? BuildStageVariablesFromDefinition(Workflow.Domain.Definition.WorkflowDefinition definition)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in definition.Stages)
        {
            if (stage.Variables is null || stage.Variables.Count == 0) continue;
            result[stage.Stage] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vars"] = JsonSerializer.Serialize(
                    stage.Variables.ToDictionary(kv => kv.Key, kv => kv.Value.HasValue ? JsonSerializer.Deserialize<object?>(kv.Value.Value.GetRawText(), WorkflowVariableJson.Options) : null),
                    WorkflowVariableJson.Options)
            };
        }
        return result.Count == 0 ? null : result;
    }

    private async Task SaveAsync()
    {
        if (_aggregate is null) return;
        await _issueStore.SaveAsync(GrainKey, _aggregate);
    }

    private Task AppendIssueEventAsync(string type, string? status, string? message, object? payload = null)
    {
        if (Issue is null) return Task.CompletedTask;
        return _events.AppendAsync(new EventInput(
            Issue.ProjectId,
            Issue.Number,
            "issue",
            type,
            IssueId: Issue.Id,
            WorkflowRunId: Issue.WorkflowRunId,
            Stage: IssueDomainNames.Stage(Issue.Stage),
            Status: status,
            Message: message,
            Payload: payload));
    }

    private void EnsureIssue()
    {
        if (Issue is null)
            throw new InvalidOperationException($"Issue '{GrainKey}' not found");
    }

    private async Task<IssuePrerequisiteSummary?> LoadIssueSummaryAsync(int issueNumber)
    {
        if (Issue is null) return null;
        try
        {
            var aggregate = await _issueStore.LoadAsync($"{Issue.ProjectId}:{issueNumber}");
            return aggregate is null ? null : ToPrerequisiteSummary(aggregate.Issue);
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

    private static string IssueRuntimeSummary(IssueStage status, IssueAttention? attention) =>
        status switch
        {
            IssueStage.Done => "done",
            IssueStage.Cancelled => "cancelled",
            _ when attention?.Reason is IssueAttentionReasons.Blocked or IssueAttentionReasons.WorkflowFailed => "blocked",
            _ when attention is not null => "attention",
            _ => "active",
        };
}

[GenerateSerializer]
public record UpdateIssueData(
    [property: Id(0)] string? Title = null,
    [property: Id(1)] string? Body = null,
    [property: Id(2)] string[]? Labels = null,
    [property: Id(3)] string? Priority = null
);
