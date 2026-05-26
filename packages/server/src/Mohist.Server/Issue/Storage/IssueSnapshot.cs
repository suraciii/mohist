using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Issue.Domain;

namespace Mohist.Server.Issue.Storage;

public sealed class IssueSnapshot
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Body { get; set; }
    public string[] Labels { get; set; } = [];
    public string Priority { get; set; } = "p2";
    public string? Model { get; set; }
    public Dictionary<string, string>? StageModels { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public string? WorkflowRunId { get; set; }
    [JsonPropertyName("Status")]
    public IssueStage Stage { get; set; } = IssueStage.Backlog;
    public IssueAttention? Attention { get; set; }
    [JsonPropertyName("ApprovalState")]
    public StageApproval? StageApproval { get; set; }
    public int RetryCount { get; set; }
    public int ConflictRetryCount { get; set; }
    public string? BlockedReason { get; set; }
    public int[] PrerequisiteNumbers { get; set; } = [];
    public string? WorkflowProfileId { get; set; }

    [JsonPropertyName("Stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LegacyStage { get; set; }

    [JsonPropertyName("RuntimeStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LegacyRuntimeStatus { get; set; }

    public static IssueSnapshot FromDomain(Domain.Issue issue) => new()
    {
        Id = issue.Id,
        ProjectId = issue.ProjectId,
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        Labels = issue.Labels,
        Priority = issue.Priority,
        Model = issue.Model,
        StageModels = issue.StageModels,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ArchivedAt = issue.ArchivedAt,
        WorkflowRunId = issue.WorkflowRunId,
        Stage = issue.Stage,
        Attention = issue.Attention,
        StageApproval = issue.StageApproval,
        RetryCount = issue.RetryCount,
        ConflictRetryCount = issue.ConflictRetryCount,
        BlockedReason = issue.BlockedReason,
        PrerequisiteNumbers = issue.PrerequisiteNumbers,
        WorkflowProfileId = issue.WorkflowProfileId,
    };

    public Domain.Issue ToDomain()
    {
        var stage = LegacyStage is { } legacyStage ? LegacyStageName(legacyStage) : Stage;
        var attention = Attention;
        var blockedReason = BlockedReason;
        var approvalState = StageApproval;

        if (LegacyRuntimeStatus is { } legacyRuntimeStatus)
        {
            var runtime = LegacyName(legacyRuntimeStatus, ["active", "paused", "blocked", "interrupted", "closed", "completed"]);
            if (runtime == "closed")
            {
                stage = IssueStage.Cancelled;
                attention = null;
                blockedReason = null;
            }
            else if (runtime == "completed")
            {
                stage = IssueStage.Done;
                attention = null;
                blockedReason = null;
                approvalState = null;
            }
            else if (runtime == "blocked" && attention is null)
            {
                attention = IssueAttention.Blocked(WorkflowRunId, blockedReason);
            }
        }

        return Domain.Issue.Restore(
            Id,
            ProjectId,
            Number,
            Title,
            Body,
            Labels,
            Priority,
            Model,
            StageModels,
            CreatedAt == default ? DateTime.UtcNow : CreatedAt,
            UpdatedAt == default ? DateTime.UtcNow : UpdatedAt,
            ArchivedAt,
            WorkflowRunId,
            stage,
            attention,
            approvalState,
            RetryCount,
            ConflictRetryCount,
            blockedReason,
            PrerequisiteNumbers,
            WorkflowProfileId);
    }

    private static IssueStage LegacyStageName(JsonElement value)
    {
        var stage = LegacyName(value, ["backlog", "plan", "build", "check", "integrate", "done"]);
        return stage switch
        {
            "done" => IssueStage.Done,
            "backlog" => IssueStage.Backlog,
            _ => IssueStage.InProgress,
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
