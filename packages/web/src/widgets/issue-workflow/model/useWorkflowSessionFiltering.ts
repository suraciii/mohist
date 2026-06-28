import { useMemo, useState } from 'react'
import type { WorkflowRunSession } from '../../../entities/coder-session'

export const WORKFLOW_PIPELINE_STAGES = ['plan', 'build', 'check', 'integrate'] as const

export type WorkflowPipelineStage = (typeof WORKFLOW_PIPELINE_STAGES)[number]

export type WorkflowSessionSortKey = 'createdAt' | 'tokens' | 'duration'

export const WORKFLOW_SESSION_SORT_KEYS: readonly WorkflowSessionSortKey[] = [
  'createdAt',
  'tokens',
  'duration',
] as const

const TERMINAL_STATUSES = new Set(['completed', 'failed', 'cancelled'])

export function isTerminalSessionStatus(status: string): boolean {
  return TERMINAL_STATUSES.has(status)
}

export function getSessionPipelineStage(session: Pick<WorkflowRunSession, 'sessionName'>): WorkflowPipelineStage | null {
  const name = session.sessionName?.trim().toLowerCase() ?? ''
  return (WORKFLOW_PIPELINE_STAGES as readonly string[]).includes(name)
    ? (name as WorkflowPipelineStage)
    : null
}

export function getSessionTotalTokens(session: Pick<WorkflowRunSession, 'usage'>): number {
  const usage = session.usage
  if (!usage) return 0
  if (typeof usage.totalTokens === 'number') return usage.totalTokens
  const input = typeof usage.inputTokens === 'number' ? usage.inputTokens : 0
  const output = typeof usage.outputTokens === 'number' ? usage.outputTokens : 0
  return input + output
}

export interface SessionDurationInput {
  status: string
  createdAt: string
  startedAt: string | null
  completedAt: string | null
}

export function computeSessionDurationMs(session: SessionDurationInput, nowMs: number): number {
  const terminal = isTerminalSessionStatus(session.status)
  const startIso = session.startedAt ?? session.createdAt
  const startMs = startIso ? new Date(startIso).getTime() : Number.NaN
  if (terminal) {
    const endIso = session.completedAt ?? session.createdAt
    const endMs = endIso ? new Date(endIso).getTime() : Number.NaN
    if (Number.isNaN(startMs) || Number.isNaN(endMs)) return 0
    return Math.max(0, endMs - startMs)
  }
  if (Number.isNaN(startMs)) return 0
  return Math.max(0, nowMs - startMs)
}

export interface UseWorkflowSessionFilteringOptions {
  nowMs?: number
}

export interface UseWorkflowSessionFilteringResult {
  sessions: WorkflowRunSession[]
  statusFilter: string | null
  stageFilter: WorkflowPipelineStage | null
  sortKey: WorkflowSessionSortKey
  setStatusFilter: (value: string | null) => void
  setStageFilter: (value: WorkflowPipelineStage | null) => void
  setSortKey: (value: WorkflowSessionSortKey) => void
  resetFilters: () => void
  availableStatuses: string[]
  availableStages: WorkflowPipelineStage[]
  totalCount: number
}

export function useWorkflowSessionFiltering(
  sessions: WorkflowRunSession[],
  options: UseWorkflowSessionFilteringOptions = {},
): UseWorkflowSessionFilteringResult {
  const [statusFilter, setStatusFilter] = useState<string | null>(null)
  const [stageFilter, setStageFilter] = useState<WorkflowPipelineStage | null>(null)
  const [sortKey, setSortKey] = useState<WorkflowSessionSortKey>('createdAt')

  const availableStatuses = useMemo(() => {
    const seen = new Set<string>()
    for (const session of sessions) {
      if (session.status) seen.add(session.status)
    }
    const required = ['running', 'completed', 'failed']
    for (const value of required) {
      seen.add(value)
    }
    return Array.from(seen).sort((a, b) => a.localeCompare(b))
  }, [sessions])

  const availableStages = useMemo(() => {
    const seen = new Set<WorkflowPipelineStage>()
    for (const session of sessions) {
      const stage = getSessionPipelineStage(session)
      if (stage) seen.add(stage)
    }
    return WORKFLOW_PIPELINE_STAGES.filter((stage) => seen.has(stage))
  }, [sessions])

  const filtered = useMemo(() => {
    return sessions.filter((session) => {
      if (statusFilter && session.status !== statusFilter) return false
      if (stageFilter) {
        const stage = getSessionPipelineStage(session)
        if (stage !== stageFilter) return false
      }
      return true
    })
  }, [sessions, statusFilter, stageFilter])

  const nowMs = options.nowMs ?? Date.now()

  const sorted = useMemo(() => {
    const list = [...filtered]
    list.sort((a, b) => compareSessions(a, b, sortKey, nowMs))
    return list
  }, [filtered, sortKey, nowMs])

  function resetFilters() {
    setStatusFilter(null)
    setStageFilter(null)
  }

  return {
    sessions: sorted,
    statusFilter,
    stageFilter,
    sortKey,
    setStatusFilter,
    setStageFilter,
    setSortKey,
    resetFilters,
    availableStatuses,
    availableStages,
    totalCount: sessions.length,
  }
}

function compareSessions(
  a: WorkflowRunSession,
  b: WorkflowRunSession,
  sortKey: WorkflowSessionSortKey,
  nowMs: number,
): number {
  if (sortKey === 'tokens') {
    return getSessionTotalTokens(b) - getSessionTotalTokens(a)
  }
  if (sortKey === 'duration') {
    return computeSessionDurationMs(b, nowMs) - computeSessionDurationMs(a, nowMs)
  }
  return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()
}