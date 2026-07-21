import { useCallback, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  dispatchTimelineEvent,
  LiveTaskContext,
  type LiveTaskState,
  type RebaseConflictState,
} from '../../entities/issue'
import { dispatchAgentEvent } from '../../entities/agent'
import type { AgentDetailEventMap } from '../../entities/agent'
import { useProject } from '../../entities/project'
import { useEventsConnection } from '../../shared/api/events-hub'
import { parseInboxItemPersistedHint } from '../../entities/inbox'
import {
  isAgentDetailEvent,
  routeTranscriptEventName,
  unwrapEnvelope,
  unwrapTranscriptEnvelope,
} from './model/event-envelope'
import { buildTimelineLiveEvent } from './model/timeline-live-event'
import { routeEvent } from './handle-event'
import { getCurrentIssueNumber, useViewedIssueRef } from './use-viewed-issue'

export const __testing__ = { unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName, buildTimelineLiveEvent, parseInboxItemPersistedHint, getCurrentIssueNumber }

export type EventsConnectionHook = typeof useEventsConnection
export type ViewedIssueHook = typeof useViewedIssueRef
export type PathnameReader = () => string

const readPathname: PathnameReader = () => window.location.pathname

function useLiveEvents(
  projectId: string | null,
  eventsConnectionHook: EventsConnectionHook,
  viewedIssueHook: ViewedIssueHook,
  pathnameReader: PathnameReader,
): LiveTaskState {
  const queryClient = useQueryClient()
  const [activeTaskId] = useState<string | null>(null)
  const [activeTaskElapsedMs] = useState<number | null>(null)
  const [rebaseConflict, setRebaseConflict] = useState<RebaseConflictState | null>(null)
  const viewedIssueRef = viewedIssueHook()

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
          pathname: pathnameReader(),
        })

        const timelineEvent = buildTimelineLiveEvent(eventName, rawData, parsed)
        dispatchTimelineEvent(timelineEvent)
      } catch {
        // ignore malformed events
      }
    },
    [queryClient, projectId, viewedIssueRef, pathnameReader],
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

  const { reconnectVersion } = eventsConnectionHook(projectId, handleEvent, handleTranscriptEvent)

  return {
    activeTaskId,
    activeTaskElapsedMs,
    rebaseConflict,
    eventsReconnectVersion: reconnectVersion,
  }
}

interface LiveTaskProviderProps {
  children: React.ReactNode
  eventsConnectionHook?: EventsConnectionHook
  viewedIssueHook?: ViewedIssueHook
  pathnameReader?: PathnameReader
}

export function LiveTaskProvider({
  children,
  eventsConnectionHook = useEventsConnection,
  viewedIssueHook = useViewedIssueRef,
  pathnameReader = readPathname,
}: LiveTaskProviderProps) {
  const { projectId } = useProject()
  const state = useLiveEvents(
    projectId,
    eventsConnectionHook,
    viewedIssueHook,
    pathnameReader,
  )
  return (
    <LiveTaskContext.Provider value={state}>
      {children}
    </LiveTaskContext.Provider>
  )
}
