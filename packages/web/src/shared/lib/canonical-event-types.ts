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
  IssueCreated: 'com.mohist.issue.created',
  IssueClosed: 'com.mohist.issue.closed',
  IssueArchived: 'com.mohist.issue.archived',
  IssueUnarchived: 'com.mohist.issue.unarchived',
  IssueReopened: 'com.mohist.issue.reopened',
  IssueWorkStarted: 'com.mohist.issue.work-started',
  IssueWorkCompleted: 'com.mohist.issue.work-completed',
  IssueLabelsChanged: 'com.mohist.issue.labels-changed',
  IssuePriorityChanged: 'com.mohist.issue.priority-changed',
  IssuePrerequisiteAdded: 'com.mohist.issue.prerequisite-added',
  IssuePrerequisiteRemoved: 'com.mohist.issue.prerequisite-removed',
  AgentSessionRuntimeBound: 'com.mohist.agent-session.runtime-bound',
  AgentSessionUsageRecorded: 'com.mohist.agent-session.usage-recorded',
  AgentSessionModelChanged: 'com.mohist.agent-session.model-changed',
} as const

export type ReverseDnsEventType =
  (typeof REVERSE_DNS_EVENT_TYPES)[keyof typeof REVERSE_DNS_EVENT_TYPES]

export const REVERSE_DNS_AGENT_SESSION_EVENT_TYPES = [
  REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound,
  REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded,
  REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged,
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
] as const

export type TranscriptEventType = (typeof TRANSCRIPT_EVENT_TYPES)[number]

export const LEGACY_ISSUE_EVENT_TYPES = [
  'stage_changed',
  'comment_added',
  'agent_started',
  'agent_completed',
  'agent_paused',
  'agent_error',
  'agent_blocked',
  'approval_requested',
  'merge_queued',
  'merge_started',
  'merge_completed',
  'merge_failed',
  'rebase_started',
  'rebase_progress',
  'rebase_completed',
  'rebase_conflict',
  'agent_conflict_resolution_started',
  'agent_conflict_resolution_completed',
  'agent_conflict_resolution_failed',
  'check_started',
  'check_update',
  'check_suite_status_changed',
  'integration_started',
  'integration_step_updated',
  'integration_completed',
  'integration_failed',
  'base_drift_detected',
  'rebase_opportunity',
  'user_attention_requested',
  'stage_task_update',
] as const

export const LEGACY_AGENT_DETAIL_EVENT_TYPES = [
  'agent_text_chunk',
  'main_tool_call',
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

export const LEGACY_EVENT_TYPES = [
  ...LEGACY_ISSUE_EVENT_TYPES,
  ...LEGACY_AGENT_DETAIL_EVENT_TYPES,
] as const

export const EVENT_TYPES = [
  ...LEGACY_EVENT_TYPES,
  ...TRANSCRIPT_EVENT_TYPES,
  ...Object.values(REVERSE_DNS_EVENT_TYPES),
] as const

export const AGENT_DETAIL_ROUTED_EVENT_TYPES = [
  ...LEGACY_AGENT_DETAIL_EVENT_TYPES,
  ...TRANSCRIPT_EVENT_TYPES,
  ...REVERSE_DNS_AGENT_SESSION_EVENT_TYPES,
] as const

export type CanonicalEventType = (typeof EVENT_TYPES)[number]
