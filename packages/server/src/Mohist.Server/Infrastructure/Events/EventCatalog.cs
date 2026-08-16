namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Read-only inventory of every CloudEvents <c>type</c> value used in the system.
/// The catalog provides stable type names only. Each producer owns the business
/// context it stamps, and producer-path specs verify that context directly.
/// </summary>
public static class EventCatalog
{
    /// <summary>
    /// Protocol CloudEvents <c>type</c> values.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ReverseDns.WorkflowRunStarted,
        ReverseDns.WorkflowRunResumed,
        ReverseDns.WorkflowRunPaused,
        ReverseDns.WorkflowRunStopped,
        ReverseDns.WorkflowRunCompleted,
        ReverseDns.WorkflowRunFailed,
        ReverseDns.WorkflowRunRetrying,
        ReverseDns.WorkflowRunRerunning,
        ReverseDns.StageStarted,
        ReverseDns.StageCompleted,
        ReverseDns.StageFailed,
        ReverseDns.StageApprovalRequested,
        ReverseDns.StageApprovalResolved,
        ReverseDns.FeedbackRequested,
        ReverseDns.TaskStarted,
        ReverseDns.TaskCompleted,
        ReverseDns.TaskFailed,
        ReverseDns.TaskInterrupted,
        ReverseDns.TaskCancelled,
        ReverseDns.AgentTaskResultUnconfirmed,
        ReverseDns.TaskBlocked,
        ReverseDns.StageBlocked,
        ReverseDns.WorkflowRunBlocked,
        ReverseDns.CheckPassed,
        ReverseDns.CheckFailed,
        ReverseDns.CheckPending,
        ReverseDns.ChecksInterrupted,
        ReverseDns.RepairScheduled,
        ReverseDns.WorkflowArtifactRecorded,
        ReverseDns.AgentSessionRuntimeBound,
        ReverseDns.AgentSessionUsageRecorded,
        ReverseDns.AgentSessionModelChanged,
        ReverseDns.AgentSessionContextCompacted,
        ReverseDns.AgentSessionContextExhausted,
        ReverseDns.AgentSessionContextHealthUpdated,
        ReverseDns.RunnerDisconnected,
        ReverseDns.AgentJobFailed,
        ReverseDns.AgentJobTerminalDelivery,
        ReverseDns.AgentJobSubagentTerminal,
        ReverseDns.AgentSessionFollowupDelivery,
        ReverseDns.IssueCompleted,
        ReverseDns.IssueCancelled,
        ReverseDns.IssueWorkStarted,
        ReverseDns.IssueCreated,
        ReverseDns.IssueLabelsChanged,
        ReverseDns.IssuePriorityChanged,
        ReverseDns.IssueDraftChanged,
        ReverseDns.IssuePrerequisiteAdded,
        ReverseDns.IssuePrerequisiteRemoved,
        ReverseDns.IssueWorkflowProfileChanged,
        ReverseDns.IssueEpicChanged,
        ReverseDns.IssueParentChanged,
        ReverseDns.IssueArchived,
        ReverseDns.IssueUnarchived,
        ReverseDns.IssueReopened,
        ReverseDns.IssueRepositoryChanged,
        ReverseDns.IssueCompositeStarted,
        ReverseDns.IssueCompositeStatusChanged,
        ReverseDns.IssueCommentAdded,
        ReverseDns.InboxItemPersisted,
        ReverseDns.WorkspaceCreated,
        ReverseDns.WorkspaceArchived,
        ReverseDns.EpicCreated,
        ReverseDns.EpicUpdated,
        ReverseDns.EpicPriorityChanged,
        ReverseDns.EpicStatusChanged,
        ReverseDns.EpicClosed,
        ReverseDns.EpicReopened,
        ReverseDns.EpicStartAttemptFailed,
        ReverseDns.GitHubIssuesLabeled,
        ReverseDns.GitHubIssuesClosed,
        ReverseDns.GitHubIssuesReopened,
        ReverseDns.GitHubPullRequestReviewed,
        ReverseDns.GitHubCheckSuiteCompleted,
    };

    /// <summary>
    /// Transcript-only vocabulary carried on the dedicated session channel.
    /// These are not CloudEvents protocol entries.
    /// </summary>
    public static readonly IReadOnlyList<string> TranscriptTypes = new[]
    {
        "tool_call",
        "agent_text_chunk",
        "main_tool_call",
        "coder_session_started",
        "coder_session_completed",
        "coder_session_failed",
        "coder_session_cancelled",
        "coder_session_status_changed",
        "coder_recovery_status",
        "plan_session_update",
        "plan_round_start",
        "plan_round_complete",
        "session.input",
        "message.delta",
        "reasoning.delta",
        "tool_call.started",
        "tool_call.updated",
        "tool_call.completed",
        "session.activity",
        "session.context_reset",
        "turn.failed",
        "session.liveness",
        "usage.updated",
        "model.resolved",
        "compaction",
        "compaction_event",
        "context_health_update",
        "provider.retry",
    };

    // Canonical envelope extension names.
    public static class Lineage
    {
        public const string ProjectId = "projectid";
        public const string WorkflowRunId = "workflowrunid";
        public const string Issue = "issue";
        public const string Epic = "epic";
        public const string Parent = "parent";
        public const string Stage = "stage";
        public const string AgentId = "agentid";
        public const string SessionId = "sessionid";
        public const string RunnerId = "runnerid";
        public const string Workspace = "workspace";
        public const string WorkspaceOriginKind = "workspaceoriginkind";
        public const string GitHubRepo = "githubrepo";
        public const string GitHubIssue = "githubissue";
    }

    /// <summary>
    /// Reverse-DNS type values for new emits. Producers should prefer these over
    /// the legacy snake_case names. The string constants must be referenced by
    /// Emit sites via this class (or appear in <see cref="All"/>) so the catalog
    /// stays the single source of truth.
    /// </summary>
    public static class ReverseDns
    {
        public const string WorkflowRunStarted = "com.mohist.workflow.run.started";
        public const string WorkflowRunResumed = "com.mohist.workflow.run.resumed";
        public const string WorkflowRunPaused = "com.mohist.workflow.run.paused";
        public const string WorkflowRunStopped = "com.mohist.workflow.run.stopped";
        public const string WorkflowRunCompleted = "com.mohist.workflow.run.completed";
        public const string WorkflowRunFailed = "com.mohist.workflow.run.failed";
        public const string WorkflowRunRetrying = "com.mohist.workflow.run.retrying";
        public const string WorkflowRunRerunning = "com.mohist.workflow.run.rerunning";

        public const string StageStarted = "com.mohist.workflow.stage.started";
        public const string StageCompleted = "com.mohist.workflow.stage.completed";
        public const string StageFailed = "com.mohist.workflow.stage.failed";
        public const string StageApprovalRequested = "com.mohist.workflow.stage.approval-requested";
        public const string StageApprovalResolved = "com.mohist.workflow.stage.approval-resolved";
        public const string FeedbackRequested = "com.mohist.workflow.feedback.requested";

        public const string TaskStarted = "com.mohist.workflow.task.started";
        public const string TaskCompleted = "com.mohist.workflow.task.completed";
        public const string TaskFailed = "com.mohist.workflow.task.failed";
        public const string TaskInterrupted = "com.mohist.workflow.task.interrupted";
        public const string TaskCancelled = "com.mohist.workflow.task.cancelled";
        public const string AgentTaskResultUnconfirmed = "com.mohist.workflow.agent-result-unconfirmed";
        public const string TaskBlocked = "com.mohist.workflow.task.blocked";
        public const string StageBlocked = "com.mohist.workflow.stage.blocked";
        public const string WorkflowRunBlocked = "com.mohist.workflow.run.blocked";
        public const string CheckPassed = "com.mohist.workflow.check.passed";
        public const string CheckFailed = "com.mohist.workflow.check.failed";
        public const string CheckPending = "com.mohist.workflow.check.pending";
        public const string ChecksInterrupted = "com.mohist.workflow.checks.interrupted";
        public const string RepairScheduled = "com.mohist.workflow.repair-scheduled";
        public const string WorkflowArtifactRecorded = "com.mohist.workflow.artifact.recorded";

        public const string AgentSessionRuntimeBound = "com.mohist.agent-session.runtime-bound";
        public const string AgentSessionUsageRecorded = "com.mohist.agent-session.usage-recorded";
        public const string AgentSessionModelChanged = "com.mohist.agent-session.model-changed";
        public const string AgentSessionContextCompacted = "com.mohist.agent-session.context-compacted";
        public const string AgentSessionContextExhausted = "com.mohist.agent-session.context-exhausted";
        public const string AgentSessionContextHealthUpdated = "com.mohist.agent-session.context-health-updated";

        public const string RunnerDisconnected = "com.mohist.runner.disconnected";

        public const string AgentJobFailed = "com.mohist.agent.job.failed";
        public const string AgentJobTerminalDelivery = "com.mohist.agent.job.terminal-delivery";
        public const string AgentJobSubagentTerminal = "com.mohist.agent.job.subagent-terminal";
        public const string AgentSessionFollowupDelivery = "com.mohist.agent.session.followup-delivery";

        public const string IssueCompleted = "com.mohist.issue.completed";
        public const string IssueCancelled = "com.mohist.issue.cancelled";
        public const string IssueWorkStarted = "com.mohist.issue.work-started";
        public const string IssueCreated = "com.mohist.issue.created";
        public const string IssueLabelsChanged = "com.mohist.issue.labels-changed";
        public const string IssuePriorityChanged = "com.mohist.issue.priority-changed";
        public const string IssueDraftChanged = "com.mohist.issue.draft-changed";
        public const string IssuePrerequisiteAdded = "com.mohist.issue.prerequisite-added";
        public const string IssuePrerequisiteRemoved = "com.mohist.issue.prerequisite-removed";
        public const string IssueWorkflowProfileChanged = "com.mohist.issue.workflow-profile-changed";
        public const string IssueEpicChanged = "com.mohist.issue.epic-changed";
        public const string IssueParentChanged = "com.mohist.issue.parent-changed";
        public const string IssueArchived = "com.mohist.issue.archived";
        public const string IssueUnarchived = "com.mohist.issue.unarchived";
        public const string IssueReopened = "com.mohist.issue.reopened";
        public const string IssueRepositoryChanged = "com.mohist.issue.repository-changed";
        public const string IssueCompositeStarted = "com.mohist.issue.composite-started";
        public const string IssueCompositeStatusChanged = "com.mohist.issue.composite-status-changed";

        public const string IssueCommentAdded = "com.mohist.issue.comment-added";

        public const string EpicCreated = "com.mohist.epic.created";
        public const string EpicUpdated = "com.mohist.epic.updated";
        public const string EpicPriorityChanged = "com.mohist.epic.priority-changed";
        public const string EpicStatusChanged = "com.mohist.epic.status-changed";
        public const string EpicClosed = "com.mohist.epic.closed";
        public const string EpicReopened = "com.mohist.epic.reopened";
        public const string EpicStartAttemptFailed = "com.mohist.epic.start-attempt-failed";

        public const string WorkspaceCreated = "com.mohist.workspace.created";
        public const string WorkspaceArchived = "com.mohist.workspace.archived";

        public const string InboxItemPersisted = "com.mohist.inbox.item-persisted";

        public const string GitHubIssuesLabeled = "com.mohist.github.issues.labeled";
        public const string GitHubIssuesClosed = "com.mohist.github.issues.closed";
        public const string GitHubIssuesReopened = "com.mohist.github.issues.reopened";
        public const string GitHubPullRequestReviewed = "com.mohist.github.pull-request.reviewed";
        public const string GitHubCheckSuiteCompleted = "com.mohist.github.check-suite.completed";
    }
}
