import { useMemo } from 'react'
import { useWorkflowRunSessions, type WorkflowRunSession } from '../../../entities/coder-session'

export interface SiblingSessionNavigation {
  sessions: WorkflowRunSession[]
  currentIndex: number
  previous: WorkflowRunSession | null
  next: WorkflowRunSession | null
  hasPrevious: boolean
  hasNext: boolean
}

export interface UseSiblingSessionsOptions {
  currentKey?: string | null
}

export function useSiblingSessions(
  workflowRunId: string | null | undefined,
  options: UseSiblingSessionsOptions = {},
): SiblingSessionNavigation {
  const { sessions } = useWorkflowRunSessions(workflowRunId)
  const currentKey = options.currentKey ?? null

  return useMemo<SiblingSessionNavigation>(() => {
    const sorted = [...sessions].sort((a, b) => {
      const aMs = new Date(a.createdAt).getTime()
      const bMs = new Date(b.createdAt).getTime()
      if (aMs !== bMs) return aMs - bMs
      return a.sessionName.localeCompare(b.sessionName)
    })

    let currentIndex = -1
    if (currentKey) {
      currentIndex = sorted.findIndex((session) => session.sessionName === currentKey)
      if (currentIndex === -1) {
        currentIndex = sorted.findIndex((session) => session.id === currentKey)
      }
    }

    const previous = currentIndex > 0 ? sorted[currentIndex - 1] : null
    const next = currentIndex >= 0 && currentIndex < sorted.length - 1 ? sorted[currentIndex + 1] : null

    return {
      sessions: sorted,
      currentIndex,
      previous,
      next,
      hasPrevious: previous != null,
      hasNext: next != null,
    }
  }, [sessions, currentKey])
}