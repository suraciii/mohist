import { IssueStatus, type Issue } from '../../../entities/issue'

export interface Column {
  key: IssueStatus
  label: string
  issues: Issue[]
}

export const STAGES: { key: IssueStatus; label: string }[] = [
  { key: IssueStatus.Backlog, label: 'Backlog' },
  { key: IssueStatus.InProgress, label: 'In Progress' },
  { key: IssueStatus.Done, label: 'Done' },
  { key: IssueStatus.Cancelled, label: 'Cancelled' },
]

export function groupIssuesByStage(issues: Issue[]): Column[] {
  const map = new Map<IssueStatus, Issue[]>()
  for (const s of STAGES) map.set(s.key, [])
  for (const issue of issues) {
    const list = map.get(issue.status)
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
    col.key === IssueStatus.Cancelled
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
  const cancelledColumn = columns.find((c) => c.key === IssueStatus.Cancelled)
  const cancelledIssues = cancelledColumn?.issues ?? []
  const doneColumn = columns.find((c) => c.key === IssueStatus.Done)
  const doneIssues = doneColumn?.issues ?? []
  return {
    closedCount: cancelledIssues.length,
    doneTotalCount: doneIssues.length + cancelledIssues.length,
  }
}
