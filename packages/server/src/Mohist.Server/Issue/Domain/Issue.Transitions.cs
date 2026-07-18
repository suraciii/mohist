using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Issue.Domain;

public sealed partial class Issue
{
    public static Issue Create(
        string projectId,
        int number,
        string title,
        string? body = null,
        IReadOnlyDictionary<string, string>? labels = null,
        string priority = "p2",
        string? repositoryRef = null,
        string? risk = null,
        bool isDraft = true,
        string? workflowProfileId = null,
        string? commandId = null,
        long? expectedRevision = null,
        DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryRef))
            throw new ArgumentException("Issue target repository must be resolved before Create", nameof(repositoryRef));
        var canonicalRepository = repositoryRef.Trim();

        var createdAt = now ?? DateTime.UtcNow;
        var issue = new Issue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Body = body,
            Priority = priority,
            Risk = risk,
            RepositoryRef = canonicalRepository,
            IsDraft = isDraft,
            WorkflowProfileId = workflowProfileId,
            RepositoryBindingRevision = NextRevision(0, expectedRevision),
            LastRepositoryCommand = commandId is null
                ? null
                : new IssueRepositoryBindingReceipt(
                    CommandId: commandId,
                    Kind: "create",
                    RepositoryName: canonicalRepository,
                    AppliedRevision: NextRevision(0, expectedRevision),
                    AppliedAt: createdAt),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        issue.ReplaceLabels(labels ?? new Dictionary<string, string>(StringComparer.Ordinal), recordEvent: false);
        issue.RecordEvent(new IssueCreated(
            Title: title,
            Priority: priority,
            Labels: issue.SnapshotLabels(),
            Risk: risk,
            RepositoryRef: canonicalRepository));
        if (!string.IsNullOrWhiteSpace(workflowProfileId))
        {
            issue.RecordEvent(new IssueWorkflowProfileChanged(issue.WorkflowProfileId));
        }
        return issue;
    }

    public void Update(string? title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, DateTime? now = null)
    {
        var changed = false;
        var labelsChanged = false;
        if (title != null)
        {
            var nextTitle = RequireTitle(title);
            if (!string.Equals(_title, nextTitle, StringComparison.Ordinal))
            {
                _title = nextTitle;
                changed = true;
            }
        }
        if (body != null && !string.Equals(_body, body, StringComparison.Ordinal))
        {
            _body = body;
            changed = true;
        }
        if (labels != null)
        {
            labelsChanged = !LabelsMatch(labels);
            ReplaceLabels(labels, recordEvent: true, now: now);
            changed = changed || labelsChanged;
        }
        if (priority != null)
        {
            var oldPriority = _priority.Value;
            var newPriority = IssuePriority.From(priority).Value;
            _priority = IssuePriority.From(priority);
            if (oldPriority != newPriority)
            {
                RecordEvent(new IssuePriorityChanged(oldPriority, newPriority));
                changed = true;
            }
        }
        if (changed && !labelsChanged) Touch(now);
    }

    /// <summary>
    /// Replace the issue-level workflow profile selection. <c>null</c>
    /// clears the selection so reads fall back to project/system default.
    /// Caller is responsible for validating that <paramref name="profileId"/>
    /// (when non-null) refers to a known workflow profile; the aggregate
    /// only stores the value.
    /// </summary>
    public void ReplaceWorkflowProfile(string? profileId, DateTime? now = null)
    {
        var next = NormalizeOptional(profileId);
        if (string.Equals(_workflowProfileId, next, StringComparison.Ordinal)) return;
        _workflowProfileId = next;
        Touch(now);
        RecordEvent(new IssueWorkflowProfileChanged(_workflowProfileId));
    }

    public void SetDraft(bool isDraft, DateTime? now = null)
    {
        ValidateDraftTransition();
        if (_isDraft == isDraft) return;
        var oldIsDraft = _isDraft;
        _isDraft = isDraft;
        Touch(now);
        RecordEvent(new IssueDraftChanged(oldIsDraft, isDraft));
    }

    public void ValidateDraftTransition()
    {
        if (_status == IssueStatus.InProgress || _status == IssueStatus.Done || _status == IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} has started and can no longer change draft state");
    }

    public void StartWorkflow(string wrId, DateTime? now = null) =>
        StartWorkflow(wrId, repository: null, workspace: null, context: null, now);

    public void StartWorkflow(
        string wrId,
        IssueWorkStartedRepository? repository,
        IssueWorkStartedWorkspace? workspace,
        IssueWorkStartedContext? context,
        DateTime? now = null)
    {
        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_workflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_workflowRunId}");
        _workflowRunId = NormalizeOptional(wrId);
        _status = IssueStatus.InProgress;
        _hasWorkflowStarted = true;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(now);
        RecordEvent(new IssueWorkStarted(
            wrId,
            repository,
            workspace,
            context));
    }

    public IssueStartBlocker? StartBlocker(IReadOnlySet<int>? undeliveredPrerequisites)
    {
        if (_isDraft) return new IssueStartBlocker.Draft();
        if (undeliveredPrerequisites is { Count: > 0 })
        {
            foreach (var number in _prerequisiteNumbers)
            {
                if (undeliveredPrerequisites.Contains(number))
                    return new IssueStartBlocker.WaitingFor(number);
            }
        }
        return null;
    }

    public bool CanStart(IReadOnlySet<int>? undeliveredPrerequisites) =>
        StartBlocker(undeliveredPrerequisites) is null;

    public void Start(string wrId, IReadOnlySet<int>? undeliveredPrerequisites, DateTime? now = null) =>
        Start(wrId, undeliveredPrerequisites, repository: null, workspace: null, context: null, now);

    public void Start(
        string wrId,
        IReadOnlySet<int>? undeliveredPrerequisites,
        IssueWorkStartedRepository? repository,
        IssueWorkStartedWorkspace? workspace,
        IssueWorkStartedContext? context,
        DateTime? now = null)
    {
        var blocker = StartBlocker(undeliveredPrerequisites);
        if (blocker is IssueStartBlocker.Draft)
            throw new IssueStartBlockedException(blocker, $"Issue #{Number} is still a draft and cannot be started");
        if (blocker is IssueStartBlocker.WaitingFor waiting)
            throw new IssueStartBlockedException(blocker, $"Issue #{Number} is waiting for prerequisite issue #{waiting.PrerequisiteNumber}");

        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_workflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_workflowRunId}");

        _workflowRunId = NormalizeOptional(wrId);
        _status = IssueStatus.InProgress;
        _hasWorkflowStarted = true;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(now);
        RecordEvent(new IssueWorkStarted(
            wrId,
            repository,
            workspace,
            context));
    }

    public void ClearStoppedWorkflow(string workflowRunId, DateTime? now = null)
    {
        if (_workflowRunId != workflowRunId) return;
        _workflowRunId = null;
        Touch(now);
    }

    public bool AssignEpic(int epicNumber, DateTime? now = null)
    {
        if (epicNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(epicNumber));
        return ChangeEpic(epicNumber, now);
    }

    public bool RemoveEpic(int expectedEpicNumber, DateTime? now = null)
    {
        if (expectedEpicNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEpicNumber));
        return _epicNumber == expectedEpicNumber && ChangeEpic(null, now);
    }

    private bool ChangeEpic(int? epicNumber, DateTime? now)
    {
        if (_epicNumber == epicNumber) return false;
        var previous = _epicNumber;
        _epicNumber = epicNumber;
        Touch(now);
        RecordEvent(new IssueEpicChanged(previous, epicNumber));
        return true;
    }

    public bool Complete(string workflowRunId, DateTime? now = null)
    {
        if (_workflowRunId != workflowRunId) return false;
        if (_status == IssueStatus.Done) return false;
        if (_status != IssueStatus.InProgress)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only InProgress can complete");
        var completedAt = now ?? DateTime.UtcNow;
        _completedAt = completedAt;
        _status = IssueStatus.Done;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(completedAt);
        RecordEvent(new IssueCompleted(workflowRunId));
        return true;
    }

    public void Archive(DateTime? now = null)
    {
        if (_status != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only Done can archive");
        var archivedAt = now ?? DateTime.UtcNow;
        _archivedAt = archivedAt;
        Touch(archivedAt);
        RecordEvent(new IssueArchived());
    }

    public void Unarchive(DateTime? now = null)
    {
        var wasArchived = _archivedAt is not null;
        _archivedAt = null;
        Touch(now);
        if (wasArchived) RecordEvent(new IssueUnarchived());
    }

    public void Close(string? reason = null, DateTime? now = null)
    {
        if (_status == IssueStatus.Done || _archivedAt != null)
            throw new InvalidOperationException($"Issue #{Number} cannot close");
        var completedAt = now ?? DateTime.UtcNow;
        _completedAt = completedAt;
        _status = IssueStatus.Cancelled;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(completedAt);
        RecordEvent(new IssueCancelled(reason));
    }

    /// <summary>
    /// Reopen a cancelled Issue back to <c>backlog</c>.
    /// <paramref name="targetExists"/> is the coordinator's commit-time
    /// answer to "is the stored target declaration still declared by
    /// the Project?". The aggregate refuses the transition when the
    /// answer is <c>false</c>; reads and locking are both keyed off
    /// the stored target, so a lost-target Issue cannot be brought
    /// back without operator intervention that first re-declares the
    /// repository.
    /// </summary>
    public void Reopen(bool targetExists, DateTime? now = null) =>
        ReopenCore(targetExists, commandId: null, expectedRevision: null, now);

    public void ReopenWithReceipt(
        bool targetExists,
        string commandId,
        long? expectedRevision,
        DateTime? now = null) =>
        ReopenCore(targetExists, commandId, expectedRevision, now);

    private void ReopenCore(
        bool targetExists,
        string? commandId,
        long? expectedRevision,
        DateTime? now)
    {
        if (_status != IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is not cancelled");
        if (!targetExists)
            throw new IssueRepositoryMissingOnReopenException(_repositoryRef?.Value ?? "<unset>");
        if (expectedRevision.HasValue && expectedRevision.Value != _repositoryBindingRevision)
            throw new IssueRepositoryStaleRevisionException(commandId ?? "reopen", expectedRevision.Value, _repositoryBindingRevision);
        _status = IssueStatus.Backlog;
        var nextRevision = NextRevision(_repositoryBindingRevision, expectedRevision);
        _repositoryBindingRevision = nextRevision;
        if (commandId is not null)
        {
            _lastRepositoryCommand = new IssueRepositoryBindingReceipt(
                commandId,
                "reopen",
                _repositoryRef?.Value ?? throw new InvalidOperationException($"Issue #{Number} has no stored target repository"),
                nextRevision,
                now ?? DateTime.UtcNow);
        }
        Touch(now);
        RecordEvent(new IssueReopened());
    }

    /// <summary>
    /// Reassign the Issue's target repository. The aggregate refuses
    /// the transition once <see cref="HasWorkflowStarted"/> is set; a
    /// stale <paramref name="expectedRevision"/> throws
    /// <see cref="IssueRepositoryStaleRevisionException"/>; an empty
    /// <paramref name="canonicalName"/> throws
    /// <see cref="ArgumentException"/>. The caller is responsible for
    /// having validated that <paramref name="canonicalName"/> matches a
    /// declared Project repository name before invoking this method.
    /// </summary>
    public void ChangeRepository(
        string canonicalName,
        string commandId,
        long? expectedRevision,
        DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(canonicalName))
            throw new ArgumentException("Target repository name must not be empty", nameof(canonicalName));
        var newName = canonicalName.Trim();
        if (_hasWorkflowStarted)
            throw new IssueRepositoryLockedException(Number);
        if (expectedRevision.HasValue && expectedRevision.Value != _repositoryBindingRevision)
            throw new IssueRepositoryStaleRevisionException(commandId, expectedRevision.Value, _repositoryBindingRevision);

        var oldName = _repositoryRef?.Value;
        var nextRevision = NextRevision(_repositoryBindingRevision, expectedRevision);
        _repositoryRef = new IssueRepositoryRef(newName);
        _repositoryBindingRevision = nextRevision;
        _lastRepositoryCommand = new IssueRepositoryBindingReceipt(
            CommandId: commandId,
            Kind: "change",
            RepositoryName: newName,
            AppliedRevision: nextRevision,
            AppliedAt: now ?? DateTime.UtcNow);
        Touch(now);
        RecordEvent(new IssueRepositoryChanged(
            OldRepositoryRef: oldName,
            NewRepositoryRef: newName,
            CommandId: commandId,
            ExpectedRevision: expectedRevision,
            AppliedRevision: nextRevision));
    }

    /// <summary>
    /// Record a successful no-op reassignment so a lost response
    /// cannot later replay as a post-start lock failure. The aggregate
    /// never produces a <see cref="IssueRepositoryChanged"/> event in
    /// this path; the receipt is the only persisted state change.
    /// </summary>
    public void RecordRepositoryCommandReceipt(
        string commandId,
        string kind,
        long? expectedRevision,
        DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("Command id must not be empty", nameof(commandId));
        if (expectedRevision.HasValue && expectedRevision.Value != _repositoryBindingRevision)
            throw new IssueRepositoryStaleRevisionException(commandId, expectedRevision.Value, _repositoryBindingRevision);
        var currentName = _repositoryRef?.Value
            ?? throw new InvalidOperationException($"Issue #{Number} has no stored target repository");
        var nextRevision = NextRevision(_repositoryBindingRevision, expectedRevision);
        _repositoryBindingRevision = nextRevision;
        _lastRepositoryCommand = new IssueRepositoryBindingReceipt(
            CommandId: commandId,
            Kind: kind,
            RepositoryName: currentName,
            AppliedRevision: nextRevision,
            AppliedAt: now ?? DateTime.UtcNow);
        Touch(now);
    }

    private static long NextRevision(long current, long? expected)
    {
        var baseline = expected ?? current;
        if (baseline < 0) baseline = 0;
        return baseline + 1;
    }
}
