using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.WorkflowProfiles;

namespace Mohist.Server.Issue.Domain;

[GenerateSerializer]
public class Issue : IJsonOnDeserialized
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

    [Id(13), JsonInclude] public IssueStatus Status { get; private set; } = IssueStatus.Backlog;
    [Id(14), JsonInclude] public IssueAttention? Attention { get; private set; }
    [Id(15), JsonInclude] public ApprovalState? ApprovalState { get; private set; }
    [Id(17), JsonInclude] public int RetryCount { get; private set; }
    [Id(18), JsonInclude] public int ConflictRetryCount { get; private set; }
    [Id(19), JsonInclude] public string? BlockedReason { get; private set; }
    [Id(20), JsonInclude] public int[] PrerequisiteNumbers { get; private set; } = [];
    [Id(21), JsonInclude] public string WorkflowProfileId { get; private set; } = IssueWorkflowProfiles.DefaultId;

    [Id(100), JsonPropertyName("Stage"), JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LegacyStage { get; private set; }

    [Id(101), JsonPropertyName("RuntimeStatus"), JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LegacyRuntimeStatus { get; private set; }

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
        WorkflowProfileId = string.IsNullOrWhiteSpace(workflowProfileId) ? IssueWorkflowProfiles.DefaultId : workflowProfileId;
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

    public void MarkReady()
    {
        if (Status == IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is cancelled");
        Status = IssueStatus.Todo;
        UpdatedAt = DateTime.UtcNow;
    }

    public void StartWorkflow(string wrId)
    {
        if (Status == IssueStatus.Cancelled || Status == IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {Status}");
        WorkflowRunId = wrId;
        Status = IssueStatus.InProgress;
        Attention = null;
        BlockedReason = null;
        ApprovalState = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        Status = IssueStatus.Done;
        Attention = null;
        BlockedReason = null;
        ApprovalState = null;
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

    public void SetApprovalState(ApprovalState? state)
    {
        ApprovalState = state;
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
        if (Status != IssueStatus.Done)
            throw new InvalidOperationException($"Issue #{Number} is {Status}, only Done can archive");
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
        if (Status == IssueStatus.Done || ArchivedAt != null)
            throw new InvalidOperationException($"Issue #{Number} cannot close");
        Status = IssueStatus.Cancelled;
        WorkflowRunId = null;
        Attention = null;
        ApprovalState = null;
        BlockedReason = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reopen()
    {
        if (Status != IssueStatus.Cancelled)
            throw new InvalidOperationException($"Issue #{Number} is not cancelled");
        Status = IssueStatus.Backlog;
        UpdatedAt = DateTime.UtcNow;
    }

    public void OnDeserialized()
    {
        if (LegacyStage is { } legacyStage)
            Status = LegacyStatus(legacyStage);

        if (LegacyRuntimeStatus is not { } legacyRuntimeStatus) return;

        var runtime = LegacyName(legacyRuntimeStatus, ["active", "paused", "blocked", "interrupted", "closed", "completed"]);
        if (runtime == "closed")
        {
            Status = IssueStatus.Cancelled;
            Attention = null;
            BlockedReason = null;
        }
        else if (runtime == "completed")
        {
            Status = IssueStatus.Done;
            Attention = null;
            BlockedReason = null;
            ApprovalState = null;
        }
        else if (runtime == "blocked" && Attention is null)
        {
            Attention = IssueAttention.Blocked(WorkflowRunId, BlockedReason);
        }

        LegacyStage = null;
        LegacyRuntimeStatus = null;
    }

    private static IssueStatus LegacyStatus(JsonElement value)
    {
        var stage = LegacyName(value, ["backlog", "plan", "build", "check", "integrate", "done"]);
        return stage switch
        {
            "done" => IssueStatus.Done,
            "backlog" => IssueStatus.Backlog,
            _ => IssueStatus.InProgress,
        };
    }

    private static string LegacyName(JsonElement value, string[] names)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 && index < names.Length)
            return names[index];
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
            {
                "inprogress" => "in_progress",
                { } name => name,
                _ => names[0],
            };
        return names[0];
    }
}
