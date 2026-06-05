namespace Mohist.Server.Issue.Domain;

public sealed partial class Issue
{
    private string _title = null!;
    private string? _body;
    private string[] _labels = [];
    private string _priority = "p2";
    private DateTime _updatedAt;
    private DateTime? _archivedAt;
    private string? _workflowRunId;
    private IssueStatus _status = IssueStatus.Backlog;
    private int[] _prerequisiteNumbers = [];
    private string? _repositoryRef;

    public required string Id { get; init; }
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

    public string[] Labels
    {
        get => [.. _labels];
        init => _labels = value ?? [];
    }

    public string Priority
    {
        get => _priority;
        init => _priority = NormalizePriority(value);
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

    public string? WorkflowRunId
    {
        get => _workflowRunId;
        init => _workflowRunId = value;
    }

    public IssueStatus Status
    {
        get => _status;
        init => _status = value;
    }

    public int[] PrerequisiteNumbers
    {
        get => [.. _prerequisiteNumbers];
        init => _prerequisiteNumbers = value ?? [];
    }

    public string? RepositoryRef
    {
        get => _repositoryRef;
        init => _repositoryRef = NormalizeOptional(value);
    }

    private static string RequireTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Issue title is required", nameof(title));
        return title;
    }

    private static string NormalizePriority(string? priority) =>
        string.IsNullOrWhiteSpace(priority) ? "p2" : priority;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void Touch(DateTime? now = null)
    {
        _updatedAt = now ?? DateTime.UtcNow;
    }
}
