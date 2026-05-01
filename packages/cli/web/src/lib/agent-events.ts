import type { AgentDetailEventMap } from './types'

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
  'agent_text_chunk',
  'main_tool_call',
  'coder_text_chunk',
  'coder_tool_call',
  'ralph_task_update',
  'ralph_loop_progress',
  'plan_round_start',
  'plan_session_update',
  'plan_round_complete',
  'coder_recovery_status',
  'coder_session_started',
  'coder_session_completed',
  'agent_paused',
  'question_asked',
  'question_answered',
  'check_update',
  'check_suite_status_changed',
]
