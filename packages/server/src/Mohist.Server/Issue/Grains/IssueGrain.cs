using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Mohist.Server.SystemInfo;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.Attachments;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Grains;

public class IssueGrain : Grain, IIssueGrain
{
    private Domain.Issue? _issue;
    private bool _issueReloadRequired;
    private readonly IIssueStore _issueStore;
    private readonly IssueWorkflowProfileRegistry _profiles;
    private readonly WorkflowQuerier _workflowQuerier;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IssueRepositoryResolver _repositoryResolver;
    private readonly WorkflowProfileManager _workflowProfileManager;
    private readonly ProjectWorkflowProfileManager _projectProfileManager;
    private readonly IssueWorkflowProfileManager _issueProfileManager;
    private readonly AttachmentService _attachmentService;
    private readonly IConfiguration _configuration;
    private readonly IEnvironmentVariableProvider _environment;
    private readonly ILogger<IssueGrain> _log;

    public IssueGrain(
        IIssueStore issueStore,
        IssueWorkflowProfileRegistry profiles,
        WorkflowQuerier workflowQuerier,
        IDbContextFactory<MohistDbContext> dbFactory,
        IssueRepositoryResolver repositoryResolver,
        WorkflowProfileManager workflowProfileManager,
        ProjectWorkflowProfileManager projectProfileManager,
        IssueWorkflowProfileManager issueProfileManager,
        AttachmentService attachmentService,
        IConfiguration configuration,
        IEnvironmentVariableProvider environment,
        ILogger<IssueGrain> log)
    {
        _issueStore = issueStore;
        _profiles = profiles;
        _workflowQuerier = workflowQuerier;
        _dbFactory = dbFactory;
        _repositoryResolver = repositoryResolver;
        _workflowProfileManager = workflowProfileManager;
        _projectProfileManager = projectProfileManager;
        _issueProfileManager = issueProfileManager;
        _attachmentService = attachmentService;
        _configuration = configuration;
        _environment = environment;
        _log = log;
    }

    private string GrainKey => string.IsNullOrEmpty(GrainKeyForTest) ? this.GetPrimaryKeyString() : GrainKeyForTest;

    internal string GrainKeyForTest { get; set; } = string.Empty;

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _issueReloadRequired = false;
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

    public async Task<bool> AssignEpicAsync(int epicNumber)
    {
        EnsureIssue();
        if (!_issue!.AssignEpic(epicNumber)) return false;
        await SaveIssueAsync();
        return true;
    }

    public async Task<bool> RemoveEpicAsync(int expectedEpicNumber)
    {
        EnsureIssue();
        if (!_issue!.RemoveEpic(expectedEpicNumber)) return false;
        await SaveIssueAsync();
        return true;
    }

    public async Task<bool> TryStartFromEpicAsync(int expectedEpicNumber)
    {
        EnsureIssue();
        if (_issue!.EpicNumber != expectedEpicNumber) return false;
        await StartWorkAsync();
        return true;
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
        ThrowIfStartBlocked(undeliveredPrerequisites);
        var wrId = $"wr_{Guid.NewGuid():N}";
        return await StartWorkflowAsync(project, wrId, repo, undeliveredPrerequisites);
    }

    private void ThrowIfStartBlocked(IReadOnlySet<int>? undeliveredPrerequisites)
    {
        var blocker = _issue!.StartBlocker(undeliveredPrerequisites);
        if (blocker is IssueStartBlocker.Draft)
            throw new IssueStartBlockedException(blocker, $"Issue #{_issue.Number} is still a draft and cannot be started");
        if (blocker is IssueStartBlocker.WaitingFor waiting)
            throw new IssueStartBlockedException(blocker, $"Issue #{_issue.Number} is waiting for prerequisite issue #{waiting.PrerequisiteNumber}");
    }

