using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Issue.Domain;

public sealed partial class Issue
{
    private string _title = null!;
    private string? _body;
    private Dictionary<string, string> _labels = new(StringComparer.Ordinal);
    private IssuePriority _priority = IssuePriority.Default;
    private string? _risk;
    private DateTime _updatedAt;
    private DateTime? _archivedAt;
    private DateTime? _completedAt;
    private string? _workflowRunId;
    private int? _epicNumber;
    private int? _parentIssueNumber;
    private IssueStatus _status = IssueStatus.Backlog;
    private int[] _prerequisiteNumbers = [];
    private IssueRepositoryRef? _repositoryRef;
    private bool _isDraft;
    private string? _workflowProfileId;
    private bool _hasWorkflowStarted;
    private long _repositoryBindingRevision;
    private IssueRepositoryBindingReceipt? _lastRepositoryCommand;
    private readonly List<IssueEvent> _pendingEvents = new();

    public required string ProjectId { get; init; }
    public required int Number { get; init; }

    public required string Title
    {
        get => _title;
        init => _title = RequireTitle(value);
    }

    public string? Body
    {
        get => _body;
        init => _body = value;
    }

    public IReadOnlyDictionary<string, string> Labels
    {
        get => _labels;
        init => ReplaceLabels(value, recordEvent: false);
    }

    public string Priority
    {
        get => _priority.Value;
        init => _priority = IssuePriority.From(value);
    }

    public string? Risk
    {
        get => _risk;
        init => _risk = IssueRisk.From(value)?.Value;
    }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        init => _updatedAt = value;
    }

    public DateTime? ArchivedAt
    {
        get => _archivedAt;
        init => _archivedAt = value;
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        init => _completedAt = value;
    }

    /// <summary>
    /// Workflow run reference. Records a workflow run that was once bound to
    /// this issue. This is an execution fact, not an indicator that the
    /// workflow is currently active or controllable: it survives <c>Done</c>,
    /// <c>Archive</c>, and <c>Cancel</c>/<c>Close</c>. Whether a workflow is
    /// active/controllable is a derived judgment from the issue's status and
    /// the workflow run's state, never from the mere presence of this id.
    /// </summary>
    public string? WorkflowRunId
    {
        get => _workflowRunId;
        init => _workflowRunId = NormalizeOptional(value);
    }

    public int? EpicNumber
    {
        get => _epicNumber;
        init => _epicNumber = value is > 0 ? value : null;
    }

    public int? ParentIssueNumber
    {
        get => _parentIssueNumber;
        init => _parentIssueNumber = value is > 0 ? value : null;
    }

    public IssueStatus Status
    {
        get => _status;
        init => _status = value;
    }

    [JsonIgnore]
    public IReadOnlyList<IssueEvent> PendingEvents => _pendingEvents;

    public void ClearPendingEvents() => _pendingEvents.Clear();

    private void RecordEvent(IssueEvent evt) => _pendingEvents.Add(evt);

    public int[] PrerequisiteNumbers
    {
        get => [.. _prerequisiteNumbers];
        init => _prerequisiteNumbers = value ?? [];
    }

    public bool IsDraft
    {
        get => _isDraft;
        init => _isDraft = value;
    }

    /// <summary>
    /// Issue-level workflow profile selection. <c>null</c> means "no
    /// issue-level selection; inherit default" and is the single source of
    /// truth for the profile id projected by every read surface.
    /// </summary>
    public string? WorkflowProfileId
    {
        get => _workflowProfileId;
        init => _workflowProfileId = NormalizeOptional(value);
    }

    public string? RepositoryRef
    {
        get => _repositoryRef?.Value;
        init => _repositoryRef = IssueRepositoryRef.From(value);
    }

    /// <summary>
    /// Set atomically with the first successful workflow start. Survives
    /// <c>Done</c>, <c>Cancel</c>/<c>Close</c>, <c>Archive</c>,
    /// <c>Reopen</c>, and any cleared/stopped-run reference. Repository
    /// reassignment and start serialization both rely on this fact
    /// rather than the workflow-run id or current status.
    /// </summary>
    public bool HasWorkflowStarted
    {
        get => _hasWorkflowStarted;
        init => _hasWorkflowStarted = value;
    }

    /// <summary>
    /// Coordination revision incremented on every create, repository
    /// reassignment, first start, completion, cancellation, and reopen.
    /// The coordinator compares incoming <c>expectedRevision</c>
    /// against this value to detect stale PATCH/POST payloads.
    /// </summary>
    public long RepositoryBindingRevision
    {
        get => _repositoryBindingRevision;
        init => _repositoryBindingRevision = value;
    }

    /// <summary>
    /// Last applied repository-binding receipt. A successful no-op
    /// reassignment still records a receipt so a lost response cannot
    /// replay as a post-start lock failure on the next coordinator
    /// activation.
    /// </summary>
    public IssueRepositoryBindingReceipt? LastRepositoryCommand
    {
        get => _lastRepositoryCommand;
        init => _lastRepositoryCommand = value;
    }

    private static string RequireTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Issue title is required", nameof(title));
        return title;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void Touch(DateTime? now = null)
    {
        _updatedAt = now ?? DateTime.UtcNow;
    }
}
