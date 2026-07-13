namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Read-only inventory of every CloudEvents <c>type</c> value used in the system.
/// This is a documentation / introspection surface, not a dispatch table — the bus
/// dispatches on <see cref="CloudNative.CloudEvents.CloudEvent.Type"/> directly.
/// </summary>
public static class EventCatalog
{
    /// <summary>
    /// All registered CloudEvents <c>type</c> values. New events must be added here
    /// AND must have a producer that calls <c>bus.Emit</c> with that exact type string.
    /// Generic session runtime event names are listed here for subscription discovery,
    /// but they flow through the dedicated transcript SignalR channel instead of the
    /// domain EventBridge path.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // === Agent-detail / transcript vocabulary (legacy agent-detail names) ===
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
        // === Generic session runtime event names (transcript channel) ===
        "session.input",
        "message.delta",
        "reasoning.delta",
        "tool_call.started",
        "tool_call.updated",
        "tool_call.completed",
        "session.closed",
        "session.liveness",
        "usage.updated",
        "model.resolved",
        "compaction",
        "compaction_event",
        "context_health_update",
        // === Reverse-DNS names (com.mohist.*) — preferred for new emits ===
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
        ReverseDns.CheckStarted,
        ReverseDns.CheckPassed,
        ReverseDns.CheckFailed,
        ReverseDns.CheckPending,
        ReverseDns.RepairScheduled,
        ReverseDns.WorkflowArtifactRecorded,
        ReverseDns.AgentSessionRuntimeBound,
        ReverseDns.AgentSessionUsageRecorded,
        ReverseDns.AgentSessionModelChanged,
        ReverseDns.AgentSessionContextCompacted,
        ReverseDns.AgentSessionContextExhausted,
        ReverseDns.AgentSessionContextHealthUpdated,
        ReverseDns.RunnerDisconnected,
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
        ReverseDns.IssueArchived,
        ReverseDns.IssueUnarchived,
        ReverseDns.IssueReopened,
        ReverseDns.InboxItemPersisted,
        ReverseDns.EpicCreated,
        ReverseDns.EpicUpdated,
        ReverseDns.EpicPriorityChanged,
        ReverseDns.EpicIssueLinked,
        ReverseDns.EpicIssueUnlinked,
        ReverseDns.EpicStatusChanged,
        ReverseDns.EpicClosed,
        ReverseDns.EpicReopened,
        ReverseDns.EpicStartAttemptFailed,
    };

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
        public const string CheckStarted = "com.mohist.workflow.check.started";
        public const string CheckPassed = "com.mohist.workflow.check.passed";
        public const string CheckFailed = "com.mohist.workflow.check.failed";
        public const string CheckPending = "com.mohist.workflow.check.pending";
        public const string RepairScheduled = "com.mohist.workflow.repair-scheduled";
        public const string WorkflowArtifactRecorded = "com.mohist.workflow.artifact.recorded";

        public const string AgentSessionRuntimeBound = "com.mohist.agent-session.runtime-bound";
        public const string AgentSessionUsageRecorded = "com.mohist.agent-session.usage-recorded";
        public const string AgentSessionModelChanged = "com.mohist.agent-session.model-changed";
        public const string AgentSessionContextCompacted = "com.mohist.agent-session.context-compacted";
        public const string AgentSessionContextExhausted = "com.mohist.agent-session.context-exhausted";
        public const string AgentSessionContextHealthUpdated = "com.mohist.agent-session.context-health-updated";

        public const string RunnerDisconnected = "com.mohist.runner.disconnected";

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
        public const string IssueArchived = "com.mohist.issue.archived";
        public const string IssueUnarchived = "com.mohist.issue.unarchived";
        public const string IssueReopened = "com.mohist.issue.reopened";

        public const string EpicCreated = "com.mohist.epic.created";
        public const string EpicUpdated = "com.mohist.epic.updated";
        public const string EpicPriorityChanged = "com.mohist.epic.priority-changed";
        public const string EpicIssueLinked = "com.mohist.epic.issue-linked";
        public const string EpicIssueUnlinked = "com.mohist.epic.issue-unlinked";
        public const string EpicStatusChanged = "com.mohist.epic.status-changed";
        public const string EpicClosed = "com.mohist.epic.closed";
        public const string EpicReopened = "com.mohist.epic.reopened";
        public const string EpicStartAttemptFailed = "com.mohist.epic.start-attempt-failed";

        public const string InboxItemPersisted = "com.mohist.inbox.item-persisted";
    }
}
