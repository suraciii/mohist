using System.Text.Json.Serialization;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowAttentionReason
{
    [JsonStringEnumMemberName("review_required")] ReviewRequired,
    [JsonStringEnumMemberName("blocked")] Blocked,
    [JsonStringEnumMemberName("agent-result-unconfirmed")] AgentResultUnconfirmed,
    [JsonStringEnumMemberName("merge_conflict")] MergeConflict,
    [JsonStringEnumMemberName("approval_rejected")] ApprovalRejected,
    [JsonStringEnumMemberName("missing_prerequisite")] MissingPrerequisite,
    [JsonStringEnumMemberName("workflow_failed")] WorkflowFailed,
    [JsonStringEnumMemberName("paused")] Paused,
}
