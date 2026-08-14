import type { AgentTranscriptDetail } from '../../../entities/agent'
import type {
  AgentSessionActivity,
  AgentTurnObservation,
  FileChangeSummary,
  SessionInputObservation,
  SessionPart,
  SessionRecoveryObservation,
  SessionTurn,
} from '../../../entities/coder-session'
import type {
  TimelineFact,
  TimelineFactKind,
  TimelineFileChange,
  TimelineToolStatus,
} from '../../../entities/session'
import { sanitizePublicAgentEvent } from './transcript-public'

export interface SessionTimelineSummaryInput {
  activity?: AgentSessionActivity | null
  lastActivityAt?: string | null
  currentTurnId?: string | null
  inputs?: readonly SessionInputObservation[] | null
  turns?: readonly AgentTurnObservation[] | null
  recoveryHistory?: readonly SessionRecoveryObservation[] | null
}

export interface SessionTimelineFactInput {
  transcript?: { turns?: readonly SessionTurn[] | null } | null
  turns?: readonly SessionTurn[] | null
  summary?: SessionTimelineSummaryInput | null
  inputs?: readonly SessionInputObservation[] | null
  agentTurns?: readonly AgentTurnObservation[] | null
  activity?: AgentSessionActivity | null
  lastActivityAt?: string | null
  recoveryHistory?: readonly SessionRecoveryObservation[] | null
  liveDetails?: readonly AgentTranscriptDetail[] | null
}

export interface SessionTimelineCurrentActivity {
  state: 'queued' | 'active' | 'idle' | 'unknown'
  label: string
  itemId?: string
  sourceId?: string
}

const TERMINAL_TURN_STATES = new Set(['completed', 'succeeded', 'success', 'done', 'failed', 'cancelled', 'canceled', 'stopped', 'timeout'])
const QUEUED_TURN_STATES = new Set(['queued', 'pending', 'waiting'])
const ACTIVE_TURN_STATES = new Set(['executing', 'running', 'active', 'in_progress'])

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function stringValue(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value : undefined
}

