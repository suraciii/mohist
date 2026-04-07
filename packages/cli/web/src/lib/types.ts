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

export interface Question {
  id: string
  issueId: string
  question: string
  answer?: string
  status: 'pending' | 'answered' | 'expired'
  createdAt: string
  answeredAt?: string
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
  agent_paused: { issueId: string; projectId: string }
  agent_error: { issueId: string; projectId: string; error: string }
  approval_requested: { issueId: string; projectId: string; stage: string }
  question_asked: { issueId: string; projectId: string; questionId: string; question: string }
  question_answered: { issueId: string; projectId: string; questionId: string; answer: string }
  explore_crystallized: { sessionId: string; issueId: string; projectId: string }
}

export type EventName = keyof EventMap

export interface DirEntry {
  name: string
  absolute: string
}

export interface ExploreSession {
  id: string
  projectId: string
  issueId: string | null
  title: string
  status: 'active' | 'crystallized' | 'archived'
  createdAt: string
  updatedAt: string
}

export interface ExploreMessage {
  id: string
  sessionId: string
  role: 'user' | 'assistant'
  content: string
  toolCalls: ToolCallRecord[] | null
  createdAt: string
}

export interface ToolCallRecord {
  name: string
  args: Record<string, unknown>
  result: unknown
}

export interface ExploreSessionWithMessages {
  session: ExploreSession
  messages: ExploreMessage[]
}
