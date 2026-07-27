import type { AgentSessionEventSummary, AgentSessionUsage } from '../../coder-session/@x/agent-session'

export interface AgentProgress {
  stage: string
  roundType?: string
  roundIndex?: number
  taskProgress?: { completed: number; total: number } | null
  lastActivityAt: string
}

export interface ActiveAgentInfo {
  issueNumber: number
  projectId: string
  progress?: AgentProgress
}

export interface AgentStatus {
  running: boolean
  issueNumber: number | null
  activeAgents: ActiveAgentInfo[]
  runnerAvailable?: boolean
  embeddedRunnerEnabled?: boolean
  runnerMessage?: string | null
  runners?: Array<{ id: string; kind?: string | null; active?: number; max?: number }>
  capacity: {
    active: number
    max: number
  }
}

export type PlanRoundStartEvent = {
  issueNumber: number
  projectId: string
  roundType: string
  roundLabel: string
  roundIndex: number
  runtimeSessionId?: string
  sessionId?: string
}

export type PlanSessionUpdateEvent = {
  issueNumber: number
  projectId: string
  roundType: string
  roundIndex: number
  sessionUpdate: string
  data: unknown
  runtimeSessionId?: string
  sessionId?: string
}

export type PlanRoundCompleteEvent = {
  issueNumber: number
  projectId: string
  roundType: string
  roundLabel?: string
  roundIndex: number
  duration: number
  verdict?: string
}

type SessionRuntimeBase = {
  runtimeSessionId?: string
  sessionId?: string
  runtime?: string
}

