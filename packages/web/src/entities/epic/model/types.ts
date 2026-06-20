import type { IssueStatus, IssueHealth, WorkflowStage } from '../../issue/@x/types'

export enum EpicStatus {
  Active = 'active',
  Paused = 'paused',
  Done = 'done',
  Closed = 'closed',
}

export type EpicPriority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4'

export interface Epic {
  id: string
  number: number | null
  title: string
  description: string
  priority: EpicPriority
  status: EpicStatus
  pauseReason?: string | null
  createdAt: string
  updatedAt: string
}

export interface EpicProgressIssue {
  id: string
  number: number
  title: string
  health: IssueHealth
}

export interface EpicProgress {
  deliveredCount: number
  totalIssueCount: number
  blockedIssues: EpicProgressIssue[]
  activeIssues: EpicProgressIssue[]
  nextIssue: { id: string; number: number; title: string } | null
  nextIssueReason: string | null
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
  stage: WorkflowStage | ''
  health: IssueHealth
  priority: string | null
}

export interface EpicDetail extends Epic {
  linkedIssues: LinkedIssue[]
  progress: EpicProgress
}
