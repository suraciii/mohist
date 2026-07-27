import type { SessionEvent } from '../types'

export function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

export function narrowPayload(event: SessionEvent): Record<string, unknown> {
  return isRecord(event.payload) ? event.payload : {}
}

export function getStringProp(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key]
  return typeof value === 'string' && value ? value : undefined
}

export function getNumberProp(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export function extractTextChunk(payload: Record<string, unknown>): string {
  const direct = getStringProp(payload, 'text')
  if (direct !== undefined) return direct
  const content = payload.content
  if (isRecord(content)) {
    const nested = getStringProp(content, 'text')
    if (nested !== undefined) return nested
  }
  return ''
}

export function normalizeRaw(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined
  if (typeof value === 'string') return value
  try {
    return JSON.stringify(value)
  } catch {
    return undefined
  }
}

export function normalizeToolName(toolName: string, title?: string): string {
  if (title) {
    const lower = title.toLowerCase()
    if (lower.startsWith('loaded skill:') || lower === 'skill' || lower.startsWith('skill:')) return 'skill'
    if (lower.includes('subagent') || lower.includes('delegate') || lower.startsWith('task:')) return 'task'
    if (lower.includes('apply_patch')) return 'apply_patch'
    if (lower.includes('search_files')) return 'search_files'
    if (lower.includes('webfetch')) return 'webfetch'
    if (lower.includes('websearch')) return 'websearch'
    if (lower.includes('todowrite')) return 'todowrite'
    if (lower === 'todo' || lower.startsWith('todo:') || lower.includes(' todo ')) return 'todo'
    if (lower.includes('bash')) return 'bash'
    if (lower.includes('shell')) return 'shell'
    if (lower.includes('grep')) return 'grep'
    if (lower.includes('glob')) return 'glob'
    if (lower.includes('read')) return 'read'
    if (lower.includes('write')) return 'write'
    if (lower.includes('edit')) return 'edit'
    if (lower.includes('question')) return 'question'
    if (lower.includes('search')) return 'search'
  }
  if (toolName) return toolName.toLowerCase()
  return 'unknown'
}

export function mapToolState(status: string | undefined): 'pending' | 'running' | 'completed' | 'failed' | 'cancelled' {
  if (status === 'completed' || status === 'failed' || status === 'cancelled') return status
  if (status === 'in_progress' || status === 'running' || status === 'pending') return 'running'
  return 'pending'
}

export function mapTerminalStatus(status: string | undefined): 'completed' | 'failed' | 'cancelled' | 'running' {
  if (status === 'completed' || status === 'failed' || status === 'cancelled') return status
  if (status === 'timeout') return 'failed'
  return 'running'
}

export function isInputEvent(type: string): boolean {
  return type === 'session.input' || type === 'input'
}

export function isAssistantTextEvent(type: string): boolean {
  return type === 'message.delta' || type === 'assistant_text'
}

export function isAssistantReasoningEvent(type: string): boolean {
  return type === 'reasoning.delta' || type === 'assistant_reasoning'
}

export function isToolEvent(type: string): boolean {
  return type === 'tool_call' || type === 'tool_call.started' || type === 'tool_call.updated' || type === 'tool_call.completed'
}

export function isSessionActivityEvent(type: string): boolean {
  return type === 'session.activity'
}

export function isLivenessEvent(type: string): boolean {
  return type === 'session.liveness' || type === 'status'
}

export function defaultToolStatus(type: string): string {
  if (type === 'tool_call.completed') return 'completed'
  if (type === 'tool_call.started') return 'started'
  return 'running'
}

export function toolRecord(payload: Record<string, unknown>): Record<string, unknown> {
  const nested = payload.toolCall
  return nested && typeof nested === 'object' && !Array.isArray(nested)
    ? nested as Record<string, unknown>
    : payload
}

export function readToolString(payload: Record<string, unknown>, ...keys: string[]): string | undefined {
  const nested = toolRecord(payload)
  for (const key of keys) {
    const value = payload[key] ?? nested[key]
    if (typeof value === 'string' && value) return value
  }
  return undefined
}

export function readToolValue(payload: Record<string, unknown>, ...keys: string[]): unknown {
  const nested = toolRecord(payload)
  for (const key of keys) {
    const value = payload[key] ?? nested[key]
    if (value !== undefined) return value
  }
  return undefined
}