export type AgentDetailEventMap = {
  agent_text_chunk: { issueNumber: number; projectId: string; text: string; stepIndex: number }
  main_tool_call: { issueNumber: number; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number; stepIndex?: number }
  coder_text_chunk: { issueNumber: number; projectId: string; executionId?: string; runtimeSessionId: string; runtime?: string; text: string; sessionId?: string; model?: string }
  coder_thought_chunk: { issueNumber: number; projectId: string; executionId?: string; runtimeSessionId: string; runtime?: string; text: string; sessionId?: string; model?: string }
  coder_tool_call: { issueNumber: number; projectId: string; executionId?: string; runtimeSessionId: string; runtime?: string; toolName: string; state?: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'; status?: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; rawOutputMetadata?: Record<string, unknown>; metadata?: Record<string, unknown>; details?: Record<string, unknown>; normalizedName?: string; displayTitle?: string; displaySubtitle?: string; category?: string; sessionId?: string; model?: string }
  'session.input': SessionRuntimeBase & { text: string; kind?: string; sentAt?: string }
  'session.activity': SessionRuntimeBase & { activity: 'idle' | 'active' | 'unknown'; observedAt?: string }
  'message.delta': SessionRuntimeBase & { text: string; model?: string }
  'reasoning.delta': SessionRuntimeBase & { text: string; model?: string }
  'tool_call.started': SessionRuntimeBase & { toolName: string; state: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; rawOutputMetadata?: Record<string, unknown>; metadata?: Record<string, unknown>; details?: Record<string, unknown>; normalizedName?: string; displayTitle?: string; displaySubtitle?: string; category?: string; model?: string }
  'tool_call.updated': AgentDetailEventMap['tool_call.started']
  'tool_call.completed': AgentDetailEventMap['tool_call.started']
  plan_round_start: PlanRoundStartEvent
  plan_session_update: PlanSessionUpdateEvent
  plan_round_complete: PlanRoundCompleteEvent
  coder_recovery_status: { issueNumber: number; projectId: string; executionId: string; runtimeSessionId: string; runtime?: string; sessionId?: string; status: 'detected' | 'recovering' | 'recovered' | 'failed'; attempt: number; reason?: string }
  'session.liveness': SessionRuntimeBase & { status: 'probing' | 'running' | 'failed'; lastDataAt: string; lastActivityType?: string; probeSentAt?: string; probeDeadlineAt?: string; probeVersion?: number; dataVersion?: number; postProbeActivity?: boolean; activeProbeVersion?: number; satisfiedProbeVersion?: number; failureReason?: string }
  coder_session_started: { issueNumber: number; projectId: string; workflowRunId?: string; sessionName?: string; sessionId: string; runtimeSessionId: string; executionId?: string; model?: string; runtime?: string; stage?: string; taskDescription?: string; title?: string | null }
  coder_session_completed: { issueNumber: number; projectId: string; sessionId: string; status: 'completed' | 'failed'; duration: number }
  coder_session_failed: { issueNumber: number; projectId: string; sessionId: string; reason?: string }
  coder_session_cancelled: { issueNumber: number; projectId: string; sessionId: string; reason?: string }
  coder_session_status_changed: SessionRuntimeBase & { issueNumber: number; projectId: string; status: string; lastDataAt?: string | null; probeSentAt?: string | null; probeDeadlineAt?: string | null; failureReason?: string | null }
  'usage.updated': SessionRuntimeBase & {
    inputTokens?: number
    outputTokens?: number
    totalTokens?: number
    cachedReadTokens?: number
    cachedWriteTokens?: number
    thoughtTokens?: number
    costAmount?: number
    costCurrency?: string
    contextWindowSize?: number
    contextWindowUsed?: number
    contextUsagePercent?: number
    healthStatus?: string
  }
  'model.resolved': SessionRuntimeBase & { resolvedModel: string }
  'compaction': SessionRuntimeBase & {
    strategy?: string
    contextWindowUsedBefore?: number | null
    contextWindowUsedAfter?: number | null
    contextWindowSize?: number | null
    summary?: string
    recordedAt?: string
  }
  'compaction_event': SessionRuntimeBase & {
    strategy?: string
    contextWindowUsedBefore?: number | null
    contextWindowUsedAfter?: number | null
    contextWindowSize?: number | null
    summary?: string
    recordedAt?: string
  }
  'context_health_update': SessionRuntimeBase & {
    healthStatus: 'green' | 'yellow' | 'red'
    contextWindowSize?: number | null
    contextWindowUsed?: number | null
    contextUsagePercent?: number | null
    recordedAt?: string
  }
  'provider.retry': SessionRuntimeBase & {
    phase?: string
    attempt?: number
    maxAttempts?: number
    delayMs?: number
    message?: string
  }
  'com.mohist.agent-session.runtime-bound': { issueNumber: number; projectId: string }
  'com.mohist.agent-session.usage-recorded': { issueNumber: number; projectId: string }
  'com.mohist.agent-session.model-changed': { issueNumber: number; projectId: string }
  'com.mohist.agent-session.context-compacted': { issueNumber: number; projectId: string; sessionId?: string | null; strategy?: string | null; contextWindowUsedBefore?: number | null; contextWindowUsedAfter?: number | null; contextWindowSize?: number | null; summary?: string | null; recordedAt?: string }
  'com.mohist.agent-session.context-exhausted': { issueNumber: number; projectId: string; failureCategory?: string | null; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
  'com.mohist.agent-session.context-health-updated': { issueNumber: number; projectId: string; sessionId?: string | null; healthStatus: string; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
}

export interface AgentSessionInfo {
  issueNumber: number
  issueTitle: string
  issueStage: string
  sessionId: string
  status: string
  model: string | null
  taskDescription: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt: string | null
}

export interface AgentActivityWorkItem {
  type: string
  id: string
  title: string
  stage: string | null
  sessionWorkType: string | null
}

export interface AgentActivityTaskProgress {
  completed: number
  total: number
}

export interface AgentActivityPreview {
  kind: 'text' | 'tool'
  text: string
  createdAt: string
}

export interface AgentActivitySession {
  issueNumber: number
  issueTitle: string
  issueStage: string
  issueStatus: string | null
  sessionId: string
  status: string
  model: string | null
  taskDescription: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt: string
  currentWorkItem: AgentActivityWorkItem | null
  taskProgress: AgentActivityTaskProgress | null
  lastActivity: AgentActivityPreview | null
  failureReason: string | null
  eventSummary?: AgentSessionEventSummary
  usage?: AgentSessionUsage
  agentId?: string | null
  agentName?: string | null
}

export interface AgentActivityWaiting {
  issueNumber: number
  issueTitle: string
  stage: string | null
  label: 'Needs Approval' | 'Blocked'
  requestedAt: string | null
  preview: string | null
}

export interface AgentActivity {
  summary: {
    active: number
    waiting: number
    completed: number
    failed: number
    slots: {
      active: number
      max: number
    }
  }
  sessions: AgentActivitySession[]
  waiting: AgentActivityWaiting[]
}