function numberValue(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function dateValue(value: unknown, fallback: string): string {
  return stringValue(value) ?? fallback
}

function timestampOrder(occurredAt: string, sequence: number | undefined, offset: number): number {
  if (sequence !== undefined) return sequence * 1_000_000 + offset
  const timestamp = Date.parse(occurredAt)
  return Number.isNaN(timestamp) ? 9_000_000_000_000_000 + offset : timestamp + offset
}

function rawSequence(value: unknown): number | undefined {
  return numberValue(record(value)?.sequence)
}

function fact(
  sourceId: string,
  source: TimelineFact['source'],
  kind: TimelineFactKind,
  occurredAt: string,
  order: number,
  raw: unknown,
  fields: Partial<TimelineFact> = {},
): TimelineFact {
  return { sourceId, source, kind, occurredAt, order, raw, ...fields }
}

function normalizeStatus(value: unknown): string {
  return stringValue(value)?.toLowerCase() ?? 'unknown'
}

function normalizeToolStatus(value: unknown): TimelineToolStatus | undefined {
  const status = normalizeStatus(value)
  if (status === 'pending' || status === 'queued') return 'pending'
  if (status === 'running' || status === 'started' || status === 'in_progress') return 'running'
  if (status === 'completed' || status === 'succeeded' || status === 'success' || status === 'done') return 'completed'
  if (status === 'failed' || status === 'error') return 'failed'
  if (status === 'cancelled' || status === 'canceled') return 'cancelled'
  if (status === 'timeout' || status === 'timed_out') return 'timeout'
  return undefined
}

function fileChanges(value: unknown): TimelineFileChange[] | undefined {
  if (!Array.isArray(value)) return undefined
  const changes = value.flatMap((entry): TimelineFileChange[] => {
    const item = record(entry)
    const path = stringValue(item?.path)
    if (!path) return []
    const operation = item?.operation
    return [{
      path,
      operation: operation === 'created' || operation === 'modified' || operation === 'deleted' || operation === 'moved'
        ? operation
        : undefined,
      additions: numberValue(item?.additions),
      deletions: numberValue(item?.deletions),
      oldPath: stringValue(item?.oldPath),
    }]
  })
  return changes.length > 0 ? changes : undefined
}

function convertFileChanges(value: FileChangeSummary[] | undefined): TimelineFileChange[] | undefined {
  return fileChanges(value)
}

function sourceIdForLive(detail: Record<string, unknown>, eventName: string, fallback: number): string {
  const explicit = stringValue(detail.sourceId) ?? stringValue(detail.eventId)
  if (explicit) return explicit
  const sequence = numberValue(detail.sequence)
  return sequence === undefined ? `live:${eventName}:${fallback}` : `live:${eventName}:${sequence}`
}

function liveOccurredAt(detail: Record<string, unknown>, fallback: string): string {
  return dateValue(detail.createdAt, dateValue(detail.recordedAt, dateValue(detail.observedAt, fallback)))
}

function liveTool(detail: Record<string, unknown>, sourceId: string): TimelineFact['tool'] {
  const nestedTool = record(detail.toolCall)
  const input = detail.rawInput ?? detail.input ?? nestedTool?.input ?? nestedTool?.rawInput
  const output = detail.rawOutput ?? detail.output ?? nestedTool?.output ?? nestedTool?.rawOutput
  const name = stringValue(detail.toolName) ?? stringValue(nestedTool?.toolName) ?? stringValue(nestedTool?.name) ?? 'tool'
  const callId = stringValue(detail.toolCallId)
    ?? stringValue(nestedTool?.toolCallId)
    ?? stringValue(detail.executionId)
    ?? sourceId
  const exitCode = numberValue(detail.exitCode) ?? numberValue(record(detail.result)?.exitCode)
  return {
    callId,
    name,
    normalizedName: stringValue(detail.normalizedName) ?? stringValue(nestedTool?.normalizedName),
    title: stringValue(detail.title) ?? stringValue(detail.displayTitle) ?? stringValue(nestedTool?.title),
    target: stringValue(detail.target) ?? stringValue(detail.displaySubtitle),
    command: stringValue(detail.command),
    input,
    output,
    status: normalizeToolStatus(detail.state ?? detail.status ?? nestedTool?.state ?? nestedTool?.status),
    exitCode,
    changedFiles: fileChanges(detail.changedFiles ?? nestedTool?.changedFiles),
  }
}

function liveFact(detail: AgentTranscriptDetail, fallback: string, index: number): TimelineFact {
  const original = record(detail) ?? {}
  const eventName = stringValue(original.type) ?? 'unknown'
  const source = sanitizePublicAgentEvent(eventName, original)
  const sourceId = sourceIdForLive(source, eventName, index)
  const occurredAt = liveOccurredAt(source, fallback)
  const sequence = numberValue(source.sequence)
  const order = timestampOrder(occurredAt, sequence, index)
  const text = stringValue(source.text)
  const turnId = stringValue(source.turnId)
  const inputId = stringValue(source.inputId)

  if (eventName === 'session.input') {
    return fact(sourceId, 'live', 'input', occurredAt, order, source, {
      text,
      input: {
        text: text ?? '消息',
        acceptance: stringValue(source.acceptance) ?? 'unknown',
        turnId,
      },
      correlationId: inputId,
    })
  }
  if (eventName === 'message.delta' || eventName === 'coder_text_chunk') {
    return fact(sourceId, 'live', 'message', occurredAt, order, source, {
      text,
      correlationId: stringValue(source.messageId) ?? turnId ?? stringValue(source.executionId),
    })
  }
  if (eventName === 'reasoning.delta' || eventName === 'coder_thought_chunk') {
    return fact(sourceId, 'live', 'reasoning', occurredAt, order, source, {
      text,
      correlationId: stringValue(source.messageId) ?? turnId ?? stringValue(source.executionId),
    })
  }
  if (eventName === 'tool_call.started' || eventName === 'tool_call.updated' || eventName === 'tool_call.completed' || eventName === 'coder_tool_call') {
    const tool = liveTool(source, sourceId)
    return fact(sourceId, 'live', 'tool', occurredAt, order, source, {
      tool,
      correlationId: stringValue(source.executionId) ?? turnId,
      groupKey: stringValue(source.groupKey),
    })
  }
  if (eventName === 'compaction' || eventName === 'compaction_event' || eventName === 'com.mohist.agent-session.context-compacted' || eventName === 'session.context_compacted' || eventName === 'context_reset' || eventName === 'session.context_reset') {
    const kind = eventName.includes('reset') ? 'reset' : 'compaction'
    return fact(sourceId, 'live', 'boundary', occurredAt, order, source, {
      boundary: {
        kind,
        reason: stringValue(source.reason),
        summary: stringValue(source.summary),
      },
    })
  }
  if (eventName === 'coder_recovery_status') {
    const status = stringValue(source.status) ?? 'unknown'
    const label = stringValue(source.reason) ?? `恢复：${status}`
    return fact(sourceId, 'live', 'status', occurredAt, order, source, {
      text: label,
      status: { label, state: status, turnId },
    })
  }
  if (eventName === 'session.activity') {
    const activity = stringValue(source.activity) ?? 'unknown'
    const label = activity === 'active' ? '执行中' : activity === 'idle' ? '空闲' : '状态未知'
    return fact(sourceId, 'live', 'status', occurredAt, order, source, {
      text: label,
      status: { label, state: activity, turnId },
    })
  }
  if (eventName === 'provider.retry' || eventName === 'session.liveness' || eventName === 'usage.updated' || eventName === 'model.resolved' || eventName === 'context_health_update' || eventName === 'com.mohist.agent-session.context-health-updated') {
    const label = stringValue(source.message)
      ?? stringValue(source.failureReason)
      ?? stringValue(source.resolvedModel)
      ?? stringValue(source.healthStatus)
      ?? eventName
    return fact(sourceId, 'live', 'status', occurredAt, order, source, {
      text: label,
      status: { label, state: eventName, turnId },
    })
  }
  return fact(sourceId, 'live', 'suppressed', occurredAt, order, source, {
    text: eventName,
  })
}

function partFact(part: SessionPart, turn: SessionTurn, turnSequence: number | undefined, partIndex: number): TimelineFact {
  const source = record(part) ?? {}
  const occurredAt = dateValue(source.startedAt, dateValue(source.at, turn.startedAt))
  const order = timestampOrder(occurredAt, turnSequence, partIndex + 1)
  const sourceId = `part:${stringValue(source.id) ?? `${turn.id}:${partIndex}`}`

  if (source.type === 'text') {
    return fact(sourceId, 'transcript', 'message', occurredAt, order, part, {
      text: stringValue(source.text),
      correlationId: `${turn.id}:text`,
    })
  }
  if (source.type === 'reasoning') {
    return fact(sourceId, 'transcript', 'reasoning', occurredAt, order, part, {
      text: stringValue(source.text),
      correlationId: `${turn.id}:reasoning`,
    })
  }
  if (source.type === 'unknown') {
    return fact(sourceId, 'transcript', 'unknown', occurredAt, order, part, {
      text: stringValue(source.text) ?? '未知运行事件',
      correlationId: stringValue(source.id),
    })
  }
  if (source.type === 'error') {
    const errorKind = stringValue(source.kind)
    if (errorKind === 'context-reset' || errorKind === 'compaction') {
      return fact(sourceId, 'transcript', 'boundary', occurredAt, order, part, {
        boundary: { kind: errorKind === 'context-reset' ? 'reset' : 'compaction', reason: stringValue(source.message) },
      })
    }
    return fact(sourceId, 'transcript', 'error', occurredAt, order, part, {
      text: stringValue(source.message),
      error: { message: stringValue(source.message), kind: errorKind },
    })
  }
  if (source.type === 'tool') {
    const tool = record(source.tool) ?? {}
    const changedFiles = convertFileChanges(tool.changedFiles as FileChangeSummary[] | undefined)
    const status = normalizeToolStatus(tool.status)
    return fact(sourceId, 'transcript', source.hidden === true ? 'suppressed' : 'tool', occurredAt, order, part, {
      tool: {
        callId: stringValue(tool.toolCallId) ?? sourceId,
        name: stringValue(tool.toolName) ?? 'tool',
        normalizedName: stringValue(tool.normalizedName),
        title: stringValue(tool.title) ?? stringValue(tool.displayTitle),
        target: stringValue(tool.target),
        input: tool.rawInput ?? tool.input,
        output: tool.rawOutput ?? tool.output,
        status,
        changedFiles,
      },
      groupKey: stringValue(tool.category) ?? stringValue(tool.normalizedName),
      error: stringValue(tool.error) ? { message: stringValue(tool.error) } : undefined,
    })
  }
  return fact(sourceId, 'transcript', 'suppressed', occurredAt, order, part, { text: '已省略的活动' })
}

function turnInputFacts(
  turns: readonly SessionTurn[],
  inputs: readonly SessionInputObservation[],
  agentTurns: readonly AgentTurnObservation[],
  fallback: string,
): TimelineFact[] {
  const turnsById = new Map(turns.map(turn => [turn.id, turn]))
  const observationsByInput = new Map<string, AgentTurnObservation>()
  for (const turn of agentTurns) {
    for (const inputId of turn.inputIds) observationsByInput.set(inputId, turn)
  }
  return inputs.map((input) => {
    const observation = observationsByInput.get(input.id)
    const transcriptTurn = observation ? turnsById.get(observation.id) : undefined
    const relatedInputIndex = observation?.inputIds.indexOf(input.id) ?? 0
    const text = transcriptTurn && relatedInputIndex === 0 ? transcriptTurn.user.text : '消息'
    const occurredAt = transcriptTurn?.user.sentAt ?? fallback
    return fact(`input:${input.id}`, 'input', 'input', occurredAt, timestampOrder(occurredAt, input.sequence, 0), input, {
      text,
      input: { text, acceptance: observation ? input.acceptance : 'unknown', turnId: observation?.id },
      correlationId: observation?.id,
    })
  })
}

function unmatchedTurnInputFacts(
  turns: readonly SessionTurn[],
  agentTurns: readonly AgentTurnObservation[],
  inputIds: Set<string>,
  fallback: string,
): TimelineFact[] {
  const turnsById = new Map(agentTurns.map(turn => [turn.id, turn]))
  return turns.flatMap((turn, index) => {
    const observation = turnsById.get(turn.id)
    const hasMatchedInput = observation?.inputIds.some(inputId => inputIds.has(inputId)) ?? false
    if (hasMatchedInput) return []
    const occurredAt = turn.user.sentAt || turn.startedAt || fallback
    return [fact(`turn:${turn.id}:input`, 'transcript', 'input', occurredAt, timestampOrder(occurredAt, observation?.sequence, index), turn, {
      text: turn.user.text,
      input: { text: turn.user.text, acceptance: 'unknown', turnId: observation?.id },
      correlationId: observation?.id,
    })]
  })
}

function turnStateFacts(
  agentTurns: readonly AgentTurnObservation[],
  turns: readonly SessionTurn[],
  fallback: string,
): TimelineFact[] {
  const turnsById = new Map(turns.map(turn => [turn.id, turn]))
  return agentTurns.flatMap((turn, index) => {
    const state = normalizeStatus(turn.status)
    const transcriptTurn = turnsById.get(turn.id)
    const occurredAt = transcriptTurn?.completedAt ?? transcriptTurn?.startedAt ?? fallback
    const order = timestampOrder(occurredAt, turn.sequence, index)
    if (QUEUED_TURN_STATES.has(state)) {
      return [fact(`turn:${turn.id}:state`, 'turn', 'status', occurredAt, order, turn, {
        text: '排队中',
        status: { label: '排队中', state: 'queued', turnId: turn.id },
      })]
    }
    if (ACTIVE_TURN_STATES.has(state)) {
      return [fact(`turn:${turn.id}:state`, 'turn', 'status', occurredAt, order, turn, {
        text: '执行中',
        status: { label: '执行中', state: 'executing', turnId: turn.id },
      })]
    }
    const result = turn.result
    const resultText = stringValue(result?.message) ?? stringValue(result?.output) ?? stringValue(result?.failureReason)
    if (state === 'failed' || state === 'cancelled' || state === 'canceled' || state === 'stopped' || state === 'timeout') {
      const label = resultText ?? (state === 'failed' ? '执行失败' : state === 'timeout' ? '执行超时' : '执行已取消')
      return [fact(`turn:${turn.id}:result`, 'turn', 'error', occurredAt, order, turn, {
        text: label,
        error: { message: label, kind: state },
        status: { label, state, turnId: turn.id },
      })]
    }
    if (TERMINAL_TURN_STATES.has(state)) {
      const label = resultText ? `已完成：${resultText}` : '已完成'
      return [fact(`turn:${turn.id}:result`, 'turn', 'status', occurredAt, order, turn, {
        text: label,
        status: { label, state: 'completed', turnId: turn.id },
      })]
    }
    return [fact(`turn:${turn.id}:state`, 'turn', 'status', occurredAt, order, turn, {
      text: turn.status,
      status: { label: turn.status, state, turnId: turn.id },
    })]
  })
}

function recoveryFacts(history: readonly SessionRecoveryObservation[], fallback: string): TimelineFact[] {
  return history.map((entry, index) => {
    const occurredAt = entry.recordedAt || fallback
    const kind = entry.type === 'reset' ? 'reset' : 'compaction'
    return fact(`recovery:${entry.type}:${entry.recordedAt}:${index}`, 'recovery', 'boundary', occurredAt, timestampOrder(occurredAt, undefined, index), entry, {
      boundary: { kind, reason: entry.reason ?? entry.strategy ?? undefined, summary: entry.summary ?? undefined },
    })
  })
}

function summaryActivityFact(activity: AgentSessionActivity | null | undefined, occurredAt: string): TimelineFact | null {
  if (!activity) return null
  const label = activity === 'active' ? '执行中' : activity === 'idle' ? '空闲' : '状态未知'
  return fact('summary:activity', 'summary', 'status', occurredAt, timestampOrder(occurredAt, undefined, 0), { activity }, {
    text: label,
    status: { label, state: activity },
  })
}

function compareFacts(left: TimelineFact, right: TimelineFact): number {
  const leftSequence = rawSequence(left.raw)
  const rightSequence = rawSequence(right.raw)
  if (leftSequence !== undefined && rightSequence !== undefined && leftSequence !== rightSequence) {
    return leftSequence - rightSequence
  }
  const leftTime = Date.parse(left.occurredAt)
  const rightTime = Date.parse(right.occurredAt)
  if (!Number.isNaN(leftTime) && !Number.isNaN(rightTime) && leftTime !== rightTime) return leftTime - rightTime
  return left.order - right.order || left.sourceId.localeCompare(right.sourceId)
}

function dedupeFacts(facts: TimelineFact[]): TimelineFact[] {
  const bySourceId = new Map<string, TimelineFact>()
  for (const item of facts) {
    const previous = bySourceId.get(item.sourceId)
    if (!previous || item.source === 'live') bySourceId.set(item.sourceId, item)
  }
  return [...bySourceId.values()].sort(compareFacts)
}

export function buildTimelineFacts(input: SessionTimelineFactInput): TimelineFact[] {
  const turns = input.turns ?? input.transcript?.turns ?? []
  const summary = input.summary
  const inputs = input.inputs ?? summary?.inputs ?? []
  const agentTurns = input.agentTurns ?? summary?.turns ?? []
  const recoveryHistory = input.recoveryHistory ?? summary?.recoveryHistory ?? []
  const fallback = input.lastActivityAt ?? summary?.lastActivityAt ?? turns.at(-1)?.completedAt ?? turns.at(-1)?.startedAt ?? '1970-01-01T00:00:00.000Z'
  const facts: TimelineFact[] = []

  const inputFacts = turnInputFacts(turns, inputs, agentTurns, fallback)
  facts.push(...inputFacts)
  facts.push(...unmatchedTurnInputFacts(turns, agentTurns, new Set(inputs.map(inputEntry => inputEntry.id)), fallback))
  for (const [turnIndex, turn] of turns.entries()) {
    const observation = agentTurns.find(candidate => candidate.id === turn.id)
    facts.push(...turn.assistant.map((part, partIndex) => partFact(part, turn, observation?.sequence, turnIndex * 100 + partIndex)))
  }
  facts.push(...turnStateFacts(agentTurns, turns, fallback))
  facts.push(...recoveryFacts(recoveryHistory, fallback))
  const activityFact = summaryActivityFact(input.activity ?? summary?.activity, fallback)
  if (activityFact) facts.push(activityFact)
  const knownInputIds = new Set(inputs.map((inputEntry) => inputEntry.id))
  for (const [index, detail] of (input.liveDetails ?? []).entries()) {
    const live = record(detail)
    if (stringValue(live?.type) === 'session.input' && knownInputIds.has(stringValue(live?.inputId) ?? '')) continue
    facts.push(liveFact(detail, fallback, index))
  }
  return dedupeFacts(facts)
}

function stateForTurn(status: string | undefined): 'queued' | 'active' | undefined {
  const state = normalizeStatus(status)
  if (QUEUED_TURN_STATES.has(state)) return 'queued'
  if (ACTIVE_TURN_STATES.has(state)) return 'active'
  return undefined
}

function factIsUnfinished(item: TimelineFact): boolean {
  if (item.source === 'live' && (item.kind === 'message' || item.kind === 'reasoning')) return true
  if (item.kind === 'message' || item.kind === 'reasoning') {
    return record(item.raw)?.completedAt == null
  }
  if (item.kind !== 'tool') return false
  const status = item.tool?.status
  return status === undefined || status === 'pending' || status === 'running'
}

export function deriveCurrentActivity(
  facts: readonly TimelineFact[],
  items: readonly { id: string; sourceIds: string[]; summary: string; isTerminal: boolean; renderClass: string }[],
  input: SessionTimelineFactInput,
): SessionTimelineCurrentActivity {
  const summary = input.summary
  const agentTurns = input.agentTurns ?? summary?.turns ?? []
  const currentTurnId = summary?.currentTurnId
  const turn = currentTurnId
    ? agentTurns.find(candidate => candidate.id === currentTurnId)
    : [...agentTurns].reverse().find(candidate => stateForTurn(candidate.status) !== undefined)
  const turnState = stateForTurn(turn?.status)
  if (turnState === 'queued') return { state: 'queued', label: '排队中', sourceId: `turn:${turn?.id}:state` }

  const activityFacts = facts.filter(factItem => factItem.kind === 'status' && (record(factItem.raw)?.activity === 'active' || record(factItem.raw)?.activity === 'idle' || record(factItem.raw)?.activity === 'unknown'))
  const latestActivity = activityFacts.at(-1)
  const rawActivity = stringValue(record(latestActivity?.raw)?.activity) ?? input.activity ?? summary?.activity ?? 'unknown'
  if (rawActivity === 'idle') return { state: 'idle', label: '空闲', sourceId: latestActivity?.sourceId }
  if (rawActivity === 'unknown') return { state: 'unknown', label: '状态未知', sourceId: latestActivity?.sourceId }

  const factsBySourceId = new Map(facts.map(factItem => [factItem.sourceId, factItem]))
  const activeItem = [...items].reverse().find(item => {
    if (item.isTerminal || item.renderClass === 'status' || item.renderClass === 'suppressed') return false
    return item.sourceIds.some(sourceId => {
      const sourceFact = factsBySourceId.get(sourceId)
      return sourceFact !== undefined && factIsUnfinished(sourceFact)
    })
  })
  if (activeItem) return { state: 'active', label: activeItem.summary, itemId: activeItem.id, sourceId: activeItem.sourceIds[0] }
  if (turnState === 'active' || rawActivity === 'active') return { state: 'active', label: '执行中', sourceId: turn ? `turn:${turn.id}:state` : latestActivity?.sourceId }
  return { state: 'unknown', label: '状态未知', sourceId: latestActivity?.sourceId }
}
