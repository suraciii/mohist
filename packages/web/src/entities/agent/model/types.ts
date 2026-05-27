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
  runners?: Array<{ id: string; kind?: string | null }>
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
  acpSessionId?: string
  coderSessionId?: string
}

export type PlanSessionUpdateEvent = {
  issueId: string
  projectId: string
  roundType: string
  roundIndex: number
  sessionUpdate: string
  data: unknown
  acpSessionId?: string
  coderSessionId?: string
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

export type AgentDetailEventMap = {
  agent_text_chunk: { issueId: string; projectId: string; text: string; stepIndex: number }
  main_tool_call: { issueId: string; projectId: string; executionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; args?: string; result?: string; error?: string; duration?: number; stepIndex?: number }
  coder_text_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string; coderSessionId?: string; model?: string }
  coder_thought_chunk: { issueId: string; projectId: string; executionId: string; acpSessionId: string; text: string; coderSessionId?: string; model?: string }
  coder_tool_call: { issueId: string; projectId: string; executionId: string; acpSessionId: string; toolName: string; state: 'started' | 'completed' | 'failed'; toolCallId: string; title?: string; rawInput?: unknown; rawOutput?: unknown; rawOutputMetadata?: Record<string, unknown>; metadata?: Record<string, unknown>; details?: Record<string, unknown>; normalizedName?: string; displayTitle?: string; displaySubtitle?: string; category?: string; coderSessionId?: string; model?: string }
  ralph_task_update: { issueId: string; projectId: string; executionId: string; taskId: string; taskIndex: number; totalTasks: number; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt?: number; error?: string }
  ralph_loop_progress: { issueId: string; projectId: string; executionId: string; completed: number; failed: number; total: number }
  plan_round_start: PlanRoundStartEvent
  plan_session_update: PlanSessionUpdateEvent
  plan_round_complete: PlanRoundCompleteEvent
  coder_recovery_status: { issueId: string; projectId: string; executionId: string; acpSessionId: string; status: 'detected' | 'recovering' | 'recovered' | 'failed'; attempt: number; reason?: string }
  coder_session_started: { issueId: string; projectId: string; coderSessionId: string; acpSessionId: string; executionId?: string; model?: string; coderType?: string; stage?: string; taskDescription?: string; title?: string | null }
  coder_session_completed: { issueId: string; projectId: string; coderSessionId: string; status: 'completed' | 'failed'; duration: number }
  coder_session_failed: { issueId: string; projectId: string; coderSessionId: string; reason?: string }
  coder_session_cancelled: { issueId: string; projectId: string; coderSessionId: string; reason?: string }
  coder_session_status_changed: { issueId: string; projectId: string; coderSessionId: string; acpSessionId: string; status: string; lastDataAt?: string | null; probeSentAt?: string | null; probeDeadlineAt?: string | null; failureReason?: string | null }
  agent_paused: { issueId: string; projectId: string }
  check_update: { issueId: string; projectId: string; checkName: string; status: string; duration?: number; autoFixed?: boolean; verdict?: string; snapshotSha?: string }
  check_suite_status_changed: { issueId: string; projectId: string; issueNumber: number; suiteStatus: string; snapshotSha: string }
  stage_task_update: { issueId: string; projectId: string; stage: string; taskId: string; taskTitle: string; status: 'started' | 'completed' | 'failed' | 'retrying'; attempt: number; artifacts: string[] }
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
}

export interface AgentActivityWaiting {
  issueId: string
  issueNumber: number
  issueTitle: string
  stage: string | null
  label: 'Needs Approval'
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
