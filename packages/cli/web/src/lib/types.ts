export enum Stage {
  Draft = 'draft',
  Plan = 'plan',
  Build = 'build',
  Check = 'check',
  Done = 'done',
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
}

export interface Issue {
  id: string
  number: number
  title: string
  body?: string
  stage: Stage
  status: IssueStatus
  projectId: string
  labels: string[]
  createdAt: string
  updatedAt: string
  projectName?: string
  projectPath?: string
  comments?: Comment[]
}

export interface Project {
  id: string
  name: string
  path: string
  createdAt: string
  updatedAt: string
}

export interface Comment {
  id: string
  issueId: string
  body: string
  createdAt: string
}

export interface ApiResponse<T = unknown> {
  success: boolean
  data?: T
  error?: string
}

export interface AgentStatus {
  running: boolean
  issueId: string | null
  issueNumber: number | null
}

export interface DiffFile {
  file: string
  additions: number
  deletions: number
}

export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string }
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string }
  agent_started: { issueId: string; projectId: string }
  agent_completed: { issueId: string; projectId: string }
  agent_error: { issueId: string; projectId: string; error: string }
  approval_requested: { issueId: string; projectId: string; stage: string }
}

export type EventName = keyof EventMap
