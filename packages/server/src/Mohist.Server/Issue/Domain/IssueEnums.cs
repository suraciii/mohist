using System.Text.Json.Serialization;

namespace Mohist.Server.Issue.Domain;

public enum IssueStage
{
    Backlog,
    Todo,
    InProgress,
    Done,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IssueAttentionReason
{
    [JsonStringEnumMemberName("review_required")] ReviewRequired,
    [JsonStringEnumMemberName("blocked")] Blocked,
    [JsonStringEnumMemberName("merge_conflict")] MergeConflict,
    [JsonStringEnumMemberName("approval_rejected")] ApprovalRejected,
    [JsonStringEnumMemberName("missing_prerequisite")] MissingPrerequisite,
    [JsonStringEnumMemberName("workflow_failed")] WorkflowFailed,
    [JsonStringEnumMemberName("paused")] Paused,
}