import type { AgentDetailEventMap } from './types'
import { AGENT_DETAIL_ROUTED_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'

type AgentEventName = keyof AgentDetailEventMap

const target = new EventTarget()

export function dispatchAgentEvent<T extends AgentEventName>(
  name: T,
  detail: AgentDetailEventMap[T],
): void {
  target.dispatchEvent(new CustomEvent(name, { detail }))
}

export function onAgentEvent<T extends AgentEventName>(
  name: T,
  handler: (detail: AgentDetailEventMap[T]) => void,
): () => void {
  const listener = (e: Event) => {
    handler((e as CustomEvent<AgentDetailEventMap[T]>).detail)
  }
  target.addEventListener(name, listener)
  return () => target.removeEventListener(name, listener)
}

export const AGENT_DETAIL_EVENTS: AgentEventName[] = [
  ...AGENT_DETAIL_ROUTED_EVENT_TYPES,
] as AgentEventName[]
