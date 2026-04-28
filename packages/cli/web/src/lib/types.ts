export enum Stage {
  Draft = 'draft',
  Explore = 'explore',
  Plan = 'plan',
  Build = 'build',
  Review = 'review',
  Done = 'done',
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked',
  Interrupted = 'interrupted',
  Closed = 'closed',
  Completed = 'completed',
}

export interface ApprovalState {
  status: 'awaiting' | 'approved' | 'rejected'
  stage?: string
  output?: Record<string, unknown>
  requestedAt: string
  approvedAt?: string
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
  approvalState?: ApprovalState
  mergeState?: 'pending' | 'merging' | 'merged' | 'build-failed' | 'conflict' | null
  priority?: string | null
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
  activeAgents: Array<{ issueId: string; issueNumber: number; projectId: string }>
  maxConcurrentAgents: number
  queueDepth: number
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>
  recoverableIssues: Array<{ issueNumber: number; stage: string }>
}

export interface DiffFile {
  file: string
  additions: number
  deletions: number
}

export type PlanRoundStartEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundLabel: string
  roundIndex: number
}

export type PlanSessionUpdateEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundIndex: number
  sessionUpdate: string
  data: unknown
}

export type AgentDetailEventMap = {
  agent_text_chunk: { issueId: string; projectId: string; text: string; stepIndex: number }
  main_tool_call: { issueId: string; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number; stepIndex?: number }
  coder_text_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string }
  coder_tool_call: { issueId: string; projectId: string; executionId: string; acpSessionId: string; toolName: string; state: 'started' | 'completed'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown }
  ralph_task_update: { issueId: string; projectId: string; executionId: string; taskId: string; taskIndex: number; totalTasks: number; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt?: number; error?: string }
  ralph_loop_progress: { issueId: string; projectId: string; executionId: string; completed: number; failed: number; total: number }
  plan_round_start: PlanRoundStartEvent
  plan_session_update: PlanSessionUpdateEvent
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
  merge_queued: { issueId: string; projectId: string; issueNumber: number; position: number }
  merge_started: { issueId: string; projectId: string; issueNumber: number }
  merge_completed: { issueId: string; projectId: string; issueNumber: number }
  merge_failed: { issueId: string; projectId: string; issueNumber: number; reason: string }
} & AgentDetailEventMap

export type EventName = keyof EventMap

export interface DirEntry {
  name: string
  absolute: string
}

export interface ExploreSession {
  id: string
  projectId: string
  issueId: string | null
  issueNumber?: number
  title: string
  status: 'active' | 'crystallized' | 'archived'
  model?: string
  variant?: string
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

export interface LogTailResult {
  file: string
  cursor: number
  lines: string[]
  truncated: boolean
  reset: boolean
}

export type ModelBadge = 'free' | 'latest'

export interface Model {
  id: string
  name: string
  badges: ModelBadge[]
  contextWindow: number
}

export interface ModelProvider {
  id: string
  name: string
  configured: boolean
  models: Model[]
}

export interface AgentSessionMessageItem {
  id: string
  role: string
  content: string | null
  toolCalls: string | null
  toolCallId: string | null
  toolName: string | null
  toolResult: string | null
  stepIndex: number
  createdAt: string
}

export interface WorkflowLogItem {
  id: string
  eventType: string
  data: unknown
  createdAt: string
}

export interface CoderSessionItem {
  id: string
  acpSessionId: string
  executionId: string | null
  taskDescription: string | null
  status: string
  createdAt: string
  completedAt: string | null
  workflowLogs: WorkflowLogItem[]
}

export interface ToolCallEntry {
  executionId: string
  toolName: string
  state: 'started' | 'completed' | 'failed'
  args?: string
  result?: string
  error?: string
  duration?: number
  stepIndex?: number
  timestamp: number
  acpSessionId?: string
  toolCallId?: string
  rawInput?: string
  rawOutput?: string
  title?: string
}

export interface CoderTextBuffer {
  executionId: string
  acpSessionId: string
  text: string
}

export interface Task {
  id: string
  title: string
  description?: string
  acceptanceCriteria?: string[]
  dependsOn?: string[]
  passes: boolean
  attempts: number
  error?: string | null
}

export interface BuildStatus {
  stage: string
  status: string
  progress: {
    completed: number
    failed: number
    total: number
    currentTask: string | null
  }
  tasks: Task[]
}
