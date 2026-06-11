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

/**
 * Render/decide-visibility seam for the Cancelled column.
 *
 * This helper is intentionally an identity function: it always returns the
 * input `columns` array reference unchanged, regardless of `showCancelled`.
 * The Cancelled column must carry its full set of issues through the
 * grouping pipeline so that downstream consumers (mobile tab counts, the
 * in-column renderer's "n hidden" affordance, etc.) can read the real
 * number of cancelled issues.
 *
 * Visibility of the cancelled issues is a render-time concern owned by
 * the Kanban renderer (`StageColumn` / the mobile list body) reading the
 * `showCancelled` state. Do not reintroduce issue mutation here — that
 * would make counts wrong, tests vacuous, and the toggle bidirectional.
 *
 * The helper is kept (rather than inlined away) as a documented seam so
 * that any future move of the visibility decision back to the data layer
 * (for example URL persistence) has a clear, intentional landing spot.
 */
export function filterCancelledFromColumns(
  columns: Column[],
  _showCancelled: boolean,
): Column[] {
  return columns
}

export function getCancelledColumnCount(columns: Column[]): {
  cancelledCount: number
  doneTotalCount: number
} {
  const cancelledColumn = columns.find((c) => c.key === IssueStatus.Cancelled)
  const cancelledIssues = cancelledColumn?.issues ?? []
  const doneColumn = columns.find((c) => c.key === IssueStatus.Done)
  const doneIssues = doneColumn?.issues ?? []
  return {
    cancelledCount: cancelledIssues.length,
    doneTotalCount: doneIssues.length + cancelledIssues.length,
  }
}
