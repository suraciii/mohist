import type { InboxItemPersistedHintPayload } from '../../inbox/@x/events'
import type { AgentDetailEventMap } from '../../agent/@x/events'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'

export type EventMap = {
  [REVERSE_DNS_EVENT_TYPES.StageStarted]: { issueNumber: number; projectId: string; from: string; to: string }
  [REVERSE_DNS_EVENT_TYPES.StageCompleted]: { issueNumber: number; projectId: string; from: string; to: string }
  [REVERSE_DNS_EVENT_TYPES.StageFailed]: { issueNumber: number; projectId: string; from: string; to: string }
  [REVERSE_DNS_EVENT_TYPES.StageApprovalRequested]: { issueNumber: number; projectId: string; stage: string }
  [REVERSE_DNS_EVENT_TYPES.StageApprovalResolved]: { issueNumber: number; projectId: string; stage: string }
  [REVERSE_DNS_EVENT_TYPES.TaskStarted]: { issueNumber: number; projectId: string; stage: string; taskId: string; workerId: string }
  [REVERSE_DNS_EVENT_TYPES.TaskCompleted]: { issueNumber: number; projectId: string; stage: string; taskId: string }
  [REVERSE_DNS_EVENT_TYPES.TaskFailed]: { issueNumber: number; projectId: string; stage: string; taskId: string; message?: string | null }
  [REVERSE_DNS_EVENT_TYPES.TaskCancelled]: { issueNumber: number; projectId: string; stage: string; taskId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentTaskResultUnconfirmed]: { issueNumber: number; projectId: string; stage: string; taskId: string; workId: string; reason: string; deadlineAt: string }
  [REVERSE_DNS_EVENT_TYPES.TaskBlocked]: { issueNumber: number; projectId: string; stage: string; taskId: string; reason: string; deadlineAt: string }
  [REVERSE_DNS_EVENT_TYPES.StageBlocked]: { issueNumber: number; projectId: string; stage: string; taskId: string; reason: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunBlocked]: { issueNumber: number; projectId: string; stage: string; taskId: string; reason: string; deadlineAt: string }
  [REVERSE_DNS_EVENT_TYPES.ArtifactRecorded]: { issueNumber: number; projectId: string; workflowRunId: string; taskRunId: string; path: string; recordedAt: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueCreated]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueEpicChanged]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueCancelled]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueArchived]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueUnarchived]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueReopened]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueWorkStarted]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueCompleted]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged]: { issueNumber: number; projectId: string; oldLabels?: Record<string, string>; labels?: Record<string, string> }
  [REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged]: { issueNumber: number; projectId: string; priority: string }
  [REVERSE_DNS_EVENT_TYPES.IssueDraftChanged]: { issueNumber: number; projectId: string; oldIsDraft: boolean; newIsDraft: boolean }
  [REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded]: { issueNumber: number; projectId: string; prerequisiteNumber: number }
  [REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved]: { issueNumber: number; projectId: string; prerequisiteNumber: number }
  [REVERSE_DNS_EVENT_TYPES.IssueWorkflowProfileChanged]: { issueNumber: number; projectId: string; workflowProfileId?: string | null }
  [REVERSE_DNS_EVENT_TYPES.IssueParentChanged]: { issueNumber: number; projectId: string; previousParentIssueNumber?: number | null; parentIssueNumber?: number | null }
  [REVERSE_DNS_EVENT_TYPES.IssueRepositoryChanged]: { issueNumber: number; projectId: string; oldRepositoryRef?: string | null; newRepositoryRef: string }
  [REVERSE_DNS_EVENT_TYPES.IssueCompositeStarted]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.IssueCompositeStatusChanged]: { issueNumber: number; projectId: string; previousStatus: string; newStatus: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionUsageRecorded]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionModelChanged]: { issueNumber: number; projectId: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionContextCompacted]: { issueNumber: number; projectId: string; strategy?: string | null; contextWindowUsedBefore?: number | null; contextWindowUsedAfter?: number | null; contextWindowSize?: number | null; summary?: string | null; recordedAt?: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionContextExhausted]: { issueNumber: number; projectId: string; failureCategory?: string | null; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
  [REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated]: { issueNumber: number; projectId: string; healthStatus: string; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
  [REVERSE_DNS_EVENT_TYPES.InboxItemPersisted]: InboxItemPersistedHintPayload
} & AgentDetailEventMap

export type EventName = keyof EventMap
