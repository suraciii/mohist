import type { SessionTurn, TextPart, ReasoningPart, ToolPart, ErrorPart, PromptKind } from '../../../entities/coder-session'
import { parseEditInput, parsePatchOperations, parseJsonSafely } from './transcript-tool-utils'
import {
  normalizeToolName,
  inferDisplayTitle,
  stringifyPayload,
  getFilePathFromInput,
  getToolLabel,
} from './transcript-tool-utils'

export interface LiveToolCall {
  toolCallId: string
  toolName: string
  normalizedName?: string
  displayTitle?: string
  displaySubtitle?: string
  category?: string
  status: 'started' | 'completed' | 'failed' | 'timeout' | 'cancelled'
  title?: string
  target?: string
  input?: string
  output?: string
  error?: string
  startedAt: string
  completedAt?: string | null
  rawInput?: unknown
  rawOutput?: unknown
  metadata?: Record<string, unknown>
  details?: Record<string, unknown>
}

function generateId(): string {
  return Math.random().toString(36).slice(2, 11)
}

export function createTextPart(text: string, startedAt: string): TextPart {
  return { id: generateId(), type: 'text', text, startedAt, completedAt: null }
}

export function createReasoningPart(text: string, startedAt: string): ReasoningPart {
  return { id: generateId(), type: 'reasoning', text, startedAt, completedAt: null }
}

export function createErrorPart(message: string, kind: ErrorPart['kind'], at: string): ErrorPart {
  return { id: generateId(), type: 'error', message, kind, at }
}

export function normalizePromptKind(kind?: string): PromptKind {
  switch (kind) {
    case 'initial':
    case 'task':
    case 'retry':
    case 'followup':
    case 'recovery':
      return kind
    default:
      return 'legacy-missing'
  }
}

export function createToolPart(tool: LiveToolCall): ToolPart {
  const normalizedName = tool.normalizedName ?? normalizeToolName(tool.toolName, tool.title, tool.rawInput, tool.rawOutput)
  const { displayTitle, displaySubtitle } = tool.displayTitle || tool.displaySubtitle
    ? { displayTitle: tool.displayTitle, displaySubtitle: tool.displaySubtitle }
    : inferDisplayTitle(normalizedName, tool.title)
  const input = tool.input ?? stringifyPayload(tool.rawInput)
  const output = tool.output ?? stringifyPayload(tool.rawOutput)
  const parsedEdit = parseEditInput(input)
  const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : undefined
  return {
    id: generateId(),
    type: 'tool',
    tool: {
      toolCallId: tool.toolCallId,
      normalizedName,
      toolName: tool.toolName,
      displayTitle,
      displaySubtitle,
      category: tool.category,
      status: mapStatusToDisplay(tool.status),
      title: tool.title,
      target: tool.target,
      input,
      output,
      error: tool.error,
      startedAt: tool.startedAt,
      completedAt: tool.completedAt,
      rawInput: input,
      rawOutput: output,
      metadata: tool.metadata,
      details: tool.details,
      changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
    },
  }
}

export function mapStatusToDisplay(status: string): ToolPart['tool']['status'] {
  switch (status) {
    case 'started':
      return 'running'
    case 'completed':
      return 'completed'
    case 'failed':
      return 'failed'
    case 'timeout':
      return 'failed'
    case 'cancelled':
      return 'cancelled'
    default:
      return 'pending'
  }
}

export function isTerminalState(state: string): boolean {
  return state === 'completed' || state === 'failed' || state === 'timeout' || state === 'cancelled'
}

export function createTemporaryTurn(at: string): SessionTurn {
  return {
    id: `live-${generateId()}`,
    startedAt: at,
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: 'Prompt is loading for this live session',
      kind: 'legacy-missing',
      sentAt: at,
    },
    assistant: [],
  }
}

