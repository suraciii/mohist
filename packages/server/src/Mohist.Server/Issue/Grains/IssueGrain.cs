using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Mohist.Server.SystemInfo;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Infrastructure.Data.Events;
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
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
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
    private readonly IssueWorkflowProfileManager _issueProfileManager;
    private readonly IEventStore _eventStore;
    private readonly IEventPublisher _eventBus;
    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly ILogger<IssueGrain> _log;

    public IssueGrain(
        IStateStore<Domain.Issue> issueStore,
        IssueWorkflowProfileRegistry profiles,
        WorkflowQuerier workflowQuerier,
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueRepositoryResolver repositoryResolver,
        IssueIdentityResolver identityResolver,
        WorkflowProfileManager workflowProfileManager,
        ProjectWorkflowProfileManager projectProfileManager,
        IssueWorkflowProfileManager issueProfileManager,
        IEventStore eventStore,
        IEventPublisher eventBus,
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
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
        _issueProfileManager = issueProfileManager;
        _eventStore = eventStore;
        _eventBus = eventBus;
        _configuration = configuration;
        _environment = environment;
        _log = log;
    }

    private string GrainKey => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _issue = await _issueStore.LoadAsync(GrainKey);
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        return Task.CompletedTask;
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

        var reusedRunId = await TryReuseActiveWorkflowAsync();
        if (reusedRunId is not null)
            return reusedRunId;

        return await StartWorkflowAsync(project);
    }

    private async Task<string> StartWorkflowAsync(WorkflowProjectContext? project)
    {
        var issue = _issue!;

        var resolution = RequireResolvedRepository(await ResolveIssueRepositoryAtStartAsync(issue));
        var repo = resolution.Repository!;

        var wrId = $"wr_{Guid.NewGuid():N}";
        issue.StartWorkflow(wrId);

        var projectGrain = GrainFactory.GetGrain<IProjectGrain>(issue.ProjectId);
        var projectInfo = await projectGrain.GetAsync();
        var projectContext = BuildWorkflowProjectContext(issue, project, projectInfo, repo);
        var workspace = BuildWorkspaceIdentity(issue, projectContext, wrId);

        if (string.IsNullOrWhiteSpace(await _projectProfileManager.GetDefaultTemplateAsync(projectContext.Id)))
            await _projectProfileManager.SetDefaultTemplateAsync(projectContext.Id, "mohist/default");

        var resolvedTemplate = await _workflowProfileManager.LoadTemplateAsync(wrId, projectContext.Id, issue.Id);
        var definition = resolvedTemplate.Structure ?? _profiles.Get(IssueWorkflowProfiles.DefaultId).Definition;

        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.DefaultId);
        var mergedPrompts = defaultProfile is MohistDefaultIssueWorkflowProfile mohistDefaultProfile
            ? await mohistDefaultProfile.GetMergedPromptsAsync(issue.ProjectId)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        await EnsurePromptsReferencesResolveAsync(definition, mergedPrompts);

        await _issueProfileManager.PatchVariablesAsync(issue.Id, BuildIssueVariables(wrId, issue, projectContext, workspace));

        foreach (var (key, body) in mergedPrompts)
            await _issueProfileManager.SetPromptAsync(issue.Id, key, body);

        var wfGrain = GrainFactory.GetGrain<IWorkflowGrain>(wrId);
        await wfGrain.StartAsync(input:
            new WorkflowStartInput(
                Metadata: new WorkflowRunMetadata(
                    Name: null,
                    CreatedAt: DateTimeOffset.UtcNow,
                    Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["projectId"] = projectContext.Id,
                        ["issueId"] = issue.Id,
                        ["issueNumber"] = issue.Number.ToString(),
                    }),
                Workspace: workspace));

        await SaveIssueAsync();
        _log.LogInformation("Issue {Key} started workflow {WrId}", GrainKey, wrId);
        return wrId;
    }

    private async Task<string?> TryReuseActiveWorkflowAsync()
    {
        var issue = _issue!;
        var activeWorkflowRunId = issue.ActiveWorkflowRunId;
        if (activeWorkflowRunId is null) return null;

        try
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(activeWorkflowRunId);
            if (await workflow.IsStoppedOrTerminalAsync())
            {
                issue.ClearStoppedWorkflow(activeWorkflowRunId);
                await SaveIssueAsync();
                return null;
            }

            _log.LogInformation("Issue {IssueId} reusing active workflow {WorkflowRunId}", issue.Id, activeWorkflowRunId);
            return activeWorkflowRunId;
        }
        catch (Exception ex) when (IsWorkflowRunStateCorruption(ex))
        {
            _log.LogWarning(ex,
                "Issue {IssueId} active workflow {WorkflowRunId} cannot be loaded while starting; clearing active workflow reference",
                issue.Id,
                activeWorkflowRunId);
            issue.ClearStoppedWorkflow(activeWorkflowRunId);
            await SaveIssueAsync();
            return null;
        }
    }

    private static bool IsWorkflowRunStateCorruption(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains("Failed to deserialize workflow run state", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static WorkflowProjectContext BuildWorkflowProjectContext(
        Domain.Issue issue,
        WorkflowProjectContext? overrideContext,
        ProjectInfo? projectInfo,
        RepositoryInfo repo)
    {
        var projectId = overrideContext?.Id ?? projectInfo?.Id ?? issue.ProjectId;
        var projectName = overrideContext?.Name ?? projectInfo?.Name ?? issue.ProjectId;
        return new WorkflowProjectContext(
            projectId,
            projectName,
            repo.Name,
            repo.GitUrl,
            repo.BaseBranch);
    }

    private WorkspaceIdentity BuildWorkspaceIdentity(Domain.Issue issue, WorkflowProjectContext projectContext, string workflowRunId)
    {
        var runnerRoot = MohistWorkspaceLayout.ResolveRunnerRoot(_configuration, _environment);
        var workspacePath = MohistWorkspaceLayout.IssueWorkspacePath(runnerRoot, projectContext.Name, issue.Number);
        var changeDir = MohistDefaultWorkflowProjection.ChangeDir(issue.Number);
        // The runner manages the per-run head ref (`mohist/run-${workflowRunId}`)
        // inside the workspace; the integrate merge uses this branch as the
        // source. Persisting it on WorkspaceIdentity keeps review APIs,
        // SignalR handlers, and the integrate task aligned on a stable ref.
        var runBranch = WorkflowRunBranch.For(workflowRunId);
        return new WorkspaceIdentity(
            Path: workspacePath,
            Branch: runBranch,
            ChangeDir: changeDir);
    }

    public async Task CancelAsync()
    {
        EnsureIssue();
        if (_issue!.ActiveWorkflowRunId is { } wrId)
        {
            var wfStatus = await _workflowQuerier.GetStatusAsync(wrId);
            if (wfStatus?.Status is "running" or "paused" or "awaiting-approval")
                throw new InvalidOperationException($"Cannot close issue while workflow is {wfStatus.Status}. Stop the workflow first.");
        }
        _issue.Close("user-cancelled");
        await SaveIssueAsync();
    }

    public async Task CompleteWorkAsync(string workflowRunId)
    {
        if (_issue is null) return;
        if (!_issue.Complete(workflowRunId)) return;
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

        // Lazy reconciliation: if the bus subscription missed the
        // Completed event, the read path here is the next chance to bring
        // the issue in sync with the workflow's actual state.
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

        if (wfStatus.Status == "completed")
            await CompleteWorkAsync(workflowRunId);
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

    private VariableBundle BuildIssueVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project, WorkspaceIdentity workspace)
    {
        var variables = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["mohist"] = JsonSerializer.SerializeToElement(new { runId = workflowRunId }, WorkflowVariableJson.Options),
            ["issue"] = JsonSerializer.SerializeToElement(new { id = issue.Id, number = issue.Number }, WorkflowVariableJson.Options),
            // `project` is a Mohist scope: only identity-level metadata. The
            // base branch belongs to the repository reference, never the
            // project, so it is intentionally absent here.
            ["project"] = JsonSerializer.SerializeToElement(new { id = project.Id, name = project.Name }, WorkflowVariableJson.Options),
            ["repository"] = JsonSerializer.SerializeToElement(new { name = project.RepositoryName, gitUrl = project.RepositoryGitUrl, baseBranch = project.RepositoryBaseBranch }, WorkflowVariableJson.Options),
            ["workspace"] = JsonSerializer.SerializeToElement(new { path = workspace.Path, branch = workspace.Branch, changeDir = workspace.ChangeDir }, WorkflowVariableJson.Options),
            ["openspecChangeDir"] = JsonSerializer.SerializeToElement(MohistDefaultWorkflowProjection.ChangeDir(issue.Number), WorkflowVariableJson.Options),
        };

        var varsJson = JsonSerializer.Serialize(variables, WorkflowVariableJson.Options);
        var varsElement = JsonSerializer.Deserialize<JsonElement>(varsJson);
        return new VariableBundle(varsElement);
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

    private async Task SaveIssueAsync()
    {
        if (_issue is null) return;
        var pending = _issue.PendingEvents;
        _issue.ClearPendingEvents();
        await _issueStore.SaveAsync(_issue.Id, _issue);
        await PublishIssueEventsAsync(pending);
    }

    private async Task PublishIssueEventsAsync(IReadOnlyList<Issue.Domain.Events.IssueEvent> events)
    {
        if (events.Count == 0 || _issue is null) return;
        var source = IssueEventPersistence.IssueSource(_issue.Id);
        var subject = _issue.Number.ToString();
        var extensions = new Dictionary<string, string>
        {
            ["projectid"] = _issue.ProjectId,
            ["issueid"] = _issue.Id,
            ["issueno"] = subject,
        };

        try
        {
            foreach (var evt in events)
            {
                var type = IssueEventSerializer.BusType(evt);
                var dataJson = IssueEventSerializer.ToData(evt);
                var envelope = new CloudEvent(
                    id: Guid.NewGuid().ToString(),
                    source: new Uri(source, UriKind.Relative),
                    type: type,
                    time: DateTimeOffset.UtcNow,
                    data: dataJson,
                    subject: subject,
                    extensions: extensions);

                await _eventStore.AppendAsync(envelope);
                await _eventBus.PublishAsync(evt, type, source, subject, extensions, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Post-commit publish failed for issue {IssueId}", _issue.Id);
        }
    }

    private void EnsureIssue()
    {
        if (_issue is null)
            throw new KeyNotFoundException($"Issue '{GrainKey}' not found");
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
        if (_issue is null) throw new KeyNotFoundException($"Issue '{GrainKey}' not found");

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
