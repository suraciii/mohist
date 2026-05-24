using System.Text.Json.Serialization;

namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public class Issue
{
    [Id(0)]  public string Id { get; }
    [Id(1)]  public string ProjectId { get; }
    [Id(2)]  public int Number { get; }
    [Id(3), JsonInclude]  public string Title { get; private set; }
    [Id(4), JsonInclude]  public string? Body { get; private set; }
    [Id(5), JsonInclude]  public string[] Labels { get; private set; }
    [Id(6), JsonInclude]  public string Priority { get; private set; }
    [Id(7), JsonInclude]  public string? Model { get; private set; }
    [Id(8), JsonInclude]  public Dictionary<string, string>? StageModels { get; private set; }
    [Id(9)]  public DateTime CreatedAt { get; }
    [Id(10), JsonInclude] public DateTime UpdatedAt { get; private set; }
    [Id(11), JsonInclude] public DateTime? ArchivedAt { get; private set; }
    [Id(12), JsonInclude] public string? WorkflowRunId { get; private set; }

    [Id(13), JsonInclude] public IssueStage Stage { get; private set; } = IssueStage.Backlog;
    [Id(14), JsonInclude] public IssueRuntimeStatus RuntimeStatus { get; private set; } = IssueRuntimeStatus.Active;
    [Id(15), JsonInclude] public ApprovalState? ApprovalState { get; private set; }
    [Id(16), JsonInclude] public MergeState? MergeState { get; private set; }
    [Id(17), JsonInclude] public int RetryCount { get; private set; }
    [Id(18), JsonInclude] public int ConflictRetryCount { get; private set; }
    [Id(19), JsonInclude] public string? BlockedReason { get; private set; }
    [Id(20), JsonInclude] public int[] PrerequisiteNumbers { get; private set; } = [];

    public Issue(
        string id,
        string projectId,
        int number,
        string title,
        string? body = null,
        string[]? labels = null,
        string priority = "p2",
        string? model = null,
        Dictionary<string, string>? stageModels = null)
    {
        Id = id;
        ProjectId = projectId;
        Number = number;
        Title = title;
        Body = body;
        Labels = labels ?? [];
        Priority = priority;
        Model = model;
        StageModels = stageModels;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Update(string? title, string? body, string[]? labels, string? priority, string? model, Dictionary<string, string>? stageModels)
    {
        if (title != null) Title = title;
        if (body != null) Body = body;
        if (labels != null) Labels = labels;
        if (priority != null) Priority = priority;
        if (model != null) Model = model;
        if (stageModels != null) StageModels = stageModels;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetWorkflowRunId(string wrId)
    {
        WorkflowRunId = wrId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetStage(IssueStage stage)
    {
        Stage = stage;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetRuntimeStatus(IssueRuntimeStatus status, string? reason = null)
    {
        RuntimeStatus = status;
        if (status == IssueRuntimeStatus.Blocked)
            BlockedReason = reason;
        else if (status == IssueRuntimeStatus.Active || status == IssueRuntimeStatus.Completed)
            BlockedReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetApprovalState(ApprovalState? state)
    {
        ApprovalState = state;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetMergeState(MergeState? state)
    {
        MergeState = state;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddPrerequisite(int prerequisiteNumber)
    {
        if (prerequisiteNumber == Number)
            throw new InvalidOperationException("Issue cannot depend on itself");
        if (PrerequisiteNumbers.Contains(prerequisiteNumber)) return;
        PrerequisiteNumbers = [.. PrerequisiteNumbers, prerequisiteNumber];
        UpdatedAt = DateTime.UtcNow;
    }

    public void RemovePrerequisite(int prerequisiteNumber)
    {
        var next = PrerequisiteNumbers.Where(number => number != prerequisiteNumber).ToArray();
        if (next.Length == PrerequisiteNumbers.Length) return;
        PrerequisiteNumbers = next;
        UpdatedAt = DateTime.UtcNow;
    }

    public void IncrementRetry()
    {
        RetryCount++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void IncrementConflictRetry()
    {
        ConflictRetryCount++;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Archive()
    {
        if (Stage != IssueStage.Done)
            throw new InvalidOperationException($"Issue #{Number} is {Stage}, only Done can archive");
        ArchivedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Unarchive()
    {
        ArchivedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Close()
    {
        if (Stage == IssueStage.Done || ArchivedAt != null)
            throw new InvalidOperationException($"Issue #{Number} cannot close");
        RuntimeStatus = IssueRuntimeStatus.Closed;
        Stage = IssueStage.Backlog;
        WorkflowRunId = null;
        ApprovalState = null;
        MergeState = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reopen()
    {
        if (RuntimeStatus != IssueRuntimeStatus.Closed)
            throw new InvalidOperationException($"Issue #{Number} is not closed");
        RuntimeStatus = IssueRuntimeStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }
}
