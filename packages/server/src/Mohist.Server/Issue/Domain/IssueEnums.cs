namespace Mohist.Server.Issue.Domain;

public enum IssueStage
{
    Backlog,
    Todo,
    InProgress,
    Done,
    Cancelled
}

public static class IssueAttentionReasons
{
    public const string ReviewRequired = "review_required";
    public const string Blocked = "blocked";
    public const string MergeConflict = "merge_conflict";
    public const string ApprovalRejected = "approval_rejected";
    public const string MissingPrerequisite = "missing_prerequisite";
    public const string WorkflowFailed = "workflow_failed";
    public const string Paused = "paused";
}
