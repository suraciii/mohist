import { useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useEventsConnection } from '../../../shared/api/events-hub'
import { REVERSE_DNS_EVENT_TYPES, type ReverseDnsEventType } from '../../../shared/lib/canonical-event-types'
import { useProject } from '../../project/@x/project-context'
import { inboxQueryKey } from '../api/queries'

const INBOX_REFRESH_EVENT_TYPES: readonly ReverseDnsEventType[] = [
  REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed,
  REVERSE_DNS_EVENT_TYPES.StageApprovalRequested,
  REVERSE_DNS_EVENT_TYPES.IssueWorkStarted,
  REVERSE_DNS_EVENT_TYPES.IssueWorkCompleted,
]

const INBOX_REFRESH_EVENT_SET: ReadonlySet<string> = new Set(INBOX_REFRESH_EVENT_TYPES)

export function useInboxLiveRefresh(): void {
  const { projectId } = useProject()
  const queryClient = useQueryClient()

  const handleEvent = useCallback(
    (eventName: string) => {
      if (INBOX_REFRESH_EVENT_SET.has(eventName)) {
        queryClient.invalidateQueries({ queryKey: inboxQueryKey(projectId) })
      }
    },
    [projectId, queryClient],
  )

  useEventsConnection(projectId, handleEvent, undefined)
}
