import { useMemo } from 'react'
import { useIssues } from '../api/queries'
import type { Issue } from '../model/types'

const SEVEN_DAYS_MS = 7 * 24 * 60 * 60 * 1000

export interface CompletionSnapshot {
  completed: number
  failed: number
  new: number
}

const EMPTY_SNAPSHOT: CompletionSnapshot = { completed: 0, failed: 0, new: 0 }

function toEpoch(value: Date | number): number {
  return value instanceof Date ? value.getTime() : value
}

function parseTimestamp(value: string): number {
  const t = Date.parse(value)
  return Number.isFinite(t) ? t : Number.NaN
}

function isInWindow(epochMs: number, windowStart: number, windowEnd: number): boolean {
  if (!Number.isFinite(epochMs)) return false
  return epochMs >= windowStart && epochMs <= windowEnd
}

export function deriveCompletionSnapshot(
  issues: readonly Issue[],
  now: Date | number = Date.now(),
): CompletionSnapshot {
  const nowMs = toEpoch(now)
  const windowStart = nowMs - SEVEN_DAYS_MS

  let completed = 0
  let failed = 0
  let newly = 0

  for (const issue of issues) {
    const createdAtMs = parseTimestamp(issue.createdAt)
    const updatedAtMs = parseTimestamp(issue.updatedAt)

    if (isInWindow(createdAtMs, windowStart, nowMs)) {
      newly += 1
    }

    if (issue.status === 'done' && isInWindow(updatedAtMs, windowStart, nowMs)) {
      completed += 1
    } else if (issue.status === 'cancelled' && isInWindow(updatedAtMs, windowStart, nowMs)) {
      failed += 1
    }
  }

  return { completed, failed, new: newly }
}

export function useCompletionSnapshot(): CompletionSnapshot {
  const { data } = useIssues()

  return useMemo(() => {
    if (!data) return EMPTY_SNAPSHOT
    return deriveCompletionSnapshot(data)
  }, [data])
}