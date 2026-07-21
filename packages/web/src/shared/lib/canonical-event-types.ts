export const REVERSE_DNS_EVENT_TYPES = {
  WorkflowRunStarted: 'com.mohist.workflow.run.started',
  WorkflowRunResumed: 'com.mohist.workflow.run.resumed',
  WorkflowRunPaused: 'com.mohist.workflow.run.paused',
  WorkflowRunStopped: 'com.mohist.workflow.run.stopped',
  WorkflowRunCompleted: 'com.mohist.workflow.run.completed',
  WorkflowRunFailed: 'com.mohist.workflow.run.failed',
  WorkflowRunRetrying: 'com.mohist.workflow.run.retrying',
  WorkflowRunRerunning: 'com.mohist.workflow.run.rerunning',
  StageStarted: 'com.mohist.workflow.stage.started',
  StageCompleted: 'com.mohist.workflow.stage.completed',
  StageFailed: 'com.mohist.workflow.stage.failed',
  StageApprovalRequested: 'com.mohist.workflow.stage.approval-requested',
  StageApprovalResolved: 'com.mohist.workflow.stage.approval-resolved',
  TaskStarted: 'com.mohist.workflow.task.started',
  TaskCompleted: 'com.mohist.workflow.task.completed',
  TaskFailed: 'com.mohist.workflow.task.failed',
  ArtifactRecorded: 'com.mohist.workflow.artifact.recorded',
  IssueCreated: 'com.mohist.issue.created',
  IssueEpicChanged: 'com.mohist.issue.epic-changed',
  IssueCancelled: 'com.mohist.issue.cancelled',
  IssueArchived: 'com.mohist.issue.archived',
  IssueUnarchived: 'com.mohist.issue.unarchived',
  IssueReopened: 'com.mohist.issue.reopened',
  IssueWorkStarted: 'com.mohist.issue.work-started',
  IssueCompleted: 'com.mohist.issue.completed',
  InboxItemPersisted: 'com.mohist.inbox.item-persisted',
  IssueLabelsChanged: 'com.mohist.issue.labels-changed',
  IssuePriorityChanged: 'com.mohist.issue.priority-changed',
  IssuePrerequisiteAdded: 'com.mohist.issue.prerequisite-added',
  IssuePrerequisiteRemoved: 'com.mohist.issue.prerequisite-removed',
  AgentSessionRuntimeBound: 'com.mohist.agent-session.runtime-bound',
  AgentSessionUsageRecorded: 'com.mohist.agent-session.usage-recorded',
  AgentSessionModelChanged: 'com.mohist.agent-session.model-changed',
  AgentSessionContextCompacted: 'com.mohist.agent-session.context-compacted',
  AgentSessionContextExhausted: 'com.mohist.agent-session.context-exhausted',
  AgentSessionContextHealthUpdated: 'com.mohist.agent-session.context-health-updated',
} as const

export type ReverseDnsEventType =
  (typeof REVERSE_DNS_EVENT_TYPES)[keyof typeof REVERSE_DNS_EVENT_TYPES]

export const REVERSE_DNS_AGENT_SESSION_EVENT_TYPES = [
  REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound,
  REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded,
  REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged,
  REVERSE_DNS_EVENT_TYPES.AgentSessionContextCompacted,
  REVERSE_DNS_EVENT_TYPES.AgentSessionContextExhausted,
  REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated,
] as const

export const TRANSCRIPT_EVENT_TYPES = [
  'session.input',
  'message.delta',
  'reasoning.delta',
  'tool_call.started',
  'tool_call.updated',
  'tool_call.completed',
  'session.liveness',
  'usage.updated',
  'model.resolved',
  'session.closed',
  'compaction',
  'compaction_event',
  'context_health_update',
  'provider.retry',
] as const

export type TranscriptEventType = (typeof TRANSCRIPT_EVENT_TYPES)[number]

export const LEGACY_AGENT_DETAIL_EVENT_TYPES = [
  'agent_text_chunk',
  'main_tool_call',
  'coder_text_chunk',
  'coder_thought_chunk',
  'coder_tool_call',
  'coder_session_started',
  'coder_session_completed',
  'coder_session_failed',
  'coder_session_cancelled',
  'coder_session_status_changed',
  'coder_recovery_status',
  'plan_round_start',
  'plan_session_update',
  'plan_round_complete',
] as const

export const EVENT_TYPES = [
  ...LEGACY_AGENT_DETAIL_EVENT_TYPES,
  ...TRANSCRIPT_EVENT_TYPES,
  ...Object.values(REVERSE_DNS_EVENT_TYPES),
] as const

export const AGENT_DETAIL_ROUTED_EVENT_TYPES = [
  ...LEGACY_AGENT_DETAIL_EVENT_TYPES,
  ...TRANSCRIPT_EVENT_TYPES,
  ...REVERSE_DNS_AGENT_SESSION_EVENT_TYPES,
] as const

export type CanonicalEventType = (typeof EVENT_TYPES)[number]
