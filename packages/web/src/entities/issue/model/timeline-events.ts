export interface TimelineLiveEvent {
  issueNumber: number | null
  type: string
  time: string | null
  eventId: string | null
  payload: Record<string, unknown>
}

const target = new EventTarget()
const TIMELINE_EVENT_NAME = 'timeline-event'

export function dispatchTimelineEvent(event: TimelineLiveEvent): void {
  target.dispatchEvent(new CustomEvent(TIMELINE_EVENT_NAME, { detail: event }))
}

export function onTimelineEvent(handler: (event: TimelineLiveEvent) => void): () => void {
  const listener = (e: Event) => {
    handler((e as CustomEvent<TimelineLiveEvent>).detail)
  }
  target.addEventListener(TIMELINE_EVENT_NAME, listener)
  return () => target.removeEventListener(TIMELINE_EVENT_NAME, listener)
}
