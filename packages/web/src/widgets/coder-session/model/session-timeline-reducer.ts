import {
  isContextHealthStatus,
  type ContextHealthStatus,
  type ToolCallEntry,
} from '../../../entities/coder-session'
import type { AgentDetailEventMap } from '../../../entities/agent'
import {
  viewSessionEvents,
  type SessionEvent,
  type SessionTimelineToolCall,
  type SessionTimelineRecovery,
  type SessionTimelineCompaction,
} from '../../../entities/session'

export interface RecoveryEvent {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt: number
  reason?: string
  timestamp: number
}

export type { ContextHealthStatus } from '../../../entities/coder-session'

export interface ContextHealthState {
  status: ContextHealthStatus | null
  contextWindowUsed: number | null
  contextWindowSize: number | null
  contextUsagePercent: number | null
  recordedAt: string | null
}

export function toContextHealthStatus(value: string | null | undefined): ContextHealthStatus | null {
  return isContextHealthStatus(value) ? value : null
}

export interface CompactionEntry {
  id: string
  strategy?: string
  contextWindowUsedBefore?: number | null
  contextWindowUsedAfter?: number | null
  contextWindowSize?: number | null
  summary?: string
  timestamp: number
  recordedAt: string
}

export interface Round {
  roundIndex: number
  label: string
  startedAt: string
  completedAt: string | null
  userText: string
  agentText: string
  thoughtText: string
  toolCalls: ToolCallEntry[]
  recoveryEvents: RecoveryEvent[]
  compactions: CompactionEntry[]
}

export interface RecoveryStatus {
  status: 'detected' | 'recovering' | 'recovered' | 'failed'
  attempt: number
  reason?: string
}

export function mapLivenessToRecoveryStatus(status: AgentDetailEventMap['session.liveness']['status']): RecoveryStatus['status'] {
  if (status === 'probing') return 'recovering'
  if (status === 'running') return 'recovered'
  return 'failed'
}

export const BASE_PLAN_STEPS: Array<{ roundType: string; roundLabel: string }> = [
  { roundType: 'proposal', roundLabel: 'Proposal' },
  { roundType: 'specs', roundLabel: 'Specs' },
  { roundType: 'design', roundLabel: 'Design' },
  { roundType: 'tasks', roundLabel: 'Tasks' },
  { roundType: 'self-review', roundLabel: 'Self Review' },
]

export interface PlanStep {
  roundType: string
  roundLabel: string
  roundIndex: number
  status: 'pending' | 'running' | 'completed' | 'failed'
  duration?: number
  verdict?: 'PASS' | 'FAIL'
}

export interface PlanProgress {
  steps: PlanStep[]
  completedCount: number
  totalSteps: number
}

export function deriveToolCallTitle(toolName: string, title: string | undefined, rawInput: string | undefined): string {
  if (title && title !== toolName) return title
  if (!rawInput) return toolName
  try {
    const parsed = JSON.parse(rawInput)
    if (typeof parsed !== 'object' || parsed === null) return toolName
    const lower = toolName.toLowerCase()
    if (['read', 'read_file', 'write', 'write_file', 'edit'].includes(lower)) {
      const fp = parsed.file_path ?? parsed.filePath ?? parsed.path
      if (typeof fp === 'string' && fp) return fp.split('/').pop() ?? fp
    }
    if (lower === 'bash') {
      const cmd = parsed.command ?? parsed.script
      if (typeof cmd === 'string' && cmd) return cmd.length > 60 ? cmd.slice(0, 57) + '...' : cmd
    }
    if (['glob', 'search_files', 'grep', 'search'].includes(lower)) {
      const pat = parsed.pattern ?? parsed.query ?? parsed.search
      if (typeof pat === 'string' && pat) return pat
    }
    return toolName
  } catch {
    return rawInput || toolName
  }
}

const PLAN_ROUND_LABELS = ['proposal.md', 'specs/', 'design.md', 'tasks.json', 'self-review']

function inferRoundLabel(roundIndex: number, totalRounds: number): string {
  if (roundIndex < PLAN_ROUND_LABELS.length && totalRounds <= PLAN_ROUND_LABELS.length) {
    return PLAN_ROUND_LABELS[roundIndex]
  }
  return `Round ${roundIndex + 1}`
}

