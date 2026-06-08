using System.Text.Json;
using CloudNative.CloudEvents;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Grains;

public class IssueGrain : Grain, IIssueGrain
{
    private Domain.Issue? _issue;
    private readonly IStateStore<Domain.Issue> _issueStore;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly WorkflowQuerier _workflowQuerier;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueRepositoryResolver _repositoryResolver;
    private readonly IssueIdentityResolver _identityResolver;
    private readonly WorkflowProfileManager _workflowProfileManager;
    private readonly ProjectWorkflowProfileManager _projectProfileManager;
    private readonly IEventBus _eventBus;
    private readonly ILogger<IssueGrain> _log;
    private readonly List<IDisposable> _subscriptions = new();

    public IssueGrain(
        IStateStore<Domain.Issue> issueStore,
        IssueWorkflowProfileRegistry profiles,
        WorkflowQuerier workflowQuerier,
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueRepositoryResolver repositoryResolver,
        IssueIdentityResolver identityResolver,
        WorkflowProfileManager workflowProfileManager,
        ProjectWorkflowProfileManager projectProfileManager,
        IEventBus eventBus,
        ILogger<IssueGrain> log)
    {
        _issueStore = issueStore;
        _profiles = profiles;
        _workflowQuerier = workflowQuerier;
        _dbFactory = dbFactory;
        _repositoryResolver = repositoryResolver;
        _identityResolver = identityResolver;
        _workflowProfileManager = workflowProfileManager;
        _projectProfileManager = projectProfileManager;
        _eventBus = eventBus;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _issue = await _issueStore.LoadAsync(GrainKey);

        // Subscribe to workflow lifecycle events. Each handler filters by
        // the workflow run id extension and calls the same domain commands
        // the user-driven API paths call (CompleteWorkAsync / AbortWorkAsync).
        // The handlers run as fire-and-forget Action<CloudEvent> callbacks
        // so the bus dispatch never blocks the WorkflowGrain that emitted
        // the event; the issue grain queues the command message and
        // processes it when its single-threaded activation is free.
        _subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunCompleted, OnWorkflowCompleted));
        _subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunStopped, OnWorkflowStopped));
        _subscriptions.Add(_eventBus.OnType(EventCatalog.ReverseDns.WorkflowRunFailed, OnWorkflowFailed));
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { /* swallow — best effort during deactivation */ }
        }
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private async Task OnWorkflowCompleted(CloudEvent evt)
    {
        if (_issue is null) return;
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (wrId is null || wrId != _issue.ActiveWorkflowRunId) return;
        if (_issue.Status != Domain.IssueStatus.InProgress) return;
        await CompleteWorkAsync(wrId);
    }

    private async Task OnWorkflowStopped(CloudEvent evt)
    {
        if (_issue is null) return;
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (wrId is null || wrId != _issue.ActiveWorkflowRunId) return;
        if (_issue.Status != Domain.IssueStatus.InProgress) return;
        await AbortWorkAsync(wrId, TryGetExtension(evt, "reason") ?? "stopped");
    }

    private async Task OnWorkflowFailed(CloudEvent evt)
    {
        if (_issue is null) return;
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (wrId is null || wrId != _issue.ActiveWorkflowRunId) return;
        if (_issue.Status != Domain.IssueStatus.InProgress) return;
        await AbortWorkAsync(wrId, TryGetExtension(evt, "reason") ?? "failed");
    }

    private static string? TryGetExtension(CloudEvent evt, string name)
    {
        foreach (var (attr, value) in evt.GetPopulatedAttributes())
        {
            if (attr.IsExtension && attr.Name == name && value is not null)
            {
                return value.ToString();
            }
        }
        return null;
    }

    public async Task<string?> ResolveRepositoryRefAsync(string projectId, string? repositoryRef)
    {
        if (!string.IsNullOrWhiteSpace(repositoryRef))
            return repositoryRef;

        var projectGrain = GrainFactory.GetGrain<IProjectGrain>(projectId);
        var project = await projectGrain.GetAsync();
        return _repositoryResolver.Resolve(project, repositoryRef: null).Repository?.Name;
    }

    private async Task<IssueRepositoryResolution> ResolveIssueRepositoryAtStartAsync(Domain.Issue issue)
    {
        var projectGrain = GrainFactory.GetGrain<IProjectGrain>(issue.ProjectId);
        var project = await projectGrain.GetAsync();
        return _repositoryResolver.Resolve(project, issue.RepositoryRef);
    }

    private static IssueRepositoryResolution RequireResolvedRepository(IssueRepositoryResolution resolution)
    {
        if (resolution.HasProblem)
        {
            var problem = resolution.Problem!;
            throw new InvalidOperationException(
                $"Cannot start workflow: {problem.Message} (code={problem.Code}, repositoryRef={problem.RepositoryRef ?? "<none>"})");
        }
        return resolution;
    }

    public async Task<string> StartWorkAsync(WorkflowProjectContext? project = null)
    {
        EnsureIssue();
        var eligibility = await GetStartEligibilityAsync();
        if (!eligibility.Startable)
            throw new InvalidOperationException(eligibility.Message ?? "Issue is waiting for prerequisites");

        var issue = _issue!;

        var resolution = RequireResolvedRepository(await ResolveIssueRepositoryAtStartAsync(issue));
        var repo = resolution.Repository!;

        var wrId = $"wr_{Guid.NewGuid():N}";
        issue.StartWorkflow(wrId);

        var projectGrain = GrainFactory.GetGrain<IProjectGrain>(issue.ProjectId);
        var projectInfo = await projectGrain.GetAsync();
        var projectContext = BuildWorkflowProjectContext(issue, project, projectInfo, repo);

        if (string.IsNullOrWhiteSpace(await _projectProfileManager.GetDefaultTemplateAsync(projectContext.Id)))
            await _projectProfileManager.SetDefaultTemplateAsync(projectContext.Id, "mohist/default");

        var resolvedTemplate = await _workflowProfileManager.LoadTemplateAsync(wrId, projectContext.Id, issue.Id);
        var definition = resolvedTemplate.Structure ?? _profiles.Get(IssueWorkflowProfiles.DefaultId).Definition;

        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var mergedPrompts = defaultProfile is MohistDefaultIssueWorkflowProfile mohistDefaultProfile
            ? await mohistDefaultProfile.GetMergedPromptsAsync(issue.ProjectId)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        await EnsurePromptsReferencesResolveAsync(definition, mergedPrompts);

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StartAsync(input:
            new WorkflowStartInput(
                BuildVariables(wrId, issue, projectContext, mergedPrompts),
                ProjectId: projectContext.Id,
                IssueId: issue.Id));

        await SaveIssueAsync();
        _log.LogInformation("Issue {Key} started workflow {WrId}", GrainKey, wrId);
        return wrId;
    }

    private static WorkflowProjectContext BuildWorkflowProjectContext(
        Domain.Issue issue,
        WorkflowProjectContext? overrideContext,
        ProjectInfo? projectInfo,
        RepositoryInfo repo)
    {
        var projectId = overrideContext?.Id ?? projectInfo?.Id ?? issue.ProjectId;
        var projectName = overrideContext?.Name ?? projectInfo?.Name ?? issue.ProjectId;
        var projectPath = overrideContext?.Path
            ?? (string.IsNullOrWhiteSpace(repo.Path) ? projectInfo?.EffectivePath : repo.Path)
            ?? ".";
        var baseBranch = overrideContext?.BaseBranch
            ?? (string.IsNullOrWhiteSpace(repo.BaseBranch) ? projectInfo?.BaseBranch : repo.BaseBranch)
            ?? "main";

        return new WorkflowProjectContext(
            projectId,
            projectName,
            projectPath,
            baseBranch,
            repo.Name,
            repo.Remote,
            repo.Path,
            repo.BaseBranch);
    }

    public async Task CancelAsync()
    {
        EnsureIssue();
        if (_issue!.ActiveWorkflowRunId is { } wrId)
        {
            var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
            await wfGrain.StopAsync("issue-closed");
        }
        // Explicit close. The bus subscription below (workflow.run.stopped)
        // will also call AbortWorkAsync for the same Stopped event;
        // AbortWorkflow is idempotent (no-op if already Cancelled), so the
        // double dispatch is safe.
        _issue.Close();
        await SaveIssueAsync();
    }

    public async Task CompleteWorkAsync(string workflowRunId)
    {
        if (_issue is null) return;
        if (!_issue.Complete(workflowRunId)) return;
        await SaveIssueAsync();
    }

    public async Task AbortWorkAsync(string workflowRunId, string? reason)
    {
        if (_issue is null) return;
        if (!_issue.AbortWorkflow(workflowRunId)) return;
        await SaveIssueAsync();
    }

    public async Task UpdateAsync(string title, string? body)
    {
        EnsureIssue();
        _issue!.Update(title, body, null, null);
        await SaveIssueAsync();
    }

    public async Task ArchiveAsync()
    {
        EnsureIssue();
        _issue!.Archive();
        await SaveIssueAsync();
    }

    public async Task UnarchiveAsync()
    {
        EnsureIssue();
        _issue!.Unarchive();
        await SaveIssueAsync();
    }

    public async Task ReopenAsync()
    {
        EnsureIssue();
        _issue!.Reopen();
        await SaveIssueAsync();
    }

    public async Task UpdateFullAsync(UpdateIssueData data)
    {
        EnsureIssue();
        _issue!.Update(data.Title, data.Body, data.Labels, data.Priority);
        await SaveIssueAsync();
    }

    public async Task<IssueWorkflowStatus?> GetWorkflowStatusAsync()
    {
        EnsureIssue();

        var wrId = _issue!.ActiveWorkflowRunId;
        if (wrId is null) return null;

        var wfStatus = await _workflowQuerier.GetStatusAsync(wrId);

        // Lazy reconciliation (Step 4 of design/event-mechanism.md): if the
        // bus subscription missed the terminal event (grain was deactivated
        // at emit time, or the event was lost across a silo restart before
        // the outbox), the read path here is the next chance to bring the
        // issue in sync with the workflow's actual state. Issuing the same
        // command the bus handler would issue (CompleteWorkAsync /
        // AbortWorkAsync) keeps the recovery idempotent.
        await ReconcileWithWorkflowTerminalStateAsync(wrId, wfStatus);

        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var projection = defaultProfile.ProjectWorkflowState(_issue, wfStatus);

        return new IssueWorkflowStatus(
            _issue.Id,
            _issue.Number,
            _issue.Title,
            projection.IssueStatus,
            projection.Health,
            wrId,
            projection.ChangeDir,
            null,
            wfStatus);
    }

    private async Task ReconcileWithWorkflowTerminalStateAsync(string workflowRunId, WorkflowStatusView? wfStatus)
    {
        if (_issue is null || wfStatus is null) return;
        if (_issue.Status != Domain.IssueStatus.InProgress) return;
        if (_issue.ActiveWorkflowRunId != workflowRunId) return;

        switch (wfStatus.Status)
        {
            case "Completed":
                await CompleteWorkAsync(workflowRunId);
                break;
            case "Failed":
                await AbortWorkAsync(workflowRunId, wfStatus.Failure?.Message ?? "failed");
                break;
            case "Stopped":
                await AbortWorkAsync(workflowRunId, "stopped");
                break;
        }
    }

    public async Task<string> CreateAsync(string projectId, int number, string title, string? body, string[]? labels, string? priority, string? repositoryRef = null, string? issueId = null)
    {
        if (_issue is not null)
            throw new InvalidOperationException($"Issue '{GrainKey}' already exists");

        var resolvedRef = await ResolveRepositoryRefAsync(projectId, repositoryRef);

        var issue = Domain.Issue.Create(
            issueId ?? $"issue_{Guid.NewGuid():N}",
            projectId,
            number,
            title,
            body,
            labels,
            priority ?? "p2",
            resolvedRef);

        _issue = issue;
        await SaveIssueAsync();
        return issue.Id;
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
        await SaveIssueAsync();
        return IssuePrerequisiteResult.Added();
    }

    public async Task RemovePrerequisiteAsync(int prerequisiteNumber)
    {
        EnsureIssue();
        _issue!.RemovePrerequisite(prerequisiteNumber);
        await SaveIssueAsync();
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

    private string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project, IReadOnlyDictionary<string, string> prompts)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { system = "mohist", runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number, title = issue.Title, body = issue.Body ?? "" }, WorkflowVariableJson.Options),
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name, path = project.Path, baseBranch = project.BaseBranch, defaultBranch = project.BaseBranch }, WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(new { name = project.RepositoryName, path = project.RepositoryPath, remote = project.RepositoryRemote, baseBranch = project.RepositoryBaseBranch ?? project.BaseBranch }, WorkflowVariableJson.Options),
            ["openspecChangeName"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeName(issue.Number), WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
            ["prompts"] = JsonSerializer.SerializeToElement(prompts, WorkflowVariableJson.Options),
        };

        return JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
    }

    private async Task EnsurePromptsReferencesResolveAsync(Workflow.Domain.Definition.WorkflowDefinition definition, IReadOnlyDictionary<string, string> mergedPrompts)
    {
        var referencedKeys = PromptReferenceScanner.Scan(definition);
        if (referencedKeys.Count == 0) return;

        var missing = new List<string>();
        foreach (var key in referencedKeys)
        {
            var topLevel = key.Split('.', 2)[0];
            if (!mergedPrompts.ContainsKey(topLevel))
                missing.Add(key);
        }
        if (missing.Count > 0)
        {
            throw new MissingPromptsException(missing);
        }
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

    private async Task SaveIssueAsync()
    {
        if (_issue is null) return;
        await _issueStore.SaveAsync(_issue.Id, _issue);
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
            var issueId = await _identityResolver.GetIdAsync(_issue.ProjectId, issueNumber);
            if (issueId is null) return null;
            var issue = await _issueStore.LoadAsync(issueId);
            return issue is null ? null : IssuePrerequisiteSummary.FromDomain(issue);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<IssueCommentResult> AddCommentAsync(string body)
    {
        if (_issue is null) throw new InvalidOperationException("Issue not found");

        var comment = new IssueCommentRow
        {
            Id = $"cmt_{Guid.NewGuid():N}",
            ProjectId = _issue.ProjectId,
            IssueId = _issue.Id,
            IssueNumber = _issue.Number,
            Body = body,
        };

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.IssueComments.Add(comment);
        await db.SaveChangesAsync();

        return new IssueCommentResult(comment.Id, comment.Body);
    }

}

[GenerateSerializer]
public record UpdateIssueData(
    [property: Id(0)] string? Title = null,
    [property: Id(1)] string? Body = null,
    [property: Id(2)] string[]? Labels = null,
    [property: Id(3)] string? Priority = null
);
