namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Read-only inventory of every CloudEvents <c>type</c> value used in the system.
/// This is the protocol registry: for every registered type, the catalog also
/// declares the set of lineage attributes that type MUST always carry on its
/// envelope (matching <c>design/event-protocol.md</c>). The bus dispatches on
/// <see cref="CloudNative.CloudEvents.CloudEvent.Type"/> directly; this catalog
/// is the single source of truth that producers stamp against and that the
/// distributed conformance assertions call.
/// </summary>
public static class EventCatalog
{
    /// <summary>
    /// Protocol CloudEvents <c>type</c> values. Every entry has a required-lineage
    /// declaration below; adding a type without one fails during static initialization.
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
        ReverseDns.IssueEpicChanged,
        ReverseDns.IssueArchived,
        ReverseDns.IssueUnarchived,
        ReverseDns.IssueReopened,
        ReverseDns.InboxItemPersisted,
        ReverseDns.EpicCreated,
        ReverseDns.EpicUpdated,
        ReverseDns.EpicPriorityChanged,
        ReverseDns.EpicStatusChanged,
        ReverseDns.EpicClosed,
        ReverseDns.EpicReopened,
        ReverseDns.EpicStartAttemptFailed,
    };

    /// <summary>
    /// Transcript-only vocabulary carried on the dedicated session channel.
    /// These are not CloudEvents protocol entries and intentionally have no
    /// lineage declaration.
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
        "session.closed",
        "session.liveness",
        "usage.updated",
        "model.resolved",
        "compaction",
        "compaction_event",
        "context_health_update",
    };

    public static readonly IReadOnlySet<string> CatalogOnlyTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        ReverseDns.WorkflowRunRetrying,
        ReverseDns.WorkflowRunRerunning,
        ReverseDns.RepairScheduled,
        ReverseDns.RunnerDisconnected,
    };

    // === Lineage attribute names ===
    // The matrix below pins the protocol names — keep them in
    // sync with `design/event-protocol.md`.
    public static class Lineage
    {
        public const string ProjectId = "projectid";
        public const string WorkflowRunId = "workflowrunid";
        public const string Issue = "issue";
        public const string Epic = "epic";
        public const string Stage = "stage";
        public const string AgentId = "agentid";
        public const string SessionId = "sessionid";
        public const string RunnerId = "runnerid";
    }

    private static readonly string[] WorkflowBase = [Lineage.ProjectId, Lineage.WorkflowRunId];
    private static readonly string[] WorkflowStageBase = [Lineage.ProjectId, Lineage.WorkflowRunId, Lineage.Stage];

    private static readonly Dictionary<string, IReadOnlyList<string>> LineageRequired = new(StringComparer.Ordinal)
    {
        // === workflow.run.* ===
        [ReverseDns.WorkflowRunStarted] = WorkflowBase,
        [ReverseDns.WorkflowRunResumed] = WorkflowBase,
        [ReverseDns.WorkflowRunPaused] = WorkflowBase,
        [ReverseDns.WorkflowRunStopped] = WorkflowBase,
        [ReverseDns.WorkflowRunCompleted] = WorkflowBase,
        [ReverseDns.WorkflowRunFailed] = WorkflowBase,
        [ReverseDns.WorkflowRunRetrying] = WorkflowBase,
        [ReverseDns.WorkflowRunRerunning] = WorkflowBase,

        // === workflow.stage.* ===
        [ReverseDns.StageStarted] = WorkflowStageBase,
        [ReverseDns.StageCompleted] = WorkflowStageBase,
        [ReverseDns.StageFailed] = WorkflowStageBase,
        [ReverseDns.StageApprovalRequested] = WorkflowStageBase,
        [ReverseDns.StageApprovalResolved] = WorkflowStageBase,

        // === workflow.feedback.requested (structurally stage-bearing per D2) ===
        [ReverseDns.FeedbackRequested] = WorkflowStageBase,

        // === workflow.task.* ===
        [ReverseDns.TaskStarted] = WorkflowStageBase,
        [ReverseDns.TaskCompleted] = WorkflowStageBase,
        [ReverseDns.TaskFailed] = WorkflowStageBase,

        // === workflow.check.* ===
        [ReverseDns.CheckPassed] = WorkflowStageBase,
        [ReverseDns.CheckFailed] = WorkflowStageBase,
        [ReverseDns.CheckPending] = WorkflowStageBase,

        // === workflow.* (artifact — workflow base, no stage per D2) ===
        [ReverseDns.WorkflowArtifactRecorded] = WorkflowBase,

        // === workflow.repair-scheduled (catalog-only, no producer today) ===
        [ReverseDns.RepairScheduled] = WorkflowBase,

        // === issue.* ===
        [ReverseDns.IssueCompleted] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueCancelled] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueWorkStarted] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueCreated] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueLabelsChanged] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssuePriorityChanged] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueDraftChanged] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssuePrerequisiteAdded] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssuePrerequisiteRemoved] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueWorkflowProfileChanged] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueEpicChanged] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueArchived] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueUnarchived] = [Lineage.ProjectId, Lineage.Issue],
        [ReverseDns.IssueReopened] = [Lineage.ProjectId, Lineage.Issue],

        // === epic.* ===
        [ReverseDns.EpicCreated] = [Lineage.ProjectId, Lineage.Epic],
        [ReverseDns.EpicUpdated] = [Lineage.ProjectId, Lineage.Epic],
        [ReverseDns.EpicPriorityChanged] = [Lineage.ProjectId, Lineage.Epic],
        [ReverseDns.EpicStatusChanged] = [Lineage.ProjectId, Lineage.Epic],
        [ReverseDns.EpicClosed] = [Lineage.ProjectId, Lineage.Epic],
        [ReverseDns.EpicReopened] = [Lineage.ProjectId, Lineage.Epic],
        [ReverseDns.EpicStartAttemptFailed] = [Lineage.ProjectId, Lineage.Epic],

        // === agent-session.* ===
        [ReverseDns.AgentSessionRuntimeBound] = [Lineage.ProjectId, Lineage.SessionId],
        [ReverseDns.AgentSessionUsageRecorded] = [Lineage.ProjectId, Lineage.SessionId],
        [ReverseDns.AgentSessionModelChanged] = [Lineage.ProjectId, Lineage.SessionId],
        [ReverseDns.AgentSessionContextCompacted] = [Lineage.ProjectId, Lineage.SessionId],
        [ReverseDns.AgentSessionContextExhausted] = [Lineage.ProjectId, Lineage.SessionId],
        [ReverseDns.AgentSessionContextHealthUpdated] = [Lineage.ProjectId, Lineage.SessionId],

        // === runner.* (catalog-only: no producer today; projectid is conditional) ===
        [ReverseDns.RunnerDisconnected] = [Lineage.RunnerId],

        // === inbox-synthesized ===
        [ReverseDns.InboxItemPersisted] = [Lineage.ProjectId, Lineage.Issue],
    };

    static EventCatalog()
    {
        ValidateDeclarations(All, LineageRequired);
    }

    internal static void ValidateDeclarations(
        IReadOnlyCollection<string> registeredTypes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declarations)
    {
        ArgumentNullException.ThrowIfNull(registeredTypes);
        ArgumentNullException.ThrowIfNull(declarations);

        var duplicateTypes = registeredTypes
            .GroupBy(type => type, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        var undeclared = registeredTypes.Where(type => !declarations.ContainsKey(type)).ToArray();
        var unregistered = declarations.Keys.Where(type => !registeredTypes.Contains(type, StringComparer.Ordinal)).ToArray();
        var empty = declarations
            .Where(pair => pair.Value.Count == 0 || pair.Value.Any(string.IsNullOrWhiteSpace))
            .Select(pair => pair.Key)
            .ToArray();
        if (duplicateTypes.Length != 0 || undeclared.Length != 0 || unregistered.Length != 0 || empty.Length != 0)
        {
            throw new InvalidOperationException(
                $"Event catalog lineage declarations are invalid. Duplicates: {string.Join(", ", duplicateTypes)}. " +
                $"Undeclared: {string.Join(", ", undeclared)}. " +
                $"Unregistered: {string.Join(", ", unregistered)}. " +
                $"Empty: {string.Join(", ", empty)}.");
        }
    }

    /// <summary>
    /// The set of lineage attributes the given event <paramref name="type"/> MUST
    /// carry on its envelope. Returns an empty list when <paramref name="type"/> is
    /// not a protocol-tracked entry (e.g. transcript / legacy names that flow on a
    /// dedicated channel). Conditional attributes (issueid/issue/epicid on
    /// workflow.*, epicid on issue.*, agentid/issue/workflowrunid/stage on
    /// agent-session.*, projectid on runner.*) are deliberately not modelled here:
    /// those are validated by the producer-tasks' stamping scenarios, not by the
    /// always-required matrix.
    /// </summary>
    public static IReadOnlyList<string> RequiredAttributes(string type) =>
        LineageRequired.TryGetValue(type, out var required) ? required : [];

    /// <summary>
    /// Whether the given event <paramref name="type"/> is tracked by the protocol
    /// registry (i.e. has a lineage declaration). Catalog-only types with no
    /// producer today are still tracked so their required attributes are pinned.
    /// </summary>
    public static bool HasLineageDeclaration(string type) => LineageRequired.ContainsKey(type);

    public static bool IsMohistProtocolType(string type) =>
        type.StartsWith("com.mohist.", StringComparison.Ordinal);

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
        public const string IssueEpicChanged = "com.mohist.issue.epic-changed";
        public const string IssueArchived = "com.mohist.issue.archived";
        public const string IssueUnarchived = "com.mohist.issue.unarchived";
        public const string IssueReopened = "com.mohist.issue.reopened";

        public const string EpicCreated = "com.mohist.epic.created";
        public const string EpicUpdated = "com.mohist.epic.updated";
        public const string EpicPriorityChanged = "com.mohist.epic.priority-changed";
        public const string EpicStatusChanged = "com.mohist.epic.status-changed";
        public const string EpicClosed = "com.mohist.epic.closed";
        public const string EpicReopened = "com.mohist.epic.reopened";
        public const string EpicStartAttemptFailed = "com.mohist.epic.start-attempt-failed";

        public const string InboxItemPersisted = "com.mohist.inbox.item-persisted";
    }
}