function timelineToolCallToEntry(tool: SessionTimelineToolCall, fallbackAt: string): ToolCallEntry {
  const state: ToolCallEntry['state'] =
    tool.state === 'running' ? 'started' : tool.state
  return {
    executionId: '',
    toolName: tool.toolName,
    state,
    timestamp: new Date(tool.startedAt ?? fallbackAt).getTime(),
    toolCallId: tool.toolCallId,
    title: deriveToolCallTitle(tool.toolName, tool.title, tool.rawInput),
    rawInput: tool.rawInput,
    rawOutput: tool.rawOutput,
  }
}

function timelineRecoveryToEvent(recovery: SessionTimelineRecovery): RecoveryEvent {
  return {
    status: recovery.status,
    attempt: recovery.attempt ?? 1,
    reason: recovery.reason,
    timestamp: new Date(recovery.at).getTime(),
  }
}

function timelineCompactionToEntry(compaction: SessionTimelineCompaction, fallbackIndex: number): CompactionEntry {
  const id = compaction.id != null ? String(compaction.id) : `compaction-${compaction.at}-${fallbackIndex}`
  return {
    id,
    strategy: compaction.strategy,
    contextWindowUsedBefore: compaction.contextWindowUsedBefore ?? null,
    contextWindowUsedAfter: compaction.contextWindowUsedAfter ?? null,
    contextWindowSize: compaction.contextWindowSize ?? null,
    summary: compaction.summary,
    timestamp: new Date(compaction.at).getTime(),
    recordedAt: compaction.at,
  }
}

export function reconstructRoundsFromEvents(events: SessionEvent[]): Round[] {
  if (events.length === 0) return []
  const view = viewSessionEvents(events, 'timeline')
  const totalRounds = view.rounds.length
  return view.rounds.map((round) => ({
    roundIndex: round.roundIndex,
    label: inferRoundLabel(round.roundIndex, totalRounds),
    startedAt: round.startedAt,
    completedAt: round.completedAt,
    userText: round.userText,
    agentText: round.agentText,
    thoughtText: round.thoughtText,
    toolCalls: round.toolCalls.map((tool) => timelineToolCallToEntry(tool, round.startedAt)),
    recoveryEvents: round.recovery.map(timelineRecoveryToEvent),
    compactions: round.compactions.map((entry, idx) => timelineCompactionToEntry(entry, idx)),
  }))
}

export interface SessionTimelineState {
  rounds: Round[]
  planProgress: PlanProgress | null
  recoveryStatus: RecoveryStatus | null
  contextHealth: ContextHealthState | null
}

export interface SessionTimelineEnv {
  now: number
  isoNow: string
  randomId: () => string
}

export function mergeContextHealth(prev: ContextHealthState | null, next: ContextHealthState): ContextHealthState | null {
  if (!prev) return next
  if (
    prev.status === next.status
    && prev.contextWindowUsed === next.contextWindowUsed
    && prev.contextWindowSize === next.contextWindowSize
    && prev.contextUsagePercent === next.contextUsagePercent
  ) {
    return prev
  }
  return next
}

export function planRoundStartReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['plan_round_start'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const newRound: Round = {
    roundIndex: prev.rounds.length,
    label: detail.roundLabel ?? `Round ${prev.rounds.length + 1}`,
    startedAt: env.isoNow,
    completedAt: null,
    userText: '',
    agentText: '',
    thoughtText: '',
    toolCalls: [],
    recoveryEvents: [],
    compactions: [],
  }
  const steps: PlanStep[] = prev.planProgress?.steps
    ? [...prev.planProgress.steps]
    : BASE_PLAN_STEPS.map((s, i) => ({
        roundType: s.roundType,
        roundLabel: s.roundLabel,
        roundIndex: i,
        status: 'pending' as const,
      }))
  const idx = steps.findIndex((s) => s.roundType === detail.roundType)
  if (idx >= 0) {
    steps[idx] = { ...steps[idx], status: 'running' }
  } else {
    steps.push({
      roundType: detail.roundType,
      roundLabel: detail.roundLabel ?? detail.roundType,
      roundIndex: detail.roundIndex,
      status: 'running',
    })
  }
  return {
    rounds: [...prev.rounds, newRound],
    planProgress: {
      steps,
      completedCount: prev.planProgress?.completedCount ?? 0,
      totalSteps: prev.planProgress?.totalSteps ?? 5,
    },
    recoveryStatus: prev.recoveryStatus,
    contextHealth: prev.contextHealth,
  }
}

