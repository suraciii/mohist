import { IssueStatus } from '../../../entities/issue'

export type StageColorKey = 'gray' | 'blue' | 'amber' | 'green' | 'red'

export interface StageColorScheme {
  /** solid color used for the column title dot / accent bar */
  accent: string
  /** tailwind class set for the column title text */
  labelClass: string
  /** background tint when this column is "active" (selected on mobile, or default on desktop) */
  activeBg: string
  /** border color when this column is "active" */
  activeBorder: string
}

export const STAGE_COLORS: Record<IssueStatus, StageColorScheme> = {
  [IssueStatus.Backlog]: {
    accent: '#9ca3af',
    labelClass: 'text-muted-foreground',
    activeBg: 'bg-gray-50',
    activeBorder: 'border-gray-200',
  },
  [IssueStatus.Todo]: {
    accent: '#3b82f6',
    labelClass: 'text-blue-700',
    activeBg: 'bg-blue-50/60',
    activeBorder: 'border-blue-200',
  },
  [IssueStatus.InProgress]: {
    accent: '#f59e0b',
    labelClass: 'text-amber-700',
    activeBg: 'bg-amber-50/60',
    activeBorder: 'border-amber-200',
  },
  [IssueStatus.Done]: {
    accent: '#22c55e',
    labelClass: 'text-green-700',
    activeBg: 'bg-green-50/40',
    activeBorder: 'border-green-200',
  },
  [IssueStatus.Cancelled]: {
    accent: '#ef4444',
    labelClass: 'text-red-700',
    activeBg: 'bg-red-50/40',
    activeBorder: 'border-red-200',
  },
}

export function getStageColors(status: IssueStatus): StageColorScheme {
  return STAGE_COLORS[status] ?? STAGE_COLORS[IssueStatus.Backlog]
}
