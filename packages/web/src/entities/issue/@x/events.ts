import type { InboxItemPersistedHintPayload } from '../../inbox/model/inbox-effects'
import type { AgentDetailEventMap } from '../../agent/@x/events'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'

export type EventMap = {
  [REVERSE_DNS_EVENT_TYPES.StageStarted]: { issueId: string; projectId: string; from: string; to: string }
  [REVERSE_DNS_EVENT_TYPES.StageCompleted]: { issueId: string; projectId: string; from: string; to: string }
  [REVERSE_DNS_EVENT_TYPES.StageFailed]: { issueId: string; projectId: string; from: string; to: string }
  [REVERSE_DNS_EVENT_TYPES.StageApprovalRequested]: { issueId: string; projectId: string; stage: string }
  [REVERSE_DNS_EVENT_TYPES.StageApprovalResolved]: { issueId: string; projectId: string; stage: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueCreated]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueClosed]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueArchived]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueUnarchived]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueReopened]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueWorkStarted]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueWorkCompleted]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged]: { issueId: string; projectId: string; oldLabels?: Record<string, string>; labels?: Record<string, string> }
  [REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged]: { issueId: string; projectId: string; priority: string }
  [REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded]: { issueId: string; projectId: string; prerequisiteId: string }
  [REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved]: { issueId: string; projectId: string; prerequisiteId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged]: { issueId: string; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionContextCompacted]: { issueId: string; projectId: string; strategy?: string | null; contextWindowUsedBefore?: number | null; contextWindowUsedAfter?: number | null; contextWindowSize?: number | null; summary?: string | null; recordedAt?: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionContextExhausted]: { issueId: string; projectId: string; failureCategory?: string | null; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated]: { issueId: string; projectId: string; healthStatus: string; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
  [REVERSE_DNS_EVENT_TYPES.InboxItemPersisted]: InboxItemPersistedHintPayload
} & AgentDetailEventMap

export type EventName = keyof EventMap