export function createInputTurn(detail: {
  text: string
  kind?: string
  sentAt?: string
}): SessionTurn {
  const sentAt = detail.sentAt ?? new Date().toISOString()
  return {
    id: `live-${generateId()}`,
    startedAt: sentAt,
    completedAt: null,
    incomplete: true,
    user: {
      role: 'mohist',
      text: detail.text,
      kind: normalizePromptKind(detail.kind),
      sentAt,
    },
    assistant: [],
  }
}

export function ensureLiveTurn(turns: SessionTurn[], at: string): SessionTurn[] {
  return turns.length > 0 ? [...turns] : [createTemporaryTurn(at)]
}

export function appendInputTurn(turns: SessionTurn[], detail: { text: string; kind?: string; sentAt?: string }): SessionTurn[] {
  const next = [...turns]
  const sentAt = detail.sentAt ?? new Date().toISOString()
  const lastTurn = next[next.length - 1]
  if (
    lastTurn
    && lastTurn.user.text === detail.text
    && lastTurn.assistant.length === 0
    && lastTurn.completedAt === null
  ) {
    next[next.length - 1] = {
      ...lastTurn,
      startedAt: lastTurn.startedAt ?? sentAt,
      user: {
        ...lastTurn.user,
        kind: detail.kind ? normalizePromptKind(detail.kind) : lastTurn.user.kind,
        sentAt,
      },
    }
    return next
  }
  if (lastTurn && lastTurn.completedAt === null && lastTurn.assistant.length > 0) {
    next[next.length - 1] = {
      ...lastTurn,
      completedAt: sentAt,
      incomplete: false,
    }
  }
  next.push(createInputTurn({ ...detail, sentAt }))
  return next
}

export function closeLatestTurn(turns: SessionTurn[], completedAt: string): SessionTurn[] {
  const next = ensureLiveTurn(turns, completedAt)
  const lastTurn = next[next.length - 1]
  const closedAssistant = lastTurn.assistant.map((part) => {
    if (part.type === 'text' && part.completedAt === null) {
      return { ...part, completedAt }
    }
    if (part.type === 'reasoning' && part.completedAt === null) {
      return { ...part, completedAt }
    }
    return part
  })
  next[next.length - 1] = {
    ...lastTurn,
    assistant: closedAssistant,
    completedAt,
    incomplete: false,
  }
  return next
}

export function appendTextToTurn(turn: SessionTurn, text: string): SessionTurn {
  const now = new Date().toISOString()
  const existingTextIndex = turn.assistant.findIndex((p): p is TextPart => p.type === 'text' && p.completedAt === null)

  if (existingTextIndex >= 0) {
    const existing = turn.assistant[existingTextIndex] as TextPart
    const updated: TextPart = { ...existing, text: existing.text + text }
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) => (i === existingTextIndex ? updated : p)),
    }
  }

  return {
    ...turn,
    assistant: [...turn.assistant, createTextPart(text, now)],
  }
}

export function closeActiveTextPart(turn: SessionTurn, completedAt: string): SessionTurn {
  const textIndex = turn.assistant.findIndex((p): p is TextPart => p.type === 'text' && p.completedAt === null)
  if (textIndex >= 0) {
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) =>
        i === textIndex ? { ...(p as TextPart), completedAt } : p,
      ),
    }
  }
  return turn
}

export function appendReasoningToTurn(turn: SessionTurn, text: string): SessionTurn {
  const now = new Date().toISOString()
  const existingReasoningIndex = turn.assistant.findIndex(
    (p): p is ReasoningPart => p.type === 'reasoning' && p.completedAt === null,
  )

  if (existingReasoningIndex >= 0) {
    const existing = turn.assistant[existingReasoningIndex] as ReasoningPart
    const updated: ReasoningPart = { ...existing, text: existing.text + text }
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) => (i === existingReasoningIndex ? updated : p)),
    }
  }

  return {
    ...turn,
    assistant: [...turn.assistant, createReasoningPart(text, now)],
  }
}

