import { useMemo } from 'react'
import { deriveTimelineItems, groupTimelineItems } from '../../../entities/session'
import type { TimelineEntry, TimelineFact, TimelineItem } from '../../../entities/session'
import {
  buildTimelineFacts,
  deriveCurrentActivity,
  type SessionTimelineCurrentActivity,
  type SessionTimelineFactInput,
} from './timeline-facts'

export type UseSessionTimelineOptions = SessionTimelineFactInput

export interface UseSessionTimelineResult {
  facts: TimelineFact[]
  items: TimelineItem[]
  entries: TimelineEntry[]
  currentActivity: SessionTimelineCurrentActivity
}

export function useSessionTimeline(options: UseSessionTimelineOptions): UseSessionTimelineResult {
  const facts = useMemo(() => buildTimelineFacts(options), [options])
  const items = useMemo(() => deriveTimelineItems(facts), [facts])
  const entries = useMemo(() => groupTimelineItems(items), [items])
  const currentActivity = useMemo(
    () => deriveCurrentActivity(facts, items, options),
    [facts, items, options],
  )

  return { facts, items, entries, currentActivity }
}

export type {
  SessionTimelineCurrentActivity,
  SessionTimelineFactInput,
  SessionTimelineSummaryInput,
} from './timeline-facts'
export { buildTimelineFacts, deriveCurrentActivity } from './timeline-facts'
