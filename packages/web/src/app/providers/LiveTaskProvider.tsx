import { useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { LiveTaskState, RebaseConflictState } from '../../entities/issue'
import { dispatchAgentEvent } from '../../entities/agent'
import type { AgentDetailEventMap } from '../../entities/agent'
import { LiveTaskContext } from '../../entities/issue'
import { useProject } from '../../entities/project'
import { useEventsConnection } from '../../shared/api/events-hub'
import { dispatchTimelineEvent } from '../../entities/issue/model/timeline-events'
import { parseInboxItemPersistedHint } from '../../entities/inbox/model/inbox-effects'
import {
  isAgentDetailEvent,
  routeTranscriptEventName,
  unwrapEnvelope,
  unwrapTranscriptEnvelope,
} from './model/event-envelope'
import { buildTimelineLiveEvent } from './model/timeline-live-event'
import { routeEvent } from './handle-event'
import { useRunnerDropNotice } from './use-runner-drop-notice'
import { getCurrentIssueNumber, useViewedIssueRef } from './use-viewed-issue'

export const __testing__ = { unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName, buildTimelineLiveEvent, parseInboxItemPersistedHint, getCurrentIssueNumber }

function useLiveEvents(projectId: string | null): LiveTaskState {
  const queryClient = useQueryClient()
  const [activeTaskId] = useState<string | null>(null)
  const [activeTaskElapsedMs] = useState<number | null>(null)
  const [rebaseConflict, setRebaseConflict] = useState<RebaseConflictState | null>(null)
  const viewedIssueRef = useViewedIssueRef()
  // Subscribe to SignalR connection transitions so transport notices are
  // routed to the toast host / Activity surface instead of any inline issue
  // content. The hook itself owns publishing.
  useRunnerDropNotice()

  const handleEvent = useCallback(
    (eventName: string, rawData: unknown, options?: { dispatchAgentDetail?: boolean }) => {
      try {
        const parsed = unwrapEnvelope(rawData)

        if (options?.dispatchAgentDetail !== false && isAgentDetailEvent(eventName)) {
          dispatchAgentEvent(eventName, parsed as AgentDetailEventMap[typeof eventName])
        }

        routeEvent(eventName, parsed, {
          queryClient,
          setRebaseConflict,
          viewedIssue: viewedIssueRef.current,
          projectId,
        })

        const timelineEvent = buildTimelineLiveEvent(eventName, rawData, parsed)
        dispatchTimelineEvent(timelineEvent)
      } catch {
        // ignore malformed events
      }
    },
    [queryClient, projectId],
  )

  const handleTranscriptEvent = useCallback(
    (rawData: unknown) => {
      const transcript = unwrapTranscriptEnvelope(rawData)
      if (!transcript) return
      const routedName = routeTranscriptEventName(transcript.eventName)
      if (isAgentDetailEvent(routedName)) {
        dispatchAgentEvent(
          routedName,
          transcript.detail as AgentDetailEventMap[typeof routedName],
        )
      }
      handleEvent(routedName, transcript.payload, { dispatchAgentDetail: false })
    },
    [handleEvent],
  )

  useEventsConnection(projectId, handleEvent, handleTranscriptEvent)

  return { activeTaskId, activeTaskElapsedMs, rebaseConflict }
}

export function LiveTaskProvider({ children }: { children: React.ReactNode }) {
  const { projectId } = useProject()
  const state = useLiveEvents(projectId)
  return (
    <LiveTaskContext.Provider value={state}>
      {children}
    </LiveTaskContext.Provider>
  )
}
