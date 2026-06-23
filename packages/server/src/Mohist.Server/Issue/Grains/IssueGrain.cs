using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Mohist.Server.SystemInfo;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Infrastructure.Workspace;
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
    private readonly AttachmentService _attachmentService;
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
        AttachmentService attachmentService,
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
        _attachmentService = attachmentService;
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

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
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
        var reusedRunId = await TryReuseActiveWorkflowAsync();
        if (reusedRunId is not null)
            return reusedRunId;
        var resolution = RequireResolvedRepository(await ResolveIssueRepositoryAtStartAsync(_issue!));
        var repo = resolution.Repository!;
        var undeliveredPrerequisites = await LoadUndeliveredPrerequisiteNumbersAsync();
        var wrId = $"wr_{Guid.NewGuid():N}";
        _issue!.Start(wrId, undeliveredPrerequisites);
        return await StartWorkflowAsync(project, wrId, repo);
    }

    private async Task<string> StartWorkflowAsync(WorkflowProjectContext? project, string wrId, RepositoryInfo repo)
    {
        var issue = _issue!;

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

        // T1: persist the issue's built-in calling context on the issue
        // profile. Global (config.jsonc) and project Variables are NOT baked
        // in here — they are merged live at resolution time (dispatch + display)
        // so that edits to project/global Variables propagate to already-created
        // issues. Explicit issue overrides (e.g. model + reasoning variant set
        // via POST/PATCH /api/issues) are preserved by PATCH-merging the
        // context bundle on top of any existing variables, so an issue whose
        // agent config was set during creation survives the T1 merge.
        var issueBundle = IssueVariableBuilder.BuildContextBundle(
            wrId,
            issue,
            projectContext,
            workspace);
        var existingVariables = await _issueProfileManager.GetVariablesAsync(issue.Id);
        var mergedVariables = VariableBundle.Patch(existingVariables, issueBundle);
        await _issueProfileManager.SetVariablesAsync(issue.Id, mergedVariables);

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
        var present = data.PresentFields ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
        var hasTitle = present.Contains(nameof(UpdateIssueData.Title));
        var hasBody = present.Contains(nameof(UpdateIssueData.Body));
        var hasLabels = present.Contains(nameof(UpdateIssueData.Labels));
        var hasPriority = present.Contains(nameof(UpdateIssueData.Priority));
        var hasIsDraft = present.Contains(nameof(UpdateIssueData.IsDraft));
        var hasAttachments = present.Contains(nameof(UpdateIssueData.AttachmentIds));

        var title = hasTitle ? data.Title : null;
        var body = hasBody ? data.Body : null;
        var priority = hasPriority ? data.Priority : null;

        var presentAttachmentsNull = hasAttachments && data.AttachmentIds is null;
        if (hasAttachments && !presentAttachmentsNull)
        {
            await _attachmentService.ValidateIssueBindAsync(_issue!.ProjectId, _issue.Id, data.AttachmentIds);
        }

        // For labels, the grain honors three-state semantics:
        //  - absent (hasLabels = false): leave labels untouched
        //  - present-and-null: clear to empty
        //  - present-and-value: pass to Issue.Update which deep-merges and
        //    emits IssueLabelsChanged only when the resulting map actually
        //    differs (matching the pre-fix event semantics).
        IReadOnlyDictionary<string, string>? labelsForUpdate = null;
        if (hasLabels)
        {
            labelsForUpdate = data.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        _issue!.Update(title, body, labelsForUpdate, priority);

        if (hasIsDraft && data.IsDraft.HasValue)
            _issue.SetDraft(data.IsDraft.Value);

        await SaveIssueAsync();

        if (presentAttachmentsNull)
        {
            // Three-state: present-and-null = unbind all attachments. The
            // ValidateBindAsync/BindAsync pair above would be a no-op for an
            // empty id list, so we go through the dedicated clear path.
            await _attachmentService.UnbindAllIssueAsync(_issue.ProjectId, _issue.Id);
        }
        else if (hasAttachments)
        {
            await _attachmentService.ReplaceIssueAsync(_issue.ProjectId, _issue.Id, data.AttachmentIds!);
        }
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

public async Task<string> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? issueId = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null)
    {
        if (_issue is not null)
            throw new InvalidOperationException($"Issue '{GrainKey}' already exists");

        var resolvedRef = await ResolveRepositoryRefAsync(projectId, repositoryRef);
        var resolvedIssueId = issueId ?? $"issue_{Guid.NewGuid():N}";
        await _attachmentService.ValidateIssueBindAsync(projectId, resolvedIssueId, attachmentIds);

        var issue = Domain.Issue.Create(
            resolvedIssueId,
            projectId,
            number,
            title,
            body,
            labels,
            priority ?? "p2",
            resolvedRef,
            risk,
            isDraft);

        _issue = issue;
        await SaveIssueAsync();
        await _attachmentService.BindIssueAsync(projectId, issue.Id, attachmentIds);
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

    public async Task<IssueStartReadiness> GetStartReadinessAsync()
    {
        EnsureIssue();
        var summaries = new List<IssuePrerequisiteSummary>();
        foreach (var prerequisiteNumber in _issue!.PrerequisiteNumbers)
        {
            var summary = await LoadIssueSummaryAsync(prerequisiteNumber);
            if (summary is not null)
                summaries.Add(summary);
        }

        var summariesByNumber = summaries.ToDictionary(s => s.Number);
        var undelivered = new HashSet<int>(summaries.Where(s => !s.Completed).Select(s => s.Number));
        var blocker = _issue!.StartBlocker(undelivered);
        var blockerDto = IssueStartBlockerDto.FromDomain(blocker, summariesByNumber);
        return new IssueStartReadiness(
            IsDraft: _issue.IsDraft,
            CanStart: blockerDto is null,
            Blocker: blockerDto);
    }

    private async Task<IReadOnlySet<int>> LoadUndeliveredPrerequisiteNumbersAsync()
    {
        var undelivered = new HashSet<int>();
        foreach (var prerequisiteNumber in _issue!.PrerequisiteNumbers)
        {
            var summary = await LoadIssueSummaryAsync(prerequisiteNumber);
            if (summary is not null && !summary.Completed)
                undelivered.Add(prerequisiteNumber);
        }
        return undelivered;
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

    public async Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null)
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

        await _attachmentService.ValidateCommentBindAsync(_issue.ProjectId, comment.Id, attachmentIds);
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.IssueComments.Add(comment);
        await db.SaveChangesAsync();
        await _attachmentService.BindCommentAsync(_issue.ProjectId, comment.Id, attachmentIds);

        return new IssueCommentResult(comment.Id, comment.Body);
    }

}

[GenerateSerializer]
public record UpdateIssueData(
    [property: Id(0)] string? Title = null,
    [property: Id(1)] string? Body = null,
[property: Id(2)] IReadOnlyDictionary<string, string>? Labels = null,
    [property: Id(3)] string? Priority = null,
    [property: Id(4)] bool? IsDraft = null,
    [property: Id(5)] string[]? AttachmentIds = null,
    [property: Id(6)] IReadOnlySet<string>? PresentFields = null
);
