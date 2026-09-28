using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.Issue.Grains;

public partial class IssueGrain
{
    public async Task<Coordinator.IssueBindingParticipantOutcome> CreateWithReceiptAsync(
        string projectId,
        int number,
        string title,
        string? body,
        IReadOnlyDictionary<string, string>? labels,
        string? priority,
        string repositoryRef,
        string? risk,
        bool isDraft,
        string[]? attachmentIds,
        string? workflowProfileId,
        bool noWorkflow,
        int[]? prerequisiteNumbers,
        int? parentIssueNumber,
        string commandId,
        long? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(repositoryRef))
            throw new IssueRepositoryUnknownException(repositoryRef ?? string.Empty);

        if (_issue is not null)
        {
            // Receipt-match: a prior coordinator activation persisted this
            // exact command, so replay the outcome without mutating state.
            // The coordinator only issues Create with a non-null
            // commandId/expectedRevision, so the absence of a matching
            // receipt means a different command is reusing the same
            // grain key — surface that as a fresh-failure conflict rather
            // than silently allowing a duplicate.
            if (_issue.LastRepositoryCommand is { } stored
                && string.Equals(stored.CommandId, commandId, StringComparison.Ordinal)
                && stored.Kind == "create"
                && string.Equals(stored.RepositoryName, repositoryRef, StringComparison.Ordinal))
            {
                return Coordinator.IssueBindingParticipantOutcome.AlreadyApplied;
            }
            throw new InvalidOperationException(
                $"Issue '{GrainKey}' already exists; cannot replay create command '{commandId}'");
        }

        var project = await GrainFactory.GetGrain<IProjectGrain>(projectId).GetAsync();
        if (project is null)
            throw new IssueRepositoryUnknownException(repositoryRef);
        var match = project.GetRepository(repositoryRef);
        if (match is null)
            throw new IssueRepositoryUnknownException(repositoryRef);
        var canonicalName = match.Name;

        if (!string.IsNullOrWhiteSpace(workflowProfileId)
            && !await ProfileExistsAsync(projectId, workflowProfileId))
        {
            throw new UnknownWorkflowProfileException(workflowProfileId);
        }

        var parent = await ResolveParentAsync(parentIssueNumber, number, requireTargetHasNoChildren: false);
        var issue = Domain.Issue.Create(
            projectId,
            number,
            title,
            body,
            labels,
            priority ?? parent?.Priority ?? "p2",
            canonicalName,
            risk,
            isDraft,
            workflowProfileId,
            noWorkflow,
            commandId,
            expectedRevision);

