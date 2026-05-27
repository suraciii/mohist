import type { Issue } from '../../../shared/api/types'
import { IssueStage } from '../../../shared/api/types'

export interface Column {
  key: IssueStage
  label: string
  issues: Issue[]
}

export const STAGES: { key: IssueStage; label: string }[] = [
  { key: IssueStage.Backlog, label: 'Backlog' },
  { key: IssueStage.Todo, label: 'Ready' },
  { key: IssueStage.InProgress, label: 'In Progress' },
  { key: IssueStage.Done, label: 'Done' },
  { key: IssueStage.Cancelled, label: 'Cancelled' },
]

export function groupIssuesByStage(issues: Issue[]): Column[] {
  const map = new Map<IssueStage, Issue[]>()
  for (const s of STAGES) map.set(s.key, [])
  for (const issue of issues) {
    const list = map.get(issue.stage)
    if (list) list.push(issue)
  }
  return STAGES.map((s) => ({
    ...s,
    issues: map.get(s.key) ?? [],
  }))
}

export function filterClosedFromDone(
  columns: Column[],
  showClosed: boolean,
): Column[] {
  if (showClosed) return columns
  return columns.map((col) =>
    col.key === IssueStage.Cancelled
      ? {
          ...col,
          issues: [],
        }
      : col,
  )
}

export function getDoneColumnCounts(columns: Column[]): {
  closedCount: number
  doneTotalCount: number
} {
  const cancelledColumn = columns.find((c) => c.key === IssueStage.Cancelled)
  const cancelledIssues = cancelledColumn?.issues ?? []
  const doneColumn = columns.find((c) => c.key === IssueStage.Done)
  const doneIssues = doneColumn?.issues ?? []
  return {
    closedCount: cancelledIssues.length,
    doneTotalCount: doneIssues.length + cancelledIssues.length,
  }
}