export function planRoundCompleteReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['plan_round_complete'],
  _env: SessionTimelineEnv,
): SessionTimelineState {
  const steps: PlanStep[] = prev.planProgress?.steps
    ? [...prev.planProgress.steps]
    : BASE_PLAN_STEPS.map((s, i) => ({
        roundType: s.roundType,
        roundLabel: s.roundLabel,
        roundIndex: i,
        status: i < detail.roundIndex ? ('completed' as const) : ('pending' as const),
      }))
  const isFailed = detail.verdict === 'FAIL'
  const idx = steps.findIndex((s) => s.roundType === detail.roundType)
  if (idx >= 0) {
    steps[idx] = {
      ...steps[idx],
      status: isFailed ? ('failed' as const) : ('completed' as const),
      duration: detail.duration,
      ...(detail.verdict ? { verdict: detail.verdict as 'PASS' | 'FAIL' } : {}),
    }
  }
  if (detail.roundType === 'self-review' && isFailed) {
    if (!steps.some((s) => s.roundType === 'auto-fix')) {
      steps.push({
        roundType: 'auto-fix',
        roundLabel: 'Auto Fix',
        roundIndex: steps.length,
        status: 'pending',
      })
    }
    if (!steps.some((s) => s.roundType === 're-self-review')) {
      steps.push({
        roundType: 're-self-review',
        roundLabel: 'Re Self Review',
        roundIndex: steps.length,
        status: 'pending',
      })
    }
  }
  const completedCount = steps.filter((s) => s.status === 'completed' || s.status === 'failed').length
  return {
    rounds: prev.rounds,
    planProgress: {
      steps,
      completedCount,
      totalSteps: prev.planProgress?.totalSteps ?? 5,
    },
    recoveryStatus: prev.recoveryStatus,
    contextHealth: prev.contextHealth,
  }
}

export function coderRecoveryStatusReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['coder_recovery_status'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const newRecoveryStatus: RecoveryStatus | null =
    detail.status === 'recovered' || detail.status === 'failed'
      ? null
      : {
          status: detail.status,
          attempt: detail.attempt,
          reason: detail.reason,
        }

  let nextRounds = prev.rounds
  if (prev.rounds.length > 0) {
    const next = [...prev.rounds]
    const lastRound = { ...next[next.length - 1] }
    lastRound.recoveryEvents = [
      ...lastRound.recoveryEvents,
      {
        status: detail.status,
        attempt: detail.attempt,
        reason: detail.reason,
        timestamp: env.now,
      },
    ]
    next[next.length - 1] = lastRound
    nextRounds = next
  }

  return {
    rounds: nextRounds,
    planProgress: prev.planProgress,
    recoveryStatus: newRecoveryStatus,
    contextHealth: prev.contextHealth,
  }
}

export function sessionLivenessReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['session.liveness'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const status = mapLivenessToRecoveryStatus(detail.status)
  const attempt = detail.activeProbeVersion ?? detail.satisfiedProbeVersion ?? detail.probeVersion ?? 1
  const reason = detail.failureReason
    ?? (detail.status === 'probing'
      ? `Probe sent; waiting for activity before ${detail.probeDeadlineAt ?? 'deadline unknown'}`
      : detail.lastActivityType)

  const setRecovery: RecoveryStatus | null =
    detail.status === 'running' || detail.status === 'failed' ? null : { status, attempt, reason }

  let nextRounds = prev.rounds
  if (prev.rounds.length > 0) {
    const next = [...prev.rounds]
    const lastRound = { ...next[next.length - 1] }
    lastRound.recoveryEvents = [
      ...lastRound.recoveryEvents,
      { status, attempt, reason, timestamp: env.now },
    ]
    next[next.length - 1] = lastRound
    nextRounds = next
  }

  return {
    rounds: nextRounds,
    planProgress: prev.planProgress,
    recoveryStatus: setRecovery,
    contextHealth: prev.contextHealth,
  }
}

export function usageUpdatedReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['usage.updated'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  if (
    detail.contextWindowUsed == null
    && detail.contextWindowSize == null
    && detail.contextUsagePercent == null
    && detail.healthStatus == null
  ) {
    return prev
  }
  const next: ContextHealthState = {
    status: toContextHealthStatus(detail.healthStatus),
    contextWindowUsed: detail.contextWindowUsed ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    contextUsagePercent: detail.contextUsagePercent ?? null,
    recordedAt: env.isoNow,
  }
  return {
    rounds: prev.rounds,
    planProgress: prev.planProgress,
    recoveryStatus: prev.recoveryStatus,
    contextHealth: mergeContextHealth(prev.contextHealth, next),
  }
}