export function findToolByCorrelation(
  turn: SessionTurn,
  normalizedName: string,
  target?: string,
  toolCallId?: string,
): number {
  return turn.assistant.findIndex((p): p is ToolPart => {
    if (p.type !== 'tool') return false
    if (toolCallId && p.tool.toolCallId && p.tool.toolCallId !== toolCallId) return false
    const toolNormalized = normalizeToolName(p.tool.toolName, p.tool.title, p.tool.rawInput, p.tool.rawOutput)
    if (toolNormalized !== normalizedName) return false
    if (isTerminalState(p.tool.status)) return false
    if (!target) return true
    const toolTarget = p.tool.target ?? p.tool.title
    if (toolTarget !== undefined && target !== toolTarget) return false
    return true
  })
}

export function deriveToolTarget(toolName: string, rawInput: unknown, title?: string): string | undefined {
  const input = stringifyPayload(rawInput)
  const path = getFilePathFromInput(input)
  if (path) return path
  const label = getToolLabel(normalizeToolName(toolName, title, rawInput), input)
  return label ?? title
}

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

export function asPayloadRecord(value: unknown): Record<string, unknown> | null {
  if (typeof value === 'string') return parseJsonSafely(value)
  return asRecord(value)
}

export function getNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export function getString(value: unknown): string | undefined {
  return typeof value === 'string' && value ? value : undefined
}

export function truncatePreview(value: string, maxLength: number = 1000): string {
  return value.length > maxLength ? `${value.slice(0, maxLength)}...` : value
}

export function buildLiveToolDetails(
  normalizedName: string,
  rawInput: unknown,
  rawOutput: unknown,
  metadata?: Record<string, unknown>,
  error?: string,
): Record<string, unknown> | undefined {
  const input = asPayloadRecord(rawInput)
  const output = asPayloadRecord(rawOutput)
  const lower = normalizedName.toLowerCase()

  if (lower === 'bash' || lower === 'shell' || lower === 'exec' || lower === 'command') {
    const details: Record<string, unknown> = { family: 'execution' }
    const command = getString(input?.command ?? input?.script ?? input?.cmd)
    const cwd = getString(input?.cwd ?? input?.workdir ?? input?.workingDir)
    const timeout = getNumber(input?.timeout)
    const exitCode = getNumber(output?.exitCode ?? output?.exit_code ?? output?.code)
    const outputPreview = getString(output?.stdout ?? output?.output)
    if (command) details.command = command
    if (cwd) details.cwd = cwd
    if (timeout !== undefined) details.timeout = timeout
    if (exitCode !== undefined) details.exitCode = exitCode
    if (outputPreview) details.outputPreview = truncatePreview(outputPreview)
    else if (typeof rawOutput === 'string' && rawOutput) details.outputPreview = truncatePreview(rawOutput)
    if (error) details.completionStatus = 'failed'
    else if (rawOutput !== undefined) details.completionStatus = 'completed'
    return details
  }

  if (lower === 'task') {
    const details: Record<string, unknown> = { family: 'delegation' }
    const description = getString(input?.description ?? input?.prompt ?? input?.task ?? input?.command ?? metadata?.description)
    const subagentType = getString(input?.subagent_type ?? input?.agentType ?? input?.type ?? metadata?.subagentType)
    const subagentName = getString(input?.subagent_name ?? input?.agentName ?? input?.name)
    const taskId = getString(input?.task_id ?? input?.taskId)
    const childSessionId = getString(metadata?.childSessionId ?? metadata?.sessionId ?? metadata?.child_session_id)
    if (description) details.description = description
    if (subagentType) details.subagentType = subagentType
    if (subagentName) details.subagentName = subagentName
    if (taskId) details.taskId = taskId
    if (childSessionId) details.childSessionId = childSessionId
    return details
  }

  if (lower === 'skill') {
    const details: Record<string, unknown> = { family: 'skill' }
    const title = getString(metadata?.title)
    const skillNameFromTitle = title?.match(/(?:loaded skill:?\s*)(.+)/i)?.[1]?.trim()
    const skillName = skillNameFromTitle
      ?? getString(input?.name ?? input?.skillName ?? input?.skill)
      ?? getString(metadata?.skillName ?? metadata?.name)
    if (skillName) details.skillName = skillName
    return details
  }

  if (lower === 'question' || lower === 'webfetch' || lower === 'websearch') {
    const details: Record<string, unknown> = { family: 'interaction' }
    const url = getString(input?.url ?? input?.uri)
    const query = getString(input?.query ?? input?.search_query ?? input?.searchQuery ?? input?.search ?? input?.question ?? input?.text)
    const textPreview = getString(output?.content ?? output?.text ?? output?.summary)
    const answers = Array.isArray(output?.answers) ? output.answers : undefined
    if (url) details.url = url
    if (query) details.query = query
    if (answers) details.answerCount = answers.length
    if (textPreview) details.resultPreview = textPreview.slice(0, 300)
    else if (typeof rawOutput === 'string' && rawOutput) details.resultPreview = rawOutput.slice(0, 300)
    return details
  }

  if (lower === 'todowrite' || lower === 'todo') {
    const todos = Array.isArray(input?.todos) ? input.todos : []
    const details: Record<string, unknown> = {
      family: 'planning',
      totalCount: todos.length,
    }
    const byStatus: Record<string, number> = {}
    for (const todo of todos) {
      const status = asRecord(todo)?.status
      if (typeof status === 'string' && status) {
        byStatus[status] = (byStatus[status] ?? 0) + 1
      }
    }
    if (Object.keys(byStatus).length > 0) details.statusCounts = byStatus
    return details
  }

  return undefined
}

