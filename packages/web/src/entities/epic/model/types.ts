import type { IssueStartBlocker, IssueStatus, IssueHealth, WorkflowStage } from '../../issue/@x/types'

export enum EpicStatus {
  Idle = 'idle',
  Running = 'running',
  Paused = 'paused',
  Done = 'done',
  Closed = 'closed',
}

export function parseEpicStatus(value: string | null | undefined): EpicStatus {
  if (!value) return EpicStatus.Idle
  const normalized = value.toLowerCase()
  if (normalized === 'active') return EpicStatus.Idle
  if (normalized === EpicStatus.Running) return EpicStatus.Running
  if (normalized === EpicStatus.Paused) return EpicStatus.Paused
  if (normalized === EpicStatus.Done) return EpicStatus.Done
  if (normalized === EpicStatus.Closed) return EpicStatus.Closed
  return EpicStatus.Idle
}

export type EpicPriority = 'p0' | 'p1' | 'p2' | 'p3' | 'p4'

export interface Epic {
  projectId: string
  number: number
  title: string
  description: string
  priority: EpicPriority
  status: EpicStatus
  pauseReason?: string | null
  createdAt: string
  updatedAt: string
}

export interface EpicProgressIssue {
  number: number
  title: string
  health: IssueHealth
}

export interface EpicProgress {
  deliveredCount: number
  totalIssueCount: number
  blockedIssues: EpicProgressIssue[]
  activeIssues: EpicProgressIssue[]
  nextIssue: { number: number; title: string } | null
  nextIssueReason: string | null
  readyToMarkDone: boolean
}

export interface EpicWithProgress extends Epic {
  progress: EpicProgress
}

export interface LinkedIssueExternalPrerequisite {
  number: number
  title: string
  stage: string
  status: string
}

export interface LinkedIssue {
  number: number
  title: string
  status: IssueStatus
  stage: WorkflowStage | ''
  health: IssueHealth
  priority: string | null
  canStart: boolean
  startBlocker: IssueStartBlocker | null
  prerequisiteNumbers: number[]
  externalPrerequisites: LinkedIssueExternalPrerequisite[]
}

export interface EpicDetail extends Epic {
  linkedIssues: LinkedIssue[]
  progress: EpicProgress
}

export interface StoredCloudEventDto {
  id: number
  eventId: string
  source: string
  type: string
  specVersion: string
  subject: string | null
  time: string
  dataContentType: string | null
  data: unknown
  extensions: Record<string, string>
}
