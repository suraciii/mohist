import type { Issue } from './types'
import { Stage, IssueStatus } from './types'

export interface Column {
  key: Stage
  label: string
  issues: Issue[]
}

export const STAGES: { key: Stage; label: string }[] = [
  { key: Stage.Backlog, label: 'Backlog' },
  { key: Stage.Plan, label: 'Plan' },
  { key: Stage.Build, label: 'Build' },
  { key: Stage.Check, label: 'Check' },
  { key: Stage.Integrate, label: 'Integrate' },
  { key: Stage.Done, label: 'Done' },
]

export function groupIssuesByStage(issues: Issue[]): Column[] {
  const map = new Map<Stage, Issue[]>()
  for (const s of STAGES) map.set(s.key, [])
  for (const issue of issues) {
    const targetStage =
      issue.status === IssueStatus.Closed ? Stage.Done : issue.stage
    const list = map.get(targetStage)
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
    col.key === Stage.Done
      ? {
          ...col,
          issues: col.issues.filter(
            (i) => i.status !== IssueStatus.Closed,
          ),
        }
      : col,
  )
}

export function getDoneColumnCounts(columns: Column[]): {
  closedCount: number
  doneTotalCount: number
} {
  const doneColumn = columns.find((c) => c.key === Stage.Done)
  const doneIssues = doneColumn?.issues ?? []
  return {
    closedCount: doneIssues.filter((i) => i.status === IssueStatus.Closed).length,
    doneTotalCount: doneIssues.length,
  }
}
