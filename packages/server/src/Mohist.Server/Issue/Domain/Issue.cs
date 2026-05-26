namespace Mohist.Server.Issue.Domain;

public class Issue
{
    public string Id { get; }
    public string ProjectId { get; }
    public int Number { get; }
    public string Title { get; private set; }
    public string? Body { get; private set; }
    public string[] Labels { get; private set; }
    public string Priority { get; private set; }
    public string? Model { get; private set; }
    public Dictionary<string, string>? StageModels { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? ArchivedAt { get; private set; }
    public string? WorkflowRunId { get; private set; }
    public IssueStage Stage { get; private set; } = IssueStage.Backlog;
    public IssueAttention? Attention { get; private set; }
    public StageApproval? StageApproval { get; private set; }
    public int RetryCount { get; private set; }
    public int ConflictRetryCount { get; private set; }
    public string? BlockedReason { get; private set; }
    public int[] PrerequisiteNumbers { get; private set; } = [];
    public string? WorkflowProfileId { get; private set; }

    public Issue(
        string id,
        string projectId,
        int number,
        string title,
        string? body = null,
        string[]? labels = null,
        string priority = "p2",
        string? model = null,
        Dictionary<string, string>? stageModels = null,
        string? workflowProfileId = null)
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
        WorkflowProfileId = string.IsNullOrWhiteSpace(workflowProfileId) ? null : workflowProfileId;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    internal static Issue Restore(
        string id,
        string projectId,
        int number,
        string title,
        string? body,
        string[] labels,
        string priority,
        string? model,
        Dictionary<string, string>? stageModels,
        DateTime createdAt,
        DateTime updatedAt,
        DateTime? archivedAt,
        string? workflowRunId,
        IssueStage stage,
        IssueAttention? attention,
        StageApproval? approvalState,
        int retryCount,
        int conflictRetryCount,
        string? blockedReason,
        int[] prerequisiteNumbers,
        string? workflowProfileId)
    {
        var issue = new Issue(id, projectId, number, title, body, labels, priority, model, stageModels, workflowProfileId)
        {
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ArchivedAt = archivedAt,
            WorkflowRunId = workflowRunId,
            Stage = stage,
            Attention = attention,
            StageApproval = approvalState,
            RetryCount = retryCount,
            ConflictRetryCount = conflictRetryCount,
            BlockedReason = blockedReason,
            PrerequisiteNumbers = prerequisiteNumbers,
        };
        return issue;
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

    public void MarkReady()
    {
        if (Stage == IssueStage.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is cancelled");
        Stage = IssueStage.Todo;
        UpdatedAt = DateTime.UtcNow;
    }

    public void StartWorkflow(string wrId)
    {
        if (Stage == IssueStage.Cancelled || Stage == IssueStage.Done)
            throw new InvalidOperationException($"Issue #{Number} is {Stage}");
        WorkflowRunId = wrId;
        Stage = IssueStage.InProgress;
        Attention = null;
        BlockedReason = null;
        StageApproval = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        Stage = IssueStage.Done;
        Attention = null;
        BlockedReason = null;
        StageApproval = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RequestAttention(IssueAttention attention)
    {
        Attention = attention;
        if (attention.Reason is IssueAttentionReasons.Blocked or IssueAttentionReasons.WorkflowFailed)
            BlockedReason = attention.Message;
        else
            BlockedReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ClearAttention()
    {
        Attention = null;
        BlockedReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetStageApproval(StageApproval? state)
    {
        StageApproval = state;
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
        Stage = IssueStage.Cancelled;
        WorkflowRunId = null;
        Attention = null;
        StageApproval = null;
        BlockedReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reopen()
    {
        if (Stage != IssueStage.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is not cancelled");
        Stage = IssueStage.Backlog;
        UpdatedAt = DateTime.UtcNow;
    }

}
