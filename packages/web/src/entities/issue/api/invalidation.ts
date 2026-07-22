import type { QueryClient } from '@tanstack/react-query'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'
import { issueArtifactKeys, issueCandidateKeys, issueDetailKeys, issueListKeys, issueWorkflowKeys } from './query-keys'

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

const STRUCTURAL_ISSUE_EVENTS = new Set<string>([
  REVERSE_DNS_EVENT_TYPES.IssueCreated,
  REVERSE_DNS_EVENT_TYPES.IssueEpicChanged,
  REVERSE_DNS_EVENT_TYPES.IssueCancelled,
  REVERSE_DNS_EVENT_TYPES.IssueArchived,
  REVERSE_DNS_EVENT_TYPES.IssueUnarchived,
  REVERSE_DNS_EVENT_TYPES.IssueReopened,
  REVERSE_DNS_EVENT_TYPES.IssueWorkStarted,
  REVERSE_DNS_EVENT_TYPES.IssueCompleted,
  REVERSE_DNS_EVENT_TYPES.IssueLabelsChanged,
  REVERSE_DNS_EVENT_TYPES.IssuePriorityChanged,
  REVERSE_DNS_EVENT_TYPES.IssueDraftChanged,
  REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteAdded,
  REVERSE_DNS_EVENT_TYPES.IssuePrerequisiteRemoved,
  REVERSE_DNS_EVENT_TYPES.IssueWorkflowProfileChanged,
  REVERSE_DNS_EVENT_TYPES.IssueParentChanged,
  REVERSE_DNS_EVENT_TYPES.IssueRepositoryChanged,
  REVERSE_DNS_EVENT_TYPES.IssueCompositeStarted,
  REVERSE_DNS_EVENT_TYPES.IssueCompositeStatusChanged,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning,
  REVERSE_DNS_EVENT_TYPES.StageStarted,
  REVERSE_DNS_EVENT_TYPES.StageCompleted,
  REVERSE_DNS_EVENT_TYPES.StageFailed,
  REVERSE_DNS_EVENT_TYPES.StageApprovalRequested,
  REVERSE_DNS_EVENT_TYPES.StageApprovalResolved,
  REVERSE_DNS_EVENT_TYPES.TaskStarted,
  REVERSE_DNS_EVENT_TYPES.TaskCompleted,
  REVERSE_DNS_EVENT_TYPES.TaskFailed,
])

const DETAIL_EVENTS = new Set<string>([
  ...STRUCTURAL_ISSUE_EVENTS,
  ...Object.values(REVERSE_DNS_EVENT_TYPES).filter((eventName) => eventName.startsWith('com.mohist.agent-session.')),
])

const CANDIDATE_EVENTS = new Set<string>([
  REVERSE_DNS_EVENT_TYPES.IssueCreated,
  REVERSE_DNS_EVENT_TYPES.IssueArchived,
  REVERSE_DNS_EVENT_TYPES.IssueUnarchived,
  REVERSE_DNS_EVENT_TYPES.IssueReopened,
  REVERSE_DNS_EVENT_TYPES.IssueWorkStarted,
  REVERSE_DNS_EVENT_TYPES.IssueCancelled,
  REVERSE_DNS_EVENT_TYPES.IssueCompleted,
  REVERSE_DNS_EVENT_TYPES.IssueParentChanged,
  REVERSE_DNS_EVENT_TYPES.IssueCompositeStarted,
  REVERSE_DNS_EVENT_TYPES.IssueCompositeStatusChanged,
])

const WORKFLOW_EVENTS = new Set<string>([
  REVERSE_DNS_EVENT_TYPES.WorkflowRunStarted,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunResumed,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunStopped,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunCompleted,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunRetrying,
  REVERSE_DNS_EVENT_TYPES.WorkflowRunRerunning,
  REVERSE_DNS_EVENT_TYPES.StageStarted,
  REVERSE_DNS_EVENT_TYPES.StageCompleted,
  REVERSE_DNS_EVENT_TYPES.StageFailed,
  REVERSE_DNS_EVENT_TYPES.StageApprovalRequested,
  REVERSE_DNS_EVENT_TYPES.StageApprovalResolved,
  REVERSE_DNS_EVENT_TYPES.TaskStarted,
  REVERSE_DNS_EVENT_TYPES.TaskCompleted,
  REVERSE_DNS_EVENT_TYPES.TaskFailed,
  REVERSE_DNS_EVENT_TYPES.ArtifactRecorded,
  REVERSE_DNS_EVENT_TYPES.IssueWorkflowProfileChanged,
  ...Object.values(REVERSE_DNS_EVENT_TYPES).filter((eventName) => eventName.startsWith('com.mohist.agent-session.')),
])

export function invalidateIssueEvent(
  queryClient: InvalidationClient,
  eventName: string,
  parsed: Record<string, unknown>,
  currentProjectId: string | null,
): void {
  if (!currentProjectId) return
  if (typeof parsed.projectId !== 'string' || parsed.projectId !== currentProjectId) return

  const issueNumber = parsed.issueNumber
  if (typeof issueNumber !== 'number' || !Number.isSafeInteger(issueNumber) || issueNumber <= 0) return

  if (DETAIL_EVENTS.has(eventName)) {
    queryClient.invalidateQueries({
      queryKey: issueDetailKeys.detail(currentProjectId, issueNumber),
      exact: true,
    })
  }

  if (eventName === REVERSE_DNS_EVENT_TYPES.IssueParentChanged) {
    const relatedParents = new Set([
      parsed.previousParentIssueNumber,
      parsed.parentIssueNumber,
    ])
    for (const relatedIssueNumber of relatedParents) {
      if (typeof relatedIssueNumber !== 'number'
        || !Number.isSafeInteger(relatedIssueNumber)
        || relatedIssueNumber <= 0
        || relatedIssueNumber === issueNumber) continue
      queryClient.invalidateQueries({
        queryKey: issueDetailKeys.detail(currentProjectId, relatedIssueNumber),
        exact: true,
      })
    }
  }

  if (STRUCTURAL_ISSUE_EVENTS.has(eventName)) {
    queryClient.invalidateQueries({ queryKey: issueListKeys.project(currentProjectId) })
  }

  if (CANDIDATE_EVENTS.has(eventName)) {
    queryClient.invalidateQueries({ queryKey: issueCandidateKeys.project(currentProjectId), exact: true })
  }

  if (WORKFLOW_EVENTS.has(eventName)) {
    queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.root(currentProjectId, issueNumber) })
  }

  if (eventName === REVERSE_DNS_EVENT_TYPES.ArtifactRecorded) {
    queryClient.invalidateQueries({ queryKey: issueArtifactKeys.root(currentProjectId, issueNumber) })
  }

  if (eventName === REVERSE_DNS_EVENT_TYPES.IssueEpicChanged) {
    queryClient.invalidateQueries({ queryKey: ['epics', currentProjectId] })
  }
}