        _issue = issue;

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
            if (parent is not null)
                _issue!.AssignParent(parent.Number);
        }
        catch
        {
            _issue = null;
            throw;
        }

        await _attachmentService.ValidateIssueBindAsync(projectId, issue.Number, attachmentIds);
        await SaveIssueAsync();
        await _attachmentService.BindIssueAsync(projectId, issue.Number, attachmentIds);
        return Coordinator.IssueBindingParticipantOutcome.Applied;
    }

    public async Task<Coordinator.IssueBindingParticipantOutcome> ChangeRepositoryWithReceiptAsync(
        IssueChangeRepositoryCommand command,
        string commandId,
        long? expectedRevision)
    {
        EnsureIssue();
        if (_issue is null)
            throw new KeyNotFoundException($"Issue '{GrainKey}' not found");

        if (string.IsNullOrWhiteSpace(command.RepositoryName))
            throw new IssueRepositoryUnknownException(command.RepositoryName ?? string.Empty);

        if (_issue.LastRepositoryCommand is { } stored
            && string.Equals(stored.CommandId, commandId, StringComparison.Ordinal)
            && stored.Kind == "change"
            && string.Equals(stored.RepositoryName, command.RepositoryName, StringComparison.Ordinal))
        {
            return Coordinator.IssueBindingParticipantOutcome.AlreadyApplied;
        }

        var project = await GrainFactory.GetGrain<IProjectGrain>(_issue.ProjectId).GetAsync();
        if (project is null)
            throw new IssueRepositoryUnknownException(command.RepositoryName);
        var match = project.GetRepository(command.RepositoryName);
        if (match is null)
            throw new IssueRepositoryUnknownException(command.RepositoryName);
        var canonicalName = match.Name;

        var present = command.PresentFields ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
        var hasTitle = present.Contains(nameof(command.Title));
        var hasBody = present.Contains(nameof(command.Body));
        var hasLabels = present.Contains(nameof(command.Labels));
        var hasPriority = present.Contains(nameof(command.Priority));
        var hasRisk = present.Contains(nameof(command.Risk));
        var hasIsDraft = present.Contains(nameof(command.IsDraft));
        var hasAttachments = present.Contains(nameof(command.AttachmentIds));
        var hasWorkflowProfile = present.Contains(nameof(command.WorkflowProfileId));
        var hasNoWorkflow = present.Contains(nameof(command.NoWorkflow));
        var hasParent = present.Contains(nameof(command.ParentIssueNumber));

        ValidateWorkflowSelection(hasWorkflowProfile, command.WorkflowProfileId, hasNoWorkflow, command.NoWorkflow);

        if (hasWorkflowProfile
            && !string.IsNullOrWhiteSpace(command.WorkflowProfileId)
            && !await ProfileExistsAsync(_issue.ProjectId, command.WorkflowProfileId))
            throw new UnknownWorkflowProfileException(command.WorkflowProfileId!);

        if (hasAttachments && command.AttachmentIds is not null)
        {
            await _attachmentService.ValidateIssueBindAsync(_issue.ProjectId, _issue.Number, command.AttachmentIds);
        }

        if (hasTitle && string.IsNullOrWhiteSpace(command.Title))
            throw new ArgumentException("Issue title is required", nameof(command.Title));
        if (hasPriority && command.Priority is not null)
            _ = IssuePriority.From(command.Priority);
        if (hasRisk)
            _ = IssueRisk.From(command.Risk);
        if (hasLabels && command.Labels is not null)
        {
            foreach (var (key, value) in command.Labels)
            {
                Domain.Issue.ValidateLabelKey(key);
                Domain.Issue.ValidateLabelValue(value);
            }
        }

        if (hasIsDraft && command.IsDraft.HasValue)
            _issue.ValidateDraftTransition();

        var parent = hasParent && command.ParentIssueNumber is not null
            ? await ResolveParentAsync(command.ParentIssueNumber, _issue.Number, requireTargetHasNoChildren: true)
            : null;

        // Every potentially failing PATCH input has been validated before
        // this point, so the repository transition and sibling aggregate
        // fields are committed together or not at all.
        if (string.Equals(_issue.RepositoryRef, canonicalName, StringComparison.Ordinal))
            _issue.RecordRepositoryCommandReceipt(commandId, "change", expectedRevision);
        else
            _issue.ChangeRepository(canonicalName, commandId, expectedRevision);

        IReadOnlyDictionary<string, string>? labelsForUpdate = null;
        if (hasLabels)
        {
            labelsForUpdate = command.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        _issue.Update(
            hasTitle ? command.Title : null,
            hasBody ? command.Body : null,
            labelsForUpdate,
            hasPriority ? command.Priority : null,
            hasRisk ? command.Risk : null,
            hasRisk);

        if (hasIsDraft && command.IsDraft.HasValue)
            _issue.SetDraft(command.IsDraft.Value);

        ApplyWorkflowSelection(hasWorkflowProfile, command.WorkflowProfileId, hasNoWorkflow, command.NoWorkflow);

        if (hasParent)
        {
            if (command.ParentIssueNumber is null)
                _issue.RemoveParent();
            else
                _issue.AssignParent(parent!.Number);
        }

        await SaveIssueAsync();

        if (hasAttachments)
        {
            if (command.AttachmentIds is null)
            {
                await _attachmentService.UnbindAllIssueAsync(_issue.ProjectId, _issue.Number);
            }
            else
            {
                await _attachmentService.ReplaceIssueAsync(_issue.ProjectId, _issue.Number, command.AttachmentIds);
            }
        }

        return Coordinator.IssueBindingParticipantOutcome.Applied;
    }

    public async Task<Coordinator.IssueBindingParticipantOutcome> ReopenWithReceiptAsync(
        string commandId,
        long? expectedRevision)
    {
        EnsureIssue();
        if (_issue is null)
            throw new KeyNotFoundException($"Issue '{GrainKey}' not found");

        if (_issue.LastRepositoryCommand is { } stored
            && string.Equals(stored.CommandId, commandId, StringComparison.Ordinal)
            && stored.Kind == "reopen"
            && string.Equals(stored.RepositoryName, _issue.RepositoryRef ?? string.Empty, StringComparison.Ordinal))
        {
            return Coordinator.IssueBindingParticipantOutcome.AlreadyApplied;
        }

        var targetExists = await ResolveReopenTargetExistsAsync();
        _issue.ReopenWithReceipt(targetExists, commandId, expectedRevision);
        await SaveIssueAsync();
        return Coordinator.IssueBindingParticipantOutcome.Applied;
    }

    public Task<long> GetRepositoryBindingRevisionAsync()
    {
        if (_issue is null) return Task.FromResult(0L);
        return Task.FromResult(_issue.RepositoryBindingRevision);
    }
}
