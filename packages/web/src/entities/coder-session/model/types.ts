export interface WorkflowLogItem {
  id: string
  eventType: string
  data: unknown
  createdAt: string
}

export type SessionStatusKind = 'loading' | 'live' | 'probing' | 'finalizing' | 'completed' | 'failed' | 'stale'

export interface AgentSessionMetadataCounts {
  eventCount: number
  toolCount: number
  messageChunkCount?: number
  thoughtChunkCount?: number
  promptCount?: number
}

export interface AgentSessionMetadata {
  id: string
  sessionName: string
  acpSessionId: string
  status: string
  statusKind?: SessionStatusKind
  model: string | null
  stage: string | null
  title: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt?: string | null
  lastDataAt?: string | null
  probeSentAt?: string | null
  probeDeadlineAt?: string | null
  failureReason?: string | null
  turnCount?: number
  changedFiles?: FileChangeSummary[]
  metadata: AgentSessionMetadataCounts
}

export interface AgentSessionEvent {
  id: number
  sequence: number
  type: string
  payload: unknown
  createdAt: string
}

export interface AgentSessionEventsResponse {
  events: AgentSessionEvent[]
}

export interface FileChangeSummary {
  path: string
  operation: 'created' | 'modified' | 'deleted' | 'moved'
  additions?: number
  deletions?: number
  oldPath?: string
  rawDetail?: string
}

export interface CoderSessionSummary {
  id: string
  sessionName?: string | null
  acpSessionId: string
  executionId: string | null
  taskDescription: string | null
  status: string
  createdAt: string
  completedAt: string | null
  model: string | null
  coderType: string | null
  stage: string | null
  title: string | null
  lastDataAt: string | null
  probeSentAt: string | null
  probeDeadlineAt: string | null
  failureReason: string | null
}

export type CoderSessionItem = CoderSessionSummary & {
  workflowLogs?: WorkflowLogItem[]
}

export type PromptKind = 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing'

export interface TranscriptWarning {
  code: string
  message: string
}

export interface PromptSummary {
  title?: string
  subtitle?: string
  outputPath?: string
  contextFiles?: string[]
  kind: PromptKind
  rawText?: string
}

export interface SessionMetadata {
  sessionId: string
  sessionName?: string | null
  coderSessionId: string
  issueId: string
  acpSessionId: string
  executionId: string | null
  title: string | null
  status: string
  statusKind?: SessionStatusKind
  model: string | null
  stage: string | null
  createdAt: string
  completedAt: string | null
  cwd?: string | null
  worktree?: string | null
  firstPromptSentAt?: string | null
  lastActivityAt?: string | null
  lastDataAt?: string | null
  probeSentAt?: string | null
  probeDeadlineAt?: string | null
  failureReason?: string | null
  eventCount?: number
  toolCount?: number
  turnCount?: number
  changedFiles?: FileChangeSummary[]
  warnings?: TranscriptWarning[]
  hasUnknownTools?: boolean
}

export interface TextPart {
  id: string
  type: 'text'
  text: string
  startedAt: string
  completedAt: string | null
}

export interface ReasoningPart {
  id: string
  type: 'reasoning'
  text: string
  startedAt: string
  completedAt: string | null
}

export interface ToolPart {
  id: string
  type: 'tool'
  hidden?: boolean
  tool: {
    toolCallId: string
    normalizedName?: string
    displayTitle?: string
    displaySubtitle?: string
    category?: string
    toolName: string
    status: 'pending' | 'running' | 'completed' | 'failed' | 'cancelled'
    title?: string
    target?: string
    input?: string
    output?: string
    error?: string
    startedAt: string
    completedAt?: string | null
    rawInput?: string
    rawOutput?: string
    metadata?: Record<string, unknown>
    changedFiles?: FileChangeSummary[]
    warnings?: TranscriptWarning[]
    details?: Record<string, unknown>
  }
}

export interface ErrorPart {
  id: string
  type: 'error'
  message: string
  kind: 'timeout' | 'failed' | 'cancelled' | 'recovery'
  at: string
}

export type SessionPart = TextPart | ReasoningPart | ToolPart | ErrorPart

export interface SessionTurn {
  id: string
  startedAt: string
  completedAt: string | null
  incomplete?: boolean
  user: {
    role: 'mohist'
    text: string
    kind: PromptKind
    sentAt: string
    summary?: PromptSummary
  }
  assistant: SessionPart[]
}

export interface CoderSessionDetail {
  id: string
  acpSessionId: string
  executionId: string | null
  taskDescription: string | null
  status: string
  createdAt: string
  completedAt: string | null
  model: string | null
  coderType: string | null
  stage: string | null
  title: string | null
  metadata: SessionMetadata
  turns: SessionTurn[]
  incomplete: boolean
  workflowLogs?: WorkflowLogItem[]
}

export interface ToolCallEntry {
  executionId: string
  toolName: string
  state: 'started' | 'completed' | 'failed' | 'pending' | 'running' | 'cancelled'
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
  changedFiles?: FileChangeSummary[]
}

export interface TaskProgressEntry {
  taskId: string
  taskIndex: number
  totalTasks: number
  status: 'pending' | 'running' | 'passed' | 'failed' | 'retrying'
  executionId?: string
  attempt?: number
  error?: string
}

export type TaskProgressMap = Map<string, TaskProgressEntry>

export interface LoopProgress {
  completed: number
  failed: number
  total: number
}

export interface CoderTextBuffer {
  executionId: string
  acpSessionId: string
  text: string
}