export function getNormalizedName(detail: {
  normalizedName?: string
  toolName: string
  title?: string
  rawInput?: unknown
  rawOutput?: unknown
}): string {
  return detail.normalizedName ?? normalizeToolName(detail.toolName, detail.title, detail.rawInput, detail.rawOutput)
}

export function getDisplayFields(detail: {
  normalizedName?: string
  displayTitle?: string
  displaySubtitle?: string
  toolName: string
  title?: string
  rawInput?: unknown
  rawOutput?: unknown
}): { normalizedName: string; displayTitle?: string; displaySubtitle?: string } {
  const normalizedName = getNormalizedName(detail)
  if (detail.title) {
    return { normalizedName, displayTitle: detail.title }
  }
  if (detail.displayTitle || detail.displaySubtitle) {
    return {
      normalizedName,
      displayTitle: detail.displayTitle,
      displaySubtitle: detail.displaySubtitle,
    }
  }
  return {
    normalizedName,
    ...inferDisplayTitle(normalizedName, detail.title),
  }
}

export function updateToolInTurn(
  turn: SessionTurn,
  toolCallId: string,
  updates: Partial<LiveToolCall>,
  correlationKey?: string,
): SessionTurn {
  const now = new Date().toISOString()
  const existingToolIndex = turn.assistant.findIndex(
    (p): p is ToolPart => p.type === 'tool' && p.tool.toolCallId === toolCallId,
  )

  if (existingToolIndex >= 0) {
    return {
      ...turn,
      assistant: turn.assistant.map((p, i) => {
        if (i !== existingToolIndex) return p
        const toolPart = p as ToolPart
        const rawInput = updates.rawInput ?? updates.input ?? toolPart.tool.rawInput ?? toolPart.tool.input
        const rawOutput = updates.rawOutput ?? updates.output ?? toolPart.tool.rawOutput ?? toolPart.tool.output
        const metadata = updates.metadata ?? toolPart.tool.metadata
        const details = updates.details ?? toolPart.tool.details
        const startedAt = updates.startedAt ?? toolPart.tool.startedAt
        const { normalizedName, displayTitle, displaySubtitle } = getDisplayFields({
          normalizedName: updates.normalizedName ?? toolPart.tool.normalizedName,
          displayTitle: updates.displayTitle ?? toolPart.tool.displayTitle,
          displaySubtitle: updates.displaySubtitle ?? toolPart.tool.displaySubtitle,
          toolName: updates.toolName ?? toolPart.tool.toolName,
          title: updates.title ?? toolPart.tool.title,
          rawInput,
          rawOutput,
        })
        const input = stringifyPayload(rawInput) ?? toolPart.tool.input
        const output = stringifyPayload(rawOutput) ?? toolPart.tool.output
        const parsedEdit = parseEditInput(input)
        const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : toolPart.tool.changedFiles
        const newStatus = mapStatusToDisplay(updates.status ?? toolPart.tool.status)
        const { status: _updatesStatus, ...restUpdates } = updates
        return {
          ...toolPart,
          tool: {
            ...toolPart.tool,
            ...restUpdates,
            normalizedName,
            displayTitle,
            displaySubtitle,
            category: updates.category ?? toolPart.tool.category,
            input,
            output,
            rawInput: input,
            rawOutput: output,
            metadata,
            details,
            target: updates.target ?? toolPart.tool.target,
            changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
            startedAt,
            status: newStatus,
            completedAt: isTerminalState(newStatus) ? now : toolPart.tool.completedAt,
          },
        }
      }),
    }
  }

  if (correlationKey) {
    const [normalizedName, target] = correlationKey.split('|')
    const correlatedIndex = findToolByCorrelation(turn, normalizedName, target)

    if (correlatedIndex >= 0) {
      return {
        ...turn,
        assistant: turn.assistant.map((p, i) => {
          if (i !== correlatedIndex) return p
          const toolPart = p as ToolPart
          const rawInput = updates.rawInput ?? updates.input ?? toolPart.tool.rawInput ?? toolPart.tool.input
          const rawOutput = updates.rawOutput ?? updates.output ?? toolPart.tool.rawOutput ?? toolPart.tool.output
          const metadata = updates.metadata ?? toolPart.tool.metadata
          const details = updates.details ?? toolPart.tool.details
          const startedAt = updates.startedAt ?? toolPart.tool.startedAt
          const { displayTitle, displaySubtitle } = getDisplayFields({
            normalizedName,
            displayTitle: updates.displayTitle ?? toolPart.tool.displayTitle,
            displaySubtitle: updates.displaySubtitle ?? toolPart.tool.displaySubtitle,
            toolName: updates.toolName ?? toolPart.tool.toolName,
            title: updates.title ?? toolPart.tool.title,
            rawInput,
            rawOutput,
          })
          const input = stringifyPayload(rawInput) ?? toolPart.tool.input
          const output = stringifyPayload(rawOutput) ?? toolPart.tool.output
          const parsedEdit = parseEditInput(input)
          const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : toolPart.tool.changedFiles
          const newStatus = mapStatusToDisplay(updates.status ?? toolPart.tool.status)
          const { status: _updatesStatus, ...restUpdates } = updates
          return {
            ...toolPart,
            tool: {
              ...toolPart.tool,
              ...restUpdates,
              toolCallId,
              normalizedName,
              displayTitle,
              displaySubtitle,
              category: updates.category ?? toolPart.tool.category,
              input,
              output,
              rawInput: input,
              rawOutput: output,
              metadata,
              details,
              target: updates.target ?? toolPart.tool.target,
              changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
              startedAt,
              status: newStatus,
              completedAt: isTerminalState(newStatus) ? now : toolPart.tool.completedAt,
            },
          }
        }),
      }
    }
  }

  return {
    ...turn,
    assistant: [
      ...turn.assistant,
      createToolPart({
        toolCallId,
        toolName: updates.toolName ?? 'unknown',
        normalizedName: updates.normalizedName,
        displayTitle: updates.displayTitle,
        displaySubtitle: updates.displaySubtitle,
        category: updates.category,
        status: (updates.status ?? 'started') as LiveToolCall['status'],
        title: updates.title,
        target: updates.target,
        input: updates.input,
        output: updates.output,
        error: updates.error,
        rawInput: updates.rawInput,
        rawOutput: updates.rawOutput,
        metadata: updates.metadata,
        details: updates.details,
        startedAt: now,
        completedAt: isTerminalState(mapStatusToDisplay(updates.status ?? 'started')) ? now : null,
      }),
    ],
  }
}