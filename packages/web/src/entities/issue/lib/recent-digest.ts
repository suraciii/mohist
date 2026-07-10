import { useMemo } from 'react'
import { useArchivedIssues, useIssues } from '../api/queries'
import { useProject } from '../../project/@x/project-context'
import type { Issue } from '../model/types'

export const DIGEST_TOP_N = 5

export interface RecentDigest {
  completed: Issue[]
  failed: Issue[]
  archived: Issue[]
}

function parseTimestamp(value: string): number {
  const t = Date.parse(value)
  return Number.isFinite(t) ? t : Number.NaN
}

function compareDesc(a: number, b: number): number {
  if (!Number.isFinite(a) && !Number.isFinite(b)) return 0
  if (!Number.isFinite(a)) return 1
  if (!Number.isFinite(b)) return -1
  return b - a
}

export function deriveRecentDigest(
  issues: readonly Issue[],
  archivedIssues: readonly Issue[] = [],
  options: { topN?: number } = {},
): RecentDigest {
  const topN = options.topN ?? DIGEST_TOP_N

  const completed: Issue[] = []
  const failed: Issue[] = []

  for (const issue of issues) {
    if (issue.archivedAt) continue
    if (issue.status === 'done') {
      completed.push(issue)
    } else if (issue.status === 'cancelled') {
      failed.push(issue)
    }
  }

  completed.sort((a, b) => compareDesc(parseTimestamp(a.completedAt ?? ''), parseTimestamp(b.completedAt ?? '')))
  failed.sort((a, b) => compareDesc(parseTimestamp(a.updatedAt), parseTimestamp(b.updatedAt)))

  const archived = archivedIssues
    .filter((issue) => issue.archivedAt != null)
    .sort((a, b) =>
      compareDesc(parseTimestamp(a.archivedAt ?? ''), parseTimestamp(b.archivedAt ?? '')),
    )

  return {
    completed: completed.slice(0, topN),
    failed: failed.slice(0, topN),
    archived: archived.slice(0, topN),
  }
}

export interface UseRecentDigestResult extends RecentDigest {
  isLoading: boolean
}

export interface RecentDigestHooks {
  useIssues: typeof useIssues
  useArchivedIssues: typeof useArchivedIssues
}

const defaultHooks: RecentDigestHooks = {
  useIssues,
  useArchivedIssues,
}

export function useRecentDigest(hooks: RecentDigestHooks = defaultHooks): UseRecentDigestResult {
  const { projectId } = useProject()
  const enabled = !!projectId

  const issuesQuery = hooks.useIssues(projectId ? { projectId } : undefined)
  const archivedQuery = hooks.useArchivedIssues(projectId ? { projectId } : undefined)

  const data = useMemo(
    () => deriveRecentDigest(issuesQuery.data ?? [], archivedQuery.data ?? []),
    [issuesQuery.data, archivedQuery.data],
  )

  return {
    ...data,
    isLoading: enabled && (issuesQuery.isLoading || archivedQuery.isLoading),
  }
}