    private async Task<string> StartWorkflowAsync(
        WorkflowProjectContext? project,
        string wrId,
        RepositoryInfo repo,
        IReadOnlySet<int>? undeliveredPrerequisites)
    {
        var input = await BuildWorkflowStartInputAsync(project, wrId, repo);

        _issue!.Start(wrId, undeliveredPrerequisites);
        await SaveIssueAsync();

        try
        {
            await EnsureCommittedWorkflowBindingAsync(wrId, input);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Issue {Key} committed workflow binding {WrId}; durable IssueWorkStarted handling will retry workflow creation",
                GrainKey,
                wrId);
        }
        _log.LogInformation("Issue {Key} started workflow {WrId}", GrainKey, wrId);
        return wrId;
    }

    private async Task<WorkflowStartInput> BuildWorkflowStartInputAsync(
        WorkflowProjectContext? project,
        string wrId,
        RepositoryInfo repo)
    {
        var issue = _issue!;

        var projectGrain = GrainFactory.GetGrain<IProjectGrain>(issue.ProjectId);
        var projectInfo = await projectGrain.GetAsync();
        var projectContext = BuildWorkflowProjectContext(issue, project, projectInfo, repo);
        var workspace = BuildWorkspaceIdentity(issue, projectContext, wrId);

        // The startup template resolution honors the issue's effective
        // workflow profile (issue selection → project default → system
        // default) and only the explicit advanced overrides (issue custom
        // YAML, project template reference) take precedence. Auto-seeding the
        // project default here would shadow an explicit issue-level profile
        // selection (e.g. mohist/github-pr would lose to a freshly auto-seeded
        // mohist/local), so the resolver's own fallback handles projects
        // without a configured default.

        var resolvedTemplate = await _workflowProfileManager.LoadTemplateAsync(wrId, projectContext.Id, issue.Number);
        var definition = resolvedTemplate.Structure
            ?? throw new InvalidOperationException(WorkflowProfileManager.NoEnabledWorkflowProfileMessage);

        // Resolve the effective profile (issue selection → project default →
        // system default) so prompts are merged from the same profile that
        // drives the workflow definition. Previously this hardcoded the
        // mohist/local profile, which meant a mohist/github-pr issue would
        // inherit default prompts even though the run used the GitHub PR
        // definition.
        var projectDefaultTemplateId = await _projectProfileManager.GetDefaultTemplateAsync(projectContext.Id);
        var disabledIds = await _projectProfileManager.GetDisabledWorkflowProfileIdsAsync(projectContext.Id);
        var effectiveProfileId = EffectiveWorkflowProfileResolver.ResolveCore(
            issue.WorkflowProfileId,
            projectDefaultTemplateId,
            _profiles.Exists,
            disabledIds,
            _profiles.List().Select(p => p.Id).ToList());
        if (string.IsNullOrWhiteSpace(effectiveProfileId))
            throw new InvalidOperationException(WorkflowProfileManager.NoEnabledWorkflowProfileMessage);

        var effectiveProfile = _profiles.Get(effectiveProfileId);
        var mergedPrompts = effectiveProfile is MohistIssueWorkflowProfileBase mohistProfile
            ? await mohistProfile.GetMergedPromptsAsync(issue.ProjectId)
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
        var existingVariables = await _issueProfileManager.GetVariablesAsync(issue.ProjectId, issue.Number);
        var mergedVariables = VariableBundle.Patch(existingVariables, issueBundle);
        await _issueProfileManager.SetVariablesAsync(issue.ProjectId, issue.Number, mergedVariables);

        foreach (var (key, body) in mergedPrompts)
            await _issueProfileManager.SetPromptAsync(issue.ProjectId, issue.Number, key, body);

        return new WorkflowStartInput(
            Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: BuildWorkflowAnnotations(issue, projectContext.Id)),
            Workspace: workspace);
    }

    public async Task EnsureWorkflowBindingAsync(string workflowRunId)
    {
        EnsureIssue();
        if (!string.Equals(_issue!.WorkflowRunId, workflowRunId, StringComparison.Ordinal)
            || !_issue.WorkflowBindingPending)
        {
            return;
        }
        await EnsureCommittedWorkflowBindingAsync(workflowRunId);
    }

    private async Task EnsureCommittedWorkflowBindingAsync(
        string workflowRunId,
        WorkflowStartInput? preparedInput = null)
    {
        var issue = _issue!;
        if (!string.Equals(issue.WorkflowRunId, workflowRunId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Issue '{issue.ProjectId}/#{issue.Number}' is not bound to workflow '{workflowRunId}'.");

        var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
        if (await workflow.GetRunStatusAsync() is null)
        {
            var input = preparedInput;
            if (input is null)
            {
                var resolution = RequireResolvedRepository(await ResolveIssueRepositoryAtStartAsync(issue));
                input = await BuildWorkflowStartInputAsync(null, workflowRunId, resolution.Repository!);
            }
            await workflow.PrepareIssueStartAsync(input);
        }

        await workflow.ConfirmIssueBindingAsync(new WorkflowIssueBinding(
            issue.ProjectId,
            issue.Number,
            issue.EpicNumber,
            issue.LineageVersion));

        if (issue.ConfirmWorkflowBinding(workflowRunId))
            await SaveIssueAsync();
    }

    private async Task<string?> TryReuseActiveWorkflowAsync()
    {
        var issue = _issue!;
        var workflowRunId = issue.WorkflowRunId;
        if (workflowRunId is null) return null;

        try
        {
            var workflow = GrainFactory.GetGrain<IWorkflowGrain>(workflowRunId);
            if (!issue.WorkflowBindingPending)
            {
                if (await workflow.IsStoppedOrTerminalAsync())
                {
                    issue.ClearStoppedWorkflow(workflowRunId);
                    await SaveIssueAsync();
                    return null;
                }

                _log.LogInformation("Issue {IssueKey} reusing workflow run {WorkflowRunId}", GrainKey, workflowRunId);
                return workflowRunId;
            }
            if (await workflow.GetRunStatusAsync() is null)
            {
                await EnsureCommittedWorkflowBindingAsync(workflowRunId);
                _log.LogInformation("Issue {IssueKey} restored workflow run {WorkflowRunId}", GrainKey, workflowRunId);
                return workflowRunId;
            }
            if (await workflow.IsStoppedOrTerminalAsync())
            {
                issue.ClearStoppedWorkflow(workflowRunId);
                await SaveIssueAsync();
                return null;
            }

            await workflow.ConfirmIssueBindingAsync(new WorkflowIssueBinding(
                issue.ProjectId,
                issue.Number,
                issue.EpicNumber,
                issue.LineageVersion));
            _log.LogInformation("Issue {IssueKey} reusing workflow run {WorkflowRunId}", GrainKey, workflowRunId);
            return workflowRunId;
        }
        catch (Exception ex) when (IsWorkflowRunStateCorruption(ex))
        {
            _log.LogWarning(ex,
                "Issue {IssueKey} workflow run {WorkflowRunId} cannot be loaded while starting; clearing workflow run reference",
                GrainKey,
                workflowRunId);
            issue.ClearStoppedWorkflow(workflowRunId);
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

    private static Dictionary<string, string> BuildWorkflowAnnotations(Domain.Issue issue, string projectId)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectId"] = projectId,
            ["issueNumber"] = issue.Number.ToString(),
        };
        if (issue.EpicNumber is > 0)
            annotations["epicNumber"] = issue.EpicNumber.Value.ToString();
        return annotations;
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
        // "Has a workflow run reference" (an execution fact) is NOT the
        // same as "has an active, controllable workflow" (status + run
        // state). Only run the cannot-close-while-running guard when the
        // issue is actually InProgress with a non-null workflowRunId AND
        // the run is not stopped/terminal — a Done or archived issue that
        // preserved its reference is not a running workflow, and Close()
        // already rejects Done/archived itself.
        // The new WorkflowRunStatus state machine splits the old single
        // "running" into Created/AwaitingBinding/Pending/Ready/Running; all five
        // represent a non-terminal, controllable workflow that the user
        // must explicitly stop before closing the issue.
        var wfStatus = await GetControllableWorkflowStatusAsync();
        if (wfStatus is { } running
            && running is "created" or "awaiting-binding" or "pending" or "ready" or "running" or "paused" or "awaiting-approval")
        {
            throw new InvalidOperationException($"Cannot close issue while workflow is {running}. Stop the workflow first.");
        }
        _issue!.Close("user-cancelled");
        await SaveIssueAsync();
    }

    /// <summary>
    /// Derived judgment: the issue currently has an active, controllable
    /// workflow run. Combines the issue's status (must be
    /// <c>InProgress</c>) with the run's state (must not be stopped or
    /// terminal). Returns the workflow status string when controllable,
    /// <c>null</c> otherwise. Used by control paths that previously
    /// conflated "has a workflow run reference" with "has an active
    /// workflow" — see design decision D3.
    /// </summary>
    private async Task<string?> GetControllableWorkflowStatusAsync()
    {
        var issue = _issue;
        if (issue is null) return null;
        if (issue.Status != Domain.IssueStatus.InProgress) return null;
        if (issue.WorkflowRunId is not { } wrId) return null;

        var wfStatus = await _workflowQuerier.GetStatusAsync(wrId);
        if (wfStatus?.Status is null) return null;
        if (wfStatus.Status is "stopped" or "completed" or "failed") return null;
        return wfStatus.Status;
    }

    public async Task CompleteWorkAsync(string workflowRunId)
    {
        RejectIfReloadRequired();
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
        var hasWorkflowProfile = present.Contains(nameof(UpdateIssueData.WorkflowProfileId));

        var title = hasTitle ? data.Title : null;
        var body = hasBody ? data.Body : null;
        var priority = hasPriority ? data.Priority : null;

        var presentAttachmentsNull = hasAttachments && data.AttachmentIds is null;
        if (hasAttachments && !presentAttachmentsNull)
        {
            await _attachmentService.ValidateIssueBindAsync(_issue!.ProjectId, _issue.Number, data.AttachmentIds);
        }

        // Workflow profile selection is an execution-template fact: it cannot
        // be changed once the issue has started. Reject any attempt early so
        // we never half-apply other fields. Variable/prompt endpoints are
        // untouched and remain valid run-scoped runtime overrides; this only
        // guards the issue-level selection.
        if (hasWorkflowProfile && _issue!.WorkflowRunId is not null)
        {
            throw new WorkflowProfileLockedException(_issue.Number, _issue.WorkflowRunId);
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

        if (hasWorkflowProfile)
        {
            // Three-state: absent = leave alone (handled by hasWorkflowProfile
            // guard above), present-and-null = clear to inherit-default,
            // present-and-value = replace with the supplied id. The route
            // handler has already validated that any non-null value refers to
            // a known profile; the aggregate's ReplaceWorkflowProfile
            // normalizes whitespace to null.
            _issue.ReplaceWorkflowProfile(data.WorkflowProfileId);
        }

        await SaveIssueAsync();

        if (presentAttachmentsNull)
        {
            // Three-state: present-and-null = unbind all attachments. The
            // ValidateBindAsync/BindAsync pair above would be a no-op for an
            // empty id list, so we go through the dedicated clear path.
            await _attachmentService.UnbindAllIssueAsync(_issue.ProjectId, _issue.Number);
        }
        else if (hasAttachments)
        {
            await _attachmentService.ReplaceIssueAsync(_issue.ProjectId, _issue.Number, data.AttachmentIds!);
        }
    }

    public async Task<IssueWorkflowStatus?> GetWorkflowStatusAsync()
    {
        EnsureIssue();

        var wrId = _issue!.WorkflowRunId;
        if (wrId is null) return null;

        var wfStatus = await _workflowQuerier.GetStatusAsync(wrId);

        var defaultProfile = _profiles.Get(IssueWorkflowProfiles.LocalId);
        var projection = defaultProfile.ProjectWorkflowState(_issue, wfStatus);

        return new IssueWorkflowStatus(
            _issue.Number,
            _issue.Title,
            projection.IssueStatus,
            projection.Health,
            wrId,
            projection.ChangeDir,
            null,
            wfStatus);
    }

    public async Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null)
    {
        if (_issue is not null)
            throw new InvalidOperationException($"Issue '{GrainKey}' already exists");

        var resolvedRef = await ResolveRepositoryRefAsync(projectId, repositoryRef);
        await _attachmentService.ValidateIssueBindAsync(projectId, number, attachmentIds);

        if (!string.IsNullOrWhiteSpace(workflowProfileId) && !_profiles.Exists(workflowProfileId))
        {
            throw new UnknownWorkflowProfileException(workflowProfileId);
        }

        var issue = Domain.Issue.Create(
            projectId,
            number,
            title,
            body,
            labels,
            priority ?? "p2",
            resolvedRef,
            risk,
            isDraft,
            workflowProfileId);

        // Stage the in-memory aggregate so LoadIssueSummaryAsync can resolve
        // the project id from _issue.ProjectId during prerequisite existence
        // validation. The aggregate has not yet been persisted (SaveIssueAsync
        // below is the only place this issue touches the store). On a
        // validation failure we restore _issue to null, which means a
        // subsequent retry on the same grain sees a clean slate, identical
        // to the branch where prerequisites are absent.
        _issue = issue;

        // Create-time prerequisite application: validate every unique number
        // against the project, reject self-reference against the newly allocated
        // number, and apply idempotently before the single SaveIssueAsync so
        // that a validation failure leaves nothing persisted. Mirrors the
        // single-add path by reusing LoadIssueSummaryAsync verbatim.
        try
        {
            if (prerequisiteNumbers is { Length: > 0 })
            {
                foreach (var prerequisiteNumber in prerequisiteNumbers.Distinct())
                {
                    if (prerequisiteNumber == number)
                        throw PrerequisiteValidationException.SelfReference(prerequisiteNumber);
                    if (await LoadIssueSummaryAsync(prerequisiteNumber) is null)
                        throw PrerequisiteValidationException.NotFound(prerequisiteNumber);
                    if (await WouldCreatePrerequisiteCycleAsync(prerequisiteNumber))
                        throw PrerequisiteValidationException.SelfReference(prerequisiteNumber);
                    _issue!.AddPrerequisite(prerequisiteNumber);
                }
            }
        }
        catch
        {
            _issue = null;
            throw;
        }

        await SaveIssueAsync();
        await _attachmentService.BindIssueAsync(projectId, issue.Number, attachmentIds);
        return issue.Number;
    }

    public async Task<IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber)
    {
        RejectIfReloadRequired();
        if (_issue is null)
            return IssuePrerequisiteResult.IssueNotFound();
        if (prerequisiteNumber == _issue.Number)
            return IssuePrerequisiteResult.Circular();
        if (await LoadIssueSummaryAsync(prerequisiteNumber) is null)
            return IssuePrerequisiteResult.PrerequisiteNotFound(prerequisiteNumber);
        if (await WouldCreatePrerequisiteCycleAsync(prerequisiteNumber))
            return IssuePrerequisiteResult.Circular("Circular prerequisite: this would create a cycle");

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

        // Snapshot before clearing: PendingEvents returns a live view over the
        // same _pendingEvents list, so ClearPendingEvents() would otherwise
        // drain `pending` too and the events-aware save would no-op on an
        // empty collection — silently skipping every IssueEvents append (the
        // regression that previously left IssueEvents permanently empty).
        var pending = _issue.PendingEvents.ToList();
        try
        {
            await _issueStore.SaveAsync(GrainKey, _issue, pending);
        }
        catch
        {
            // The store rolled its transaction back, but the in-memory
            // aggregate has already absorbed the state mutation. A retry on
            // this activation could persist the mutated state through the
            // no-events overload, losing the rolled-back IssueEvents row.
            // Quarantine this activation: mark it reload required so
            // EnsureIssue() rejects further work, then let the caller's
            // exception surface while the grain deactivates and reloads from
            // storage on its next activation.
            _issueReloadRequired = true;
            DeactivateOnIdle();
            throw;
        }
        _issue.ClearPendingEvents();
    }

    [MemberNotNull(nameof(_issue))]
    private void EnsureIssue()
    {
        if (_issue is null)
            throw new KeyNotFoundException($"Issue '{GrainKey}' not found");
        if (_issueReloadRequired)
            throw new InvalidOperationException($"Issue '{GrainKey}' must reload after a failed event-aware save");
    }

    // For entry points that return a result (not throw) when no issue exists,
    // a reload-required activation must still be rejected: the dirty in-memory
    // aggregate must not be mutated/persisted through these paths before the
    // grain reloads from storage.
    private void RejectIfReloadRequired()
    {
        if (_issueReloadRequired)
            throw new InvalidOperationException($"Issue '{GrainKey}' must reload after a failed event-aware save");
    }

    private async Task<IssuePrerequisiteSummary?> LoadIssueSummaryAsync(int issueNumber)
    {
        if (_issue is null) return null;
        try
        {
            var issue = await _issueStore.LoadAsync(new IssueKey(_issue.ProjectId, issueNumber).ToGrainKeyString());
            return issue is null ? null : IssuePrerequisiteSummary.FromDomain(issue);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<bool> WouldCreatePrerequisiteCycleAsync(int prerequisiteNumber)
    {
        if (_issue is null) return false;
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(prerequisiteNumber);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current == _issue.Number) return true;

            var currentIssue = await LoadIssueByNumberAsync(current);
            if (currentIssue is null) continue;
            foreach (var next in currentIssue.PrerequisiteNumbers)
                pending.Push(next);
        }

        return false;
    }

    private async Task<Domain.Issue?> LoadIssueByNumberAsync(int issueNumber)
    {
        if (_issue is null) return null;
        try
        {
            return await _issueStore.LoadAsync(new IssueKey(_issue.ProjectId, issueNumber).ToGrainKeyString());
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null)
    {
        RejectIfReloadRequired();
        if (_issue is null) throw new KeyNotFoundException($"Issue '{GrainKey}' not found");

        var comment = new IssueCommentRow
        {
            Id = $"cmt_{Guid.NewGuid():N}",
            ProjectId = _issue.ProjectId,
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
    [property: Id(6)] IReadOnlySet<string>? PresentFields = null,
    [property: Id(7)] string? WorkflowProfileId = null
);
