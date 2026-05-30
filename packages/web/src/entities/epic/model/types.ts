import type { IssueStatus, IssueHealth } from '../../issue/@x/types'

export enum EpicStatus {
  Active = 'active',
  Done = 'done',
  Closed = 'closed',
}

export type EpicPriority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4'

export interface Epic {
  id: string
  title: string
  description: string
  priority: EpicPriority
  status: EpicStatus
  createdAt: string
  updatedAt: string
}

export interface EpicProgress {
  completedCount: number
  totalIssueCount: number
  blockedIssues: string[]
  activeIssues: string[]
  nextIssue: { id: string; number: number; title: string } | null
  readyToMarkDone: boolean
}

export interface EpicWithProgress extends Epic {
  progress: EpicProgress
}

export interface LinkedIssue {
  id: string
  number: number
  title: string
  status: IssueStatus
  health: IssueHealth
  priority: string | null
}

export interface EpicDetail extends Epic {
  linkedIssues: LinkedIssue[]
  progress: EpicProgress
}
