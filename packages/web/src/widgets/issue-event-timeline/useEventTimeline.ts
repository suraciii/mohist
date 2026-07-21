import { useEffect, useMemo, useRef, useState } from 'react'
import {
  onTimelineEvent,
  useIssueEvents,
  useWorkflowTimeline,
  type TimelineLiveEvent,
  type StoredCloudEventDto,
  type WorkflowTimeline,
} from '../../entities/issue'
import { classifyEvent } from './model/classify'
import { describeEvent, type TaskTitleResolver } from './model/describe'
import { classifySource } from './model/source-tag'
import type { TimelineEntry } from './model/types'

const MAX_LIVE_EVENTS = 500

export interface EventTimelineHistoryResult {
  data: StoredCloudEventDto[] | undefined
  isLoading: boolean
}

export type EventTimelineHistoryHook = (
  issueNumber: number,
  enabled: boolean,
) => EventTimelineHistoryResult

export interface EventTimelineWorkflowResult {
  data: WorkflowTimeline | null | undefined
}

export type EventTimelineWorkflowHook = (
  issueNumber: number,
  enabled: boolean,
) => EventTimelineWorkflowResult

const useDefaultHistory: EventTimelineHistoryHook = (issueNumber, enabled) =>
  useIssueEvents(issueNumber, enabled)

const useDefaultWorkflow: EventTimelineWorkflowHook = (issueNumber, enabled) =>
  useWorkflowTimeline(issueNumber, enabled)

function eventData(payload: Record<string, unknown>): Record<string, unknown> {
  return (payload.data && typeof payload.data === 'object'
    ? payload.data
    : payload) as Record<string, unknown>
}

function historyToEntry(event: StoredCloudEventDto, resolveTaskTitle: TaskTitleResolver): TimelineEntry {
  const payload = eventData(event.data as Record<string, unknown>)
  const { category, attention } = classifyEvent(event.type, payload)
  return {
    id: event.eventId || `${event.type}-${event.time}`,
    type: event.type,
    time: event.time,
    source: classifySource(event.type),
    category,
    attention,
    description: describeEvent(event.type, payload, resolveTaskTitle),
    detail: extractDetail(event.type, payload),
    payload,
    isLive: false,
  }
}

function liveToEntry(event: TimelineLiveEvent, resolveTaskTitle: TaskTitleResolver): TimelineEntry {
  const payload = event.payload
  const { category, attention } = classifyEvent(event.type, payload)
  return {
    id: event.eventId || `${event.type}-${event.time ?? Date.now()}`,
    type: event.type,
    time: event.time ?? new Date().toISOString(),
    source: classifySource(event.type),
    category,
    attention,
    description: describeEvent(event.type, payload, resolveTaskTitle),
    detail: extractDetail(event.type, payload),
    payload,
    isLive: true,
  }
}

function extractDetail(type: string, payload: Record<string, unknown>): string | null {
  const lower = type.toLowerCase()

  if (lower === 'rebase_conflict' || lower.includes('conflict')) {
    const conflicts = Array.isArray(payload.conflicts)
      ? payload.conflicts.filter((x): x is string => typeof x === 'string')
      : []
    if (conflicts.length > 0) return conflicts.join('\n')
  }

  if (lower.includes('failed') || lower.includes('error')) {
    const reason = typeof payload.reason === 'string' ? payload.reason : ''
    const error = typeof payload.error === 'string' ? payload.error : ''
    const message = typeof payload.message === 'string' ? payload.message : ''
    const detail = reason || error || message
    if (detail) return detail
  }

  if (lower === 'base_drift_detected' && payload.decision === 'needs-attention') {
    const conflicts = Array.isArray(payload.conflicts)
      ? payload.conflicts.filter((x): x is string => typeof x === 'string')
      : []
    if (conflicts.length > 0) return conflicts.join('\n')
    return 'Base drift needs attention before continuing'
  }

  return null
}

function belongsToIssue(event: TimelineLiveEvent, issueNumber: number): boolean {
  return event.issueNumber === issueNumber
}

function dedupeKey(entry: TimelineEntry): string {
  return entry.id
}

export function useEventTimeline(
  issueNumber: number,
  enabled: boolean = true,
  historyHook: EventTimelineHistoryHook = useDefaultHistory,
  workflowHook: EventTimelineWorkflowHook = useDefaultWorkflow,
): {
  entries: TimelineEntry[]
  isLoading: boolean
} {
  const { data: history, isLoading } = historyHook(issueNumber, enabled)
  const { data: workflowTimeline } = workflowHook(issueNumber, enabled)
  const resolveTaskTitle = useMemo<TaskTitleResolver>(() => {
    const titles = new Map<string, string>()
    for (const stage of workflowTimeline?.stages ?? []) {
      for (const task of stage.tasks) {
        titles.set(`${stage.stage}\u0000${task.id}`, task.title)
      }
    }
    return (stage, taskId) => titles.get(`${stage}\u0000${taskId}`) ?? null
  }, [workflowTimeline])
  const [liveTick, setLiveTick] = useState(0)
  const liveRef = useRef<TimelineEntry[]>([])

  useEffect(() => {
    liveRef.current = []
    setLiveTick((n) => n + 1)
  }, [issueNumber])

  useEffect(() => {
    if (!enabled) return
    return onTimelineEvent((event) => {
      if (!belongsToIssue(event, issueNumber)) return
      const entry = liveToEntry(event, resolveTaskTitle)
      liveRef.current = [entry, ...liveRef.current].slice(0, MAX_LIVE_EVENTS)
      setLiveTick((n) => n + 1)
    })
  }, [issueNumber, enabled, resolveTaskTitle])

  const entries = useMemo(() => {
    const historyEntries = (history ?? []).map((event) => historyToEntry(event, resolveTaskTitle))
    const seen = new Set<string>()
    const merged: TimelineEntry[] = []

    for (const liveEntry of liveRef.current) {
      const entry = {
        ...liveEntry,
        description: describeEvent(liveEntry.type, liveEntry.payload, resolveTaskTitle),
      }
      const key = dedupeKey(entry)
      if (!seen.has(key)) {
        seen.add(key)
        merged.push(entry)
      }
    }

    for (const entry of historyEntries) {
      const key = dedupeKey(entry)
      if (!seen.has(key)) {
        seen.add(key)
        merged.push(entry)
      }
    }

    return merged.sort((a, b) => new Date(a.time).getTime() - new Date(b.time).getTime())
  }, [history, liveTick, resolveTaskTitle])

  return { entries, isLoading }
}
