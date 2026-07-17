import type { AgentSessionEventSummary, AgentSessionUsage } from '../../coder-session/@x/agent-session'

export interface AgentProgress {
  stage: string
  roundType?: string
  roundIndex?: number
  taskProgress?: { completed: number; total: number } | null
  lastActivityAt: string
}

export interface ActiveAgentInfo {
  issueId: string
  issueNumber: number
  projectId: string
  progress?: AgentProgress
}

export interface AgentStatus {
  running: boolean
  issueId: string | null
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
  issueId: string
  projectId: string
  roundType: string
  roundLabel: string
  roundIndex: number
  runtimeSessionId?: string
  sessionId?: string
}

export type PlanSessionUpdateEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundIndex: number
  sessionUpdate: string
  data: unknown
  runtimeSessionId?: string
  sessionId?: string
}

export type PlanRoundCompleteEvent = {
  issueId: string
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
  agent_text_chunk: { issueId: string; projectId: string; text: string; stepIndex: number }
  main_tool_call: { issueId: string; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number; stepIndex?: number }
  coder_text_chunk: { issueId: string; projectId: string; executionId?: string; runtimeSessionId: string; runtime?: string; text: string; sessionId?: string; model?: string }
  coder_thought_chunk: { issueId: string; projectId: string; executionId?: string; runtimeSessionId: string; runtime?: string; text: string; sessionId?: string; model?: string }
  coder_tool_call: { issueId: string; projectId: string; executionId?: string; runtimeSessionId: string; runtime?: string; toolName: string; state?: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'; status?: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; rawOutputMetadata?: Record<string, unknown>; metadata?: Record<string, unknown>; details?: Record<string, unknown>; normalizedName?: string; displayTitle?: string; displaySubtitle?: string; category?: string; sessionId?: string; model?: string }
  'session.input': SessionRuntimeBase & { text: string; kind?: string; sentAt?: string }
  'message.delta': SessionRuntimeBase & { text: string; model?: string }
  'reasoning.delta': SessionRuntimeBase & { text: string; model?: string }
  'tool_call.started': SessionRuntimeBase & { toolName: string; state: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; rawOutputMetadata?: Record<string, unknown>; metadata?: Record<string, unknown>; details?: Record<string, unknown>; normalizedName?: string; displayTitle?: string; displaySubtitle?: string; category?: string; model?: string }
  'tool_call.updated': AgentDetailEventMap['tool_call.started']
  'tool_call.completed': AgentDetailEventMap['tool_call.started']
  plan_round_start: PlanRoundStartEvent
  plan_session_update: PlanSessionUpdateEvent
  plan_round_complete: PlanRoundCompleteEvent
  coder_recovery_status: { issueId: string; projectId: string; executionId: string; runtimeSessionId: string; runtime?: string; sessionId?: string; status: 'detected' | 'recovering' | 'recovered' | 'failed'; attempt: number; reason?: string }
  'session.liveness': SessionRuntimeBase & { status: 'probing' | 'running' | 'failed'; lastDataAt: string; lastActivityType?: string; probeSentAt?: string; probeDeadlineAt?: string; probeVersion?: number; dataVersion?: number; postProbeActivity?: boolean; activeProbeVersion?: number; satisfiedProbeVersion?: number; failureReason?: string }
  coder_session_started: { issueId: string; projectId: string; workflowRunId?: string; sessionName?: string; sessionId: string; runtimeSessionId: string; executionId?: string; model?: string; runtime?: string; stage?: string; taskDescription?: string; title?: string | null }
  coder_session_completed: { issueId: string; projectId: string; sessionId: string; status: 'completed' | 'failed'; duration: number }
  coder_session_failed: { issueId: string; projectId: string; sessionId: string; reason?: string }
  coder_session_cancelled: { issueId: string; projectId: string; sessionId: string; reason?: string }
  coder_session_status_changed: SessionRuntimeBase & { issueId: string; projectId: string; status: string; lastDataAt?: string | null; probeSentAt?: string | null; probeDeadlineAt?: string | null; failureReason?: string | null }
  'session.closed': SessionRuntimeBase & { status: 'completed' | 'failed' | 'cancelled' | string; failureReason?: string | null; failureCategory?: string | null; exitCode?: number | null }
  'usage.updated': SessionRuntimeBase & {
    inputTokens?: number
    outputTokens?: number
    totalTokens?: number
    cachedReadTokens?: number
    thoughtTokens?: number
    costAmount?: number
    costCurrency?: string
    contextWindowSize?: number
    contextWindowUsed?: number
    contextUsagePercent?: number
    healthStatus?: string
  }
  'model.resolved': SessionRuntimeBase & { model: string }
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
  'com.mohist.agent-session.runtime-bound': { issueId: string; projectId: string }
  'com.mohist.agent-session.usage-recorded': { issueId: string; projectId: string }
  'com.mohist.agent-session.model-changed': { issueId: string; projectId: string }
  'com.mohist.agent-session.context-compacted': { issueId: string; projectId: string; strategy?: string | null; contextWindowUsedBefore?: number | null; contextWindowUsedAfter?: number | null; contextWindowSize?: number | null; summary?: string | null; recordedAt?: string }
  'com.mohist.agent-session.context-exhausted': { issueId: string; projectId: string; failureCategory?: string | null; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
  'com.mohist.agent-session.context-health-updated': { issueId: string; projectId: string; healthStatus: string; contextUsagePercent?: number | null; contextWindowUsed?: number | null; contextWindowSize?: number | null; recordedAt?: string }
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
  issueId: string
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
  issueId: string
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
