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

    public void Update(
        string? title,
        string? body,
        IReadOnlyDictionary<string, string>? labels,
        string? priority,
        string? risk = null,
        bool updateRisk = false,
        DateTime? now = null)
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
        if (updateRisk)
        {
            var nextRisk = IssueRisk.From(risk)?.Value;
            if (!string.Equals(_risk, nextRisk, StringComparison.Ordinal))
            {
                _risk = nextRisk;
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
        StartWorkflow(wrId, repository: null, workspace: null, context: null, workspaceName: null, now);

    public void StartWorkflow(
        string wrId,
        IssueWorkStartedRepository? repository,
        IssueWorkStartedWorkspace? workspace,
        IssueWorkStartedContext? context,
        string? workspaceName,
        DateTime? now = null)
    {
        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_workflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_workflowRunId}");
        _workflowRunId = NormalizeOptional(wrId);
        _workspaceName = NormalizeOptional(workspaceName);
        _status = IssueStatus.InProgress;
        _hasWorkflowStarted = true;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(now);
        RecordEvent(new IssueWorkStarted(
            wrId,
            repository,
            workspace,
            context,
            workspaceName));
    }

    public IssueStartBlocker? StartBlocker(
        IReadOnlySet<int>? undeliveredPrerequisites,
        bool hasChildren = false)
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

    public bool CanStart(IReadOnlySet<int>? undeliveredPrerequisites, bool hasChildren = false) =>
        StartBlocker(undeliveredPrerequisites, hasChildren) is null;

    public void Start(
        string wrId,
        IReadOnlySet<int>? undeliveredPrerequisites,
        DateTime? now = null,
        bool hasChildren = false) =>
        Start(wrId, undeliveredPrerequisites, repository: null, workspace: null, context: null, workspaceName: null, now, hasChildren);

    public void Start(
        string wrId,
        IReadOnlySet<int>? undeliveredPrerequisites,
        IssueWorkStartedRepository? repository,
        IssueWorkStartedWorkspace? workspace,
        IssueWorkStartedContext? context,
        string? workspaceName,
        DateTime? now = null,
        bool hasChildren = false)
    {
        var blocker = StartBlocker(undeliveredPrerequisites, hasChildren);
        if (blocker is IssueStartBlocker.Draft)
            throw new IssueStartBlockedException(blocker, $"Issue #{Number} is still a draft and cannot be started");
        if (blocker is IssueStartBlocker.WaitingFor waiting)
            throw new IssueStartBlockedException(blocker, $"Issue #{Number} is waiting for prerequisite issue #{waiting.PrerequisiteNumber}");

        if (_status == IssueStatus.Cancelled || _status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}");
        if (_workflowRunId is not null)
            throw new InvalidOperationException($"Issue #{Number} already has workflow {_workflowRunId}");

        _workflowRunId = NormalizeOptional(wrId);
        _workspaceName = NormalizeOptional(workspaceName);
        _status = IssueStatus.InProgress;
        _hasWorkflowStarted = true;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(now);
        RecordEvent(new IssueWorkStarted(
            wrId,
            repository,
            workspace,
            context,
            workspaceName));
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
        if (_parentIssueNumber is not null)
            throw new IssueChildCannotJoinEpicException(Number);
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

    public bool AssignParent(int parentIssueNumber, DateTime? now = null)
    {
        if (parentIssueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(parentIssueNumber));
        if (parentIssueNumber == Number)
            throw new IssueSelfParentException(Number);
        if (_epicNumber is not null)
            throw new IssueEpicMemberCannotBecomeChildException(Number, _epicNumber.Value);
        if (_status != IssueStatus.Backlog || _hasWorkflowStarted)
            throw new IssueCannotBecomeChildException(Number, _status, _hasWorkflowStarted);
        return ChangeParent(parentIssueNumber, now);
    }

    public bool RemoveParent(DateTime? now = null) => ChangeParent(null, now);

    private bool ChangeParent(int? parentIssueNumber, DateTime? now)
    {
        if (_parentIssueNumber == parentIssueNumber) return false;
        var previous = _parentIssueNumber;
        _parentIssueNumber = parentIssueNumber;
        Touch(now);
        RecordEvent(new IssueParentChanged(previous, parentIssueNumber));
        return true;
    }

    /// <summary>
    /// Transition a parent issue from Backlog to InProgress via composite
    /// advancement. The parent never owns a workflow run: <c>WorkflowRunId</c>,
    /// <c>HasWorkflowStarted</c>, and <c>RepositoryBindingRevision</c> are
    /// deliberately untouched. Records <see cref="IssueCompositeStarted"/>; a
    /// no-op when the issue is already <see cref="IssueStatus.InProgress"/>.
    /// Throws when the caller passes an empty children snapshot, so a
    /// last-detach race cannot leave a non-parent in a composite state.
    /// </summary>
    public bool MarkCompositeStarted(IReadOnlyCollection<ChildSnapshot> childrenSnapshot, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(childrenSnapshot);
        if (childrenSnapshot.Count == 0)
            throw new IssueEmptyCompositeSnapshotException(Number);
        if (_status == IssueStatus.InProgress) return false;
        if (_status != IssueStatus.Backlog)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only Backlog can start composite");
        var at = now ?? DateTime.UtcNow;
        _status = IssueStatus.InProgress;
        Touch(at);
        RecordEvent(new IssueCompositeStarted());
        return true;
    }

    /// <summary>
    /// Transition a parent issue from InProgress to Done via aggregation
    /// (all children terminal with at least one Done). Sets <c>CompletedAt</c>;
    /// no <c>WorkflowRunId</c> match (the parent has none). No-op when already
    /// <see cref="IssueStatus.Done"/>. Throws on an empty children snapshot.
    /// </summary>
    public bool MarkCompositeDone(IReadOnlyCollection<ChildSnapshot> childrenSnapshot, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(childrenSnapshot);
        if (childrenSnapshot.Count == 0)
            throw new IssueEmptyCompositeSnapshotException(Number);
        if (_status == IssueStatus.Done) return false;
        if (_status != IssueStatus.InProgress)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only InProgress can complete composite");
        if (RecomputeCompositeStatus(childrenSnapshot) != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} children snapshot does not yield aggregated Done");
        var at = now ?? DateTime.UtcNow;
        _completedAt = at;
        _status = IssueStatus.Done;
        Touch(at);
        RecordEvent(new IssueCompositeStatusChanged(
            PreviousStatus: StatusToWire(IssueStatus.InProgress),
            NewStatus: StatusToWire(IssueStatus.Done)));
        return true;
    }

    /// <summary>
    /// Transition a parent issue to Cancelled via aggregation (all children
    /// Cancelled). Does not set <c>CompletedAt</c> — the parent never had a
    /// workflow run to complete. No-op when already
    /// <see cref="IssueStatus.Cancelled"/>. Throws on an empty children
    /// snapshot.
    /// </summary>
    public bool MarkCompositeCancelled(IReadOnlyCollection<ChildSnapshot> childrenSnapshot, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(childrenSnapshot);
        if (childrenSnapshot.Count == 0)
            throw new IssueEmptyCompositeSnapshotException(Number);
        if (_status == IssueStatus.Cancelled) return false;
        if (RecomputeCompositeStatus(childrenSnapshot) != IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} children snapshot does not yield aggregated Cancelled");
        var previous = _status;
        var at = now ?? DateTime.UtcNow;
        _status = IssueStatus.Cancelled;
        Touch(at);
        RecordEvent(new IssueCompositeStatusChanged(
            PreviousStatus: StatusToWire(previous),
            NewStatus: StatusToWire(IssueStatus.Cancelled)));
        return true;
    }

    /// <summary>
    /// Reopen a cancelled parent issue back to Backlog without consulting
    /// the repository coordinator: the parent has no executable target.
    /// No-op when not <see cref="IssueStatus.Cancelled"/>.
    /// </summary>
    public bool ReopenComposite(DateTime? now = null)
    {
        if (_status != IssueStatus.Cancelled) return false;
        var at = now ?? DateTime.UtcNow;
        _status = IssueStatus.Backlog;
        Touch(at);
        RecordEvent(new IssueCompositeStatusChanged(
            PreviousStatus: StatusToWire(IssueStatus.Cancelled),
            NewStatus: StatusToWire(IssueStatus.Backlog)));
        return true;
    }

    private static string StatusToWire(IssueStatus status) => status switch
    {
        IssueStatus.Backlog => "backlog",
        IssueStatus.InProgress => "inProgress",
        IssueStatus.Done => "done",
        IssueStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>
    /// Pure decision method: given a children snapshot, return the parent
    /// status the aggregate would transition to. Throws when the snapshot is
    /// empty (a non-parent has no children to aggregate over). Does not
    /// mutate the aggregate; the caller compares to <see cref="Status"/> and
    /// applies the matching transition when they differ.
    /// </summary>
    public IssueStatus RecomputeCompositeStatus(IReadOnlyCollection<ChildSnapshot> childrenSnapshot)
    {
        ArgumentNullException.ThrowIfNull(childrenSnapshot);
        if (childrenSnapshot.Count == 0)
            throw new IssueEmptyCompositeSnapshotException(Number);

        var inProgressCount = 0;
        var doneCount = 0;
        var cancelledCount = 0;
        var backlogCount = 0;
        foreach (var child in childrenSnapshot)
        {
            ArgumentNullException.ThrowIfNull(child);
            switch (child.Status)
            {
                case IssueStatus.Backlog:
                    backlogCount++;
                    break;
                case IssueStatus.InProgress:
                    inProgressCount++;
                    break;
                case IssueStatus.Done:
                    doneCount++;
                    break;
                case IssueStatus.Cancelled:
                    cancelledCount++;
                    break;
            }
        }

        // Any running child flips the parent to InProgress (covers
        // "any-running → InProgress" and the Backlog-only case below).
        if (inProgressCount > 0) return IssueStatus.InProgress;
        // All children still Backlog — the parent is Backlog.
        if (backlogCount == childrenSnapshot.Count) return IssueStatus.Backlog;
        // Mixed terminal + Backlog (e.g. some children Done, some still
        // Backlog) means a child is still scheduled to start; treat as
        // InProgress so the next child event redrives completion.
        if (backlogCount > 0) return IssueStatus.InProgress;
        if (cancelledCount == childrenSnapshot.Count) return IssueStatus.Cancelled;
        return IssueStatus.Done;
    }

    public bool Complete(string workflowRunId, DateTime? now = null)
    {
        if (_workflowRunId != workflowRunId) return false;
        return CompleteCore(workflowRunId, IssueCompletionKinds.Workflow, now);
    }

    public bool MarkDone(DateTime? now = null)
    {
        if (_status == IssueStatus.Done) return false;
        var workflowRunId = _workflowRunId
            ?? throw new InvalidOperationException($"Issue #{Number} has no workflow run to complete");
        return CompleteCore(workflowRunId, IssueCompletionKinds.Manual, now);
    }

    private bool CompleteCore(string workflowRunId, string completionKind, DateTime? now)
    {
        if (_status == IssueStatus.Done) return false;
        if (_status != IssueStatus.InProgress)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only InProgress can complete");
        var completedAt = now ?? DateTime.UtcNow;
        _completedAt = completedAt;
        _status = IssueStatus.Done;
        _repositoryBindingRevision = NextRevision(_repositoryBindingRevision, null);
        Touch(completedAt);
        RecordEvent(new IssueCompleted(workflowRunId, completionKind));
        return true;
    }

    public void Archive(DateTime? now = null)
    {
        if (_status != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {_status}, only Done can archive");
        ArchiveForced(now);
    }

    public void ArchiveForced(DateTime? now = null)
    {
        if (_archivedAt is not null) return;
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

    public void Close(
        IReadOnlyCollection<ChildSnapshot> childrenSnapshot,
        string? reason = null,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(childrenSnapshot);
        if (childrenSnapshot.Count == 0)
            throw new IssueEmptyCompositeSnapshotException(Number);
        var nonTerminalChildNumbers = childrenSnapshot
            .Where(child => child.Status is IssueStatus.Backlog or IssueStatus.InProgress)
            .Select(child => child.Number)
            .Order()
            .ToArray();
        if (nonTerminalChildNumbers.Length > 0)
            throw new IssueParentHasNonTerminalChildrenException(Number, nonTerminalChildNumbers);
        Close(reason, now);
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