export function contextHealthUpdateReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['context_health_update'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const next: ContextHealthState = {
    status: toContextHealthStatus(detail.healthStatus),
    contextWindowUsed: detail.contextWindowUsed ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    contextUsagePercent: detail.contextUsagePercent ?? null,
    recordedAt: detail.recordedAt ?? env.isoNow,
  }
  return {
    rounds: prev.rounds,
    planProgress: prev.planProgress,
    recoveryStatus: prev.recoveryStatus,
    contextHealth: mergeContextHealth(prev.contextHealth, next),
  }
}

function appendCompactionToRounds(rounds: Round[], entry: CompactionEntry, recordedAt: string): Round[] {
  if (rounds.length === 0) {
    const placeholder: Round = {
      roundIndex: 0,
      label: 'Compaction',
      startedAt: recordedAt,
      completedAt: recordedAt,
      userText: '',
      agentText: '',
      thoughtText: '',
      toolCalls: [],
      recoveryEvents: [],
      compactions: [entry],
    }
    return [placeholder]
  }
  const next = [...rounds]
  const lastRound = { ...next[next.length - 1] }
  lastRound.compactions = [...lastRound.compactions, entry]
  next[next.length - 1] = lastRound
  return next
}

export function compactionEventReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['compaction_event'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const recordedAt = detail.recordedAt ?? env.isoNow
  const entry: CompactionEntry = {
    id: `compaction-${recordedAt}-${env.randomId()}`,
    strategy: detail.strategy,
    contextWindowUsedBefore: detail.contextWindowUsedBefore ?? null,
    contextWindowUsedAfter: detail.contextWindowUsedAfter ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    summary: detail.summary,
    timestamp: new Date(recordedAt).getTime(),
    recordedAt,
  }
  const nextRounds = appendCompactionToRounds(prev.rounds, entry, recordedAt)
  const nextHealth: ContextHealthState = {
    status: null,
    contextWindowUsed: detail.contextWindowUsedAfter ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    contextUsagePercent: null,
    recordedAt,
  }
  return {
    rounds: nextRounds,
    planProgress: prev.planProgress,
    recoveryStatus: prev.recoveryStatus,
    contextHealth: mergeContextHealth(prev.contextHealth, nextHealth),
  }
}

export function contextCompactedReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['com.mohist.agent-session.context-compacted'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const recordedAt = detail.recordedAt ?? env.isoNow
  const entry: CompactionEntry = {
    id: `compaction-domain-${recordedAt}-${env.randomId()}`,
    strategy: detail.strategy ?? undefined,
    contextWindowUsedBefore: detail.contextWindowUsedBefore ?? null,
    contextWindowUsedAfter: detail.contextWindowUsedAfter ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    summary: detail.summary ?? undefined,
    timestamp: new Date(recordedAt).getTime(),
    recordedAt,
  }
  const nextRounds = appendCompactionToRounds(prev.rounds, entry, recordedAt)
  const nextHealth: ContextHealthState = {
    status: null,
    contextWindowUsed: detail.contextWindowUsedAfter ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    contextUsagePercent: null,
    recordedAt,
  }
  return {
    rounds: nextRounds,
    planProgress: prev.planProgress,
    recoveryStatus: prev.recoveryStatus,
    contextHealth: mergeContextHealth(prev.contextHealth, nextHealth),
  }
}

export function contextHealthUpdatedReducer(
  prev: SessionTimelineState,
  detail: AgentDetailEventMap['com.mohist.agent-session.context-health-updated'],
  env: SessionTimelineEnv,
): SessionTimelineState {
  const next: ContextHealthState = {
    status: toContextHealthStatus(detail.healthStatus),
    contextWindowUsed: detail.contextWindowUsed ?? null,
    contextWindowSize: detail.contextWindowSize ?? null,
    contextUsagePercent: detail.contextUsagePercent ?? null,
    recordedAt: detail.recordedAt ?? env.isoNow,
  }
  return {
    rounds: prev.rounds,
    planProgress: prev.planProgress,
    recoveryStatus: prev.recoveryStatus,
    contextHealth: mergeContextHealth(prev.contextHealth, next),
  }
}
