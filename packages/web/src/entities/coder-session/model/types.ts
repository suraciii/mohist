import type { SessionEvent } from '../../session/model/view'

export type SessionStatusKind = 'loading' | 'live' | 'probing' | 'finalizing' | 'completed' | 'failed' | 'stale'

export interface AgentSessionUsage {
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  cachedReadTokens?: number | null
  thoughtTokens?: number | null
  costAmount?: number | null
  costCurrency?: string | null
  contextWindowUsed?: number | null
  contextWindowSize?: number | null
  contextUsagePercent?: number | null
  healthStatus?: string | null
}

export interface AgentSessionEventSummary {
  resolvedModel?: string | null
  failureCategory?: string | null
  contextExhaustion?: boolean | null
  contextExhaustionSuspected?: boolean | null
  toolCallCount?: number | null
  toolErrorCount?: number | null
}

export interface AgentSessionMetadataCounts {
  partCount?: number
  eventCount?: number
  toolCount: number
  promptCount?: number
}

export type AgentSessionEvent = SessionEvent

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
  eventSummary?: AgentSessionEventSummary
  usage?: AgentSessionUsage
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
  eventSummary?: AgentSessionEventSummary
  usage?: AgentSessionUsage
}

export interface WorkflowRunSession {
  id: string
  workflowRunId: string
  sessionName: string
  acpSessionId: string | null
  projectId: string | null
  issueNumber: number | null
  runnerId: string | null
  status: string
  stage: string | null
  model: string | null
  workDir: string | null
  processPid: number | null
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  lastDataAt: string | null
  failureReason: string | null
  exitCode: number | null
  eventSummary?: AgentSessionEventSummary
  usage?: AgentSessionUsage
}

export type CoderSessionItem = CoderSessionSummary

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
  workspace?: string | null
  firstPromptSentAt?: string | null
  lastActivityAt?: string | null
  lastDataAt?: string | null
  probeSentAt?: string | null
  probeDeadlineAt?: string | null
  failureReason?: string | null
  partCount?: number
  eventCount?: number
  toolCount?: number
  turnCount?: number
  changedFiles?: FileChangeSummary[]
  warnings?: TranscriptWarning[]
  hasUnknownTools?: boolean
  eventSummary?: AgentSessionEventSummary
  usage?: AgentSessionUsage
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
}

export interface AgentSessionTranscriptResponse {
  turns: SessionTurn[]
  partCount: number
  lastActivityAt: string | null
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
