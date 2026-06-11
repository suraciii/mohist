import { useEffect, useMemo, useRef, useState, useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../../../entities/agent'
import type { SessionTurn, SessionPart, TextPart, ReasoningPart, ToolPart, ErrorPart } from '../../../entities/coder-session'
import type { SessionEvent, SessionChatPart } from '../../../entities/session/model/view'
import { viewSessionEvents } from '../../../entities/session/model/view'
import { parseEditInput, parsePatchOperations, parseJsonSafely } from './transcript-tool-utils'
import {
  normalizeToolName,
  inferDisplayTitle,
  stringifyPayload,
  getCorrelationKey,
  getFilePathFromInput,
  getToolLabel,
} from './transcript-tool-utils'

interface UseSessionTranscriptOptions {
  issueNumber: number
  sessionId: string
  acpSessionId: string
  initialTurns?: SessionTurn[]
  initialEvents?: SessionEvent[]
  sessionQueryKeys?: readonly (readonly unknown[])[]
  isRunning: boolean
}

interface LiveToolCall {
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

export interface UseSessionTranscriptResult {
  turns: SessionTurn[]
  transcriptVersion: number
  isNearBottom: boolean
  setIsNearBottom: (nearBottom: boolean) => void
  scrollToBottom: () => void
  newContentAvailable: boolean
  acknowledgeNewContent: () => void
  isFinalizing: boolean
  isThinking: boolean
  isStreaming: boolean
}

function generateId(): string {
  return Math.random().toString(36).slice(2, 11)
}

function isSessionChatPart(part: unknown): part is SessionChatPart {
  if (!part || typeof part !== 'object') return false
  const candidate = part as { partType?: unknown }
  return candidate.partType === 'text'
    || candidate.partType === 'reasoning'
    || candidate.partType === 'tool'
    || candidate.partType === 'error'
}

function chatPartToAssistantPart(
  part: SessionChatPart,
): SessionPart {
  if (part.partType === 'text') {
    const textPart: TextPart = {
      id: part.id,
      type: 'text',
      text: part.text,
      startedAt: part.startedAt,
      completedAt: part.completedAt,
    }
    return textPart
  }
  if (part.partType === 'reasoning') {
    const reasoningPart: ReasoningPart = {
      id: part.id,
      type: 'reasoning',
      text: part.text,
      startedAt: part.startedAt,
      completedAt: part.completedAt,
    }
    return reasoningPart
  }
  if (part.partType === 'error') {
    const errorPart: ErrorPart = {
      id: part.id,
      type: 'error',
      message: part.message,
      kind: part.kind,
      at: part.at,
    }
    return errorPart
  }
  const toolInput = part.input
  const toolOutput = part.output
  const tool: ToolPart['tool'] = {
    toolCallId: part.toolCallId,
    normalizedName: part.normalizedName,
    toolName: part.toolName,
    status: part.status,
    title: part.title,
    input: toolInput,
    output: toolOutput,
    error: part.error,
    startedAt: part.startedAt,
    completedAt: part.completedAt,
    rawInput: toolInput,
    rawOutput: toolOutput,
  }
  const toolPart: ToolPart = { id: part.id, type: 'tool', tool }
  return toolPart
}

function projectHistoricalEvents(events: SessionEvent[]): SessionTurn[] {
  if (events.length === 0) return []
  const chat = viewSessionEvents(events, 'chat')
  return chat.turns.map((turn, index) => {
    const assistant: SessionPart[] = []
    for (const part of turn.parts) {
      if (isSessionChatPart(part)) {
        assistant.push(chatPartToAssistantPart(part))
      }
    }
    const projected: SessionTurn = {
      id: turn.id || `turn-${index}`,
      startedAt: turn.startedAt,
      completedAt: turn.completedAt,
      incomplete: turn.incomplete,
      user: {
        role: 'mohist',
        text: turn.prompt.text,
        kind: turn.prompt.kind,
        sentAt: turn.prompt.sentAt,
      },
      assistant,
    }
    return projected
  })
}

function createTextPart(text: string, startedAt: string): TextPart {
  return { id: generateId(), type: 'text', text, startedAt, completedAt: null }
}

function createReasoningPart(text: string, startedAt: string): ReasoningPart {
  return { id: generateId(), type: 'reasoning', text, startedAt, completedAt: null }
}

function createErrorPart(message: string, kind: ErrorPart['kind'], at: string): ErrorPart {
  return { id: generateId(), type: 'error', message, kind, at }
}

function createToolPart(tool: LiveToolCall): ToolPart {
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

function mapStatusToDisplay(status: string): ToolPart['tool']['status'] {
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

function isTerminalState(state: string): boolean {
  return state === 'completed' || state === 'failed' || state === 'timeout' || state === 'cancelled'
}

function createTemporaryTurn(at: string): SessionTurn {
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

function ensureLiveTurn(turns: SessionTurn[], at: string): SessionTurn[] {
  return turns.length > 0 ? [...turns] : [createTemporaryTurn(at)]
}

function appendTextToTurn(turn: SessionTurn, text: string): SessionTurn {
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

function closeActiveTextPart(turn: SessionTurn, completedAt: string): SessionTurn {
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

function appendReasoningToTurn(turn: SessionTurn, text: string): SessionTurn {
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

function findToolByCorrelation(
  turn: SessionTurn,
  normalizedName: string,
  target?: string,
  toolCallId?: string,
): number {
  return turn.assistant.findIndex((p): p is ToolPart => {
    if (p.type !== 'tool') return false
    if (toolCallId && p.tool.toolCallId === toolCallId) return false
    const toolNormalized = normalizeToolName(p.tool.toolName, p.tool.title, p.tool.rawInput, p.tool.rawOutput)
    if (toolNormalized !== normalizedName) return false
    if (isTerminalState(p.tool.status)) return false
    if (!target) return true
    const toolTarget = p.tool.target ?? p.tool.title
    if (toolTarget !== undefined && target !== toolTarget) return false
    return true
  })
}

function deriveToolTarget(toolName: string, rawInput: unknown, title?: string): string | undefined {
  const input = stringifyPayload(rawInput)
  const path = getFilePathFromInput(input)
  if (path) return path
  const label = getToolLabel(normalizeToolName(toolName, title, rawInput), input)
  return label ?? title
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function asPayloadRecord(value: unknown): Record<string, unknown> | null {
  if (typeof value === 'string') return parseJsonSafely(value)
  return asRecord(value)
}

function getNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function getString(value: unknown): string | undefined {
  return typeof value === 'string' && value ? value : undefined
}

function truncatePreview(value: string, maxLength: number = 1000): string {
  return value.length > maxLength ? `${value.slice(0, maxLength)}...` : value
}

function buildLiveToolDetails(
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

function getNormalizedName(detail: {
  normalizedName?: string
  toolName: string
  title?: string
  rawInput?: unknown
  rawOutput?: unknown
}): string {
  return detail.normalizedName ?? normalizeToolName(detail.toolName, detail.title, detail.rawInput, detail.rawOutput)
}

function getDisplayFields(detail: {
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

function updateToolInTurn(
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

export function useSessionTranscript({
  issueNumber,
  sessionId,
  acpSessionId,
  initialTurns,
  initialEvents,
  sessionQueryKeys,
  isRunning,
}: UseSessionTranscriptOptions): UseSessionTranscriptResult {
  const queryClient = useQueryClient()
  const initialState = useMemo<SessionTurn[]>(() => {
    if (initialEvents && initialEvents.length > 0) {
      return projectHistoricalEvents(initialEvents)
    }
    return initialTurns ?? []
  }, [])
  const [turns, setTurns] = useState<SessionTurn[]>(initialState)
  const [transcriptVersion, setTranscriptVersion] = useState(0)
  const [isNearBottom, setIsNearBottom] = useState(true)
  const [newContentAvailable, setNewContentAvailable] = useState(false)
  const [isFinalizing, setIsFinalizing] = useState(false)
  const [isThinking, setIsThinking] = useState(false)
  const [isStreaming, setIsStreaming] = useState(false)

  const turnsRef = useRef(turns)
  turnsRef.current = turns
  const isNearBottomRef = useRef(isNearBottom)
  isNearBottomRef.current = isNearBottom

  const liveToolCallMapRef = useRef<Map<string, LiveToolCall>>(new Map())
  const pendingCorrelationRef = useRef<Map<string, { normalizedName: string; target?: string; turnIndex: number }>>(new Map())
  const mountedRef = useRef(true)
  const streamingTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const isStreamingRef = useRef(false)
  const hasLiveTailRef = useRef(false)

  const issueId = String(issueNumber)

  useEffect(() => {
    hasLiveTailRef.current = false
  }, [issueId, sessionId, acpSessionId])

  const scrollToBottom = useCallback(() => {
    setIsNearBottom(true)
    setNewContentAvailable(false)
  }, [])

  const clearStreaming = useCallback(() => {
    isStreamingRef.current = false
    setIsStreaming(false)
  }, [])

  const bumpTranscriptVersion = useCallback(() => {
    setTranscriptVersion((version) => version + 1)
    if (streamingTimerRef.current !== null) {
      clearTimeout(streamingTimerRef.current)
    }
    isStreamingRef.current = true
    setIsStreaming(true)
    streamingTimerRef.current = setTimeout(() => {
      clearStreaming()
      streamingTimerRef.current = null
    }, 2000)
  }, [clearStreaming])

  const markNewContent = useCallback(() => {
    bumpTranscriptVersion()
    if (!isNearBottomRef.current) {
      setNewContentAvailable(true)
    }
  }, [bumpTranscriptVersion])

  const markNewContentRef = useRef(markNewContent)
  markNewContentRef.current = markNewContent

  const acknowledgeNewContent = useCallback(() => {
    setNewContentAvailable(false)
  }, [])

  const invalidateAndRefetch = useCallback(() => {
    setIsFinalizing(true)
    for (const queryKey of sessionQueryKeys ?? []) {
      queryClient.invalidateQueries({ queryKey })
    }
    queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions', sessionId] })
  }, [queryClient, issueNumber, sessionId, sessionQueryKeys])

  useEffect(() => {
    if (hasLiveTailRef.current && isRunning) {
      return
    }
    if (initialEvents && initialEvents.length > 0) {
      setTurns(projectHistoricalEvents(initialEvents))
    } else {
      setTurns(initialTurns ?? [])
    }
    hasLiveTailRef.current = false
    liveToolCallMapRef.current.clear()
    pendingCorrelationRef.current.clear()
    setIsFinalizing(false)
    setIsThinking(false)
    setIsStreaming(false)
    setTranscriptVersion((version) => version + 1)
  }, [initialEvents, initialTurns, isRunning])

  useEffect(() => {
    if (!isRunning) {
      setIsThinking(false)
      return
    }
    const hasVisibleContent = turns.some(t =>
      t.assistant.some(p => p.type === 'text' || p.type === 'reasoning' || p.type === 'tool')
    )
    if (!hasVisibleContent) {
      setIsThinking(true)
    }
  }, [isRunning, turns])

  useEffect(() => {
    if (!isRunning) return

    mountedRef.current = true
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        hasLiveTailRef.current = true
        setIsStreaming(true)
        setTurns((prev) => {
          const next = ensureLiveTurn(prev, new Date().toISOString())
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = appendTextToTurn(lastTurn, detail.text)
          return next
        })
        setIsThinking(false)
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_thought_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        hasLiveTailRef.current = true
        setIsStreaming(true)
        setTurns((prev) => {
          const now = new Date().toISOString()
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          const withClosedText = closeActiveTextPart(lastTurn, now)
          next[next.length - 1] = appendReasoningToTurn(withClosedText, detail.text)
          return next
        })
        setIsThinking(true)
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        hasLiveTailRef.current = true
        const now = new Date().toISOString()
        const toolCallId = detail.toolCallId
        const normalizedName = getNormalizedName(detail)
        const pendingCorrelation = pendingCorrelationRef.current.get(toolCallId)
        const target = deriveToolTarget(detail.toolName, detail.rawInput, detail.title) ?? pendingCorrelation?.target
        const correlationKey = getCorrelationKey(detail.toolName, detail.title, target)
        const metadata = detail.metadata ?? detail.rawOutputMetadata
        const detailsMetadata = asRecord(metadata)
        const liveDetails = detail.details ?? buildLiveToolDetails(normalizedName, detail.rawInput, detail.rawOutput, detailsMetadata ?? undefined)
        const { displayTitle, displaySubtitle } = getDisplayFields(detail)

        if (detail.state === 'started') {
          liveToolCallMapRef.current.set(toolCallId, {
            toolCallId,
            toolName: detail.toolName,
            normalizedName,
            displayTitle,
            displaySubtitle,
            category: detail.category,
            status: 'started',
            title: detail.title,
            target,
            input: stringifyPayload(detail.rawInput),
            output: stringifyPayload(detail.rawOutput),
            error: '',
            rawInput: detail.rawInput,
            rawOutput: detail.rawOutput,
            metadata: detailsMetadata ?? undefined,
            details: liveDetails,
            startedAt: now,
            completedAt: null,
          })

          pendingCorrelationRef.current.set(toolCallId, { normalizedName, target, turnIndex: -1 })

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
              toolName: detail.toolName,
              normalizedName,
              displayTitle,
              displaySubtitle,
              category: detail.category,
              status: 'started',
              title: detail.title,
              target,
              input: stringifyPayload(detail.rawInput),
              output: stringifyPayload(detail.rawOutput),
              rawInput: detail.rawInput,
              rawOutput: detail.rawOutput,
              metadata: detailsMetadata ?? undefined,
              details: liveDetails,
              startedAt: now,
            }, correlationKey)
            return next
          })
          setIsThinking(false)
          markNewContentRef.current()
        } else if (isTerminalState(detail.state)) {
          const existing = liveToolCallMapRef.current.get(toolCallId)
          if (existing) {
            existing.status = detail.state as LiveToolCall['status']
            existing.normalizedName = normalizedName
            existing.displayTitle = displayTitle
            existing.displaySubtitle = displaySubtitle
            existing.category = detail.category ?? existing.category
            existing.input = stringifyPayload(detail.rawInput) ?? existing.input
            existing.output = stringifyPayload(detail.rawOutput)
            existing.rawInput = detail.rawInput ?? existing.rawInput
            existing.rawOutput = detail.rawOutput
            existing.metadata = detailsMetadata ?? existing.metadata
            existing.details = liveDetails ?? existing.details
            existing.completedAt = now
            existing.error = detail.state === 'failed' ? (typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? 'Tool failed')) : existing.error
          }

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            const error = detail.state === 'failed' ? (typeof detail.rawOutput === 'string' ? detail.rawOutput : JSON.stringify(detail.rawOutput ?? 'Tool failed')) : undefined
            next[next.length - 1] = updateToolInTurn(lastTurn, toolCallId, {
              status: mapStatusToDisplay(detail.state) as LiveToolCall['status'],
              toolName: detail.toolName,
              normalizedName,
              displayTitle,
              displaySubtitle,
              category: detail.category,
              title: detail.title,
              target,
              input: stringifyPayload(detail.rawInput),
              output: stringifyPayload(detail.rawOutput),
              rawInput: detail.rawInput,
              rawOutput: detail.rawOutput,
              metadata: detailsMetadata ?? undefined,
              details: liveDetails,
              completedAt: now,
              error,
            }, correlationKey)
            return next
          })
          markNewContentRef.current()

          if (isTerminalState(detail.state)) {
            invalidateAndRefetch()
          }
        }
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.coderSessionId !== sessionId) return

        hasLiveTailRef.current = true
        const now = new Date().toISOString()

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = {
            ...lastTurn,
            completedAt: now,
          }
          return next
        })
        invalidateAndRefetch()
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_failed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.coderSessionId !== sessionId) return

        hasLiveTailRef.current = true
        const now = new Date().toISOString()

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = {
            ...lastTurn,
            completedAt: now,
          }
          return next
        })

        const errorPart = createErrorPart(
          detail.reason ?? 'Session failed',
          'failed',
          now,
        )
        setTurns((prev) => {
          const next = [...prev]
          if (next.length > 0) {
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = {
              ...lastTurn,
              assistant: [...lastTurn.assistant, errorPart],
            }
          }
          return next
        })

        invalidateAndRefetch()
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_cancelled', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.coderSessionId !== sessionId) return

        hasLiveTailRef.current = true
        const now = new Date().toISOString()

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = {
            ...lastTurn,
            completedAt: now,
          }
          return next
        })

        const errorPart = createErrorPart(
          detail.reason ?? 'Session cancelled',
          'cancelled',
          now,
        )
        setTurns((prev) => {
          const next = [...prev]
          if (next.length > 0) {
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = {
              ...lastTurn,
              assistant: [...lastTurn.assistant, errorPart],
            }
          }
          return next
        })

        invalidateAndRefetch()
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_recovery_status', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        hasLiveTailRef.current = true
        const now = new Date().toISOString()
        const errorMessages: Record<string, string> = {
          detected: 'Recovery detected',
          recovering: 'Recovery in progress',
          recovered: 'Recovery succeeded',
          failed: 'Recovery failed',
        }

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, now)
          const lastTurn = next[next.length - 1]
          const newPart = createErrorPart(
            errorMessages[detail.status] ?? detail.status,
            'recovery',
            now,
          )
          next[next.length - 1] = {
            ...lastTurn,
            assistant: [...lastTurn.assistant, newPart],
          }
          return next
        })
        markNewContentRef.current()

        if (detail.status === 'recovered' || detail.status === 'failed') {
          invalidateAndRefetch()
        }
      }),
    )

    unsubs.push(
      onAgentEvent('agent_liveness_status', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        hasLiveTailRef.current = true
        const timestamp = detail.probeSentAt ?? detail.lastDataAt ?? new Date().toISOString()
        const message = detail.status === 'probing'
          ? `Liveness probe sent. Waiting until ${detail.probeDeadlineAt ?? 'deadline unknown'}. Last activity: ${detail.lastActivityType ?? 'unknown'}.`
          : detail.status === 'running'
            ? `Liveness recovered after ${detail.lastActivityType ?? 'session'} activity.`
            : `Liveness failed: ${detail.failureReason ?? 'unknown'}. Last activity: ${detail.lastActivityType ?? 'unknown'}.`

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, timestamp)
          const lastTurn = next[next.length - 1]
          const newPart = createErrorPart(message, 'recovery', timestamp)
          next[next.length - 1] = {
            ...lastTurn,
            assistant: [...lastTurn.assistant, newPart],
          }
          return next
        })
        markNewContentRef.current()

        if (detail.status === 'running' || detail.status === 'failed') {
          invalidateAndRefetch()
        }
      }),
    )

    return () => {
      mountedRef.current = false
      if (streamingTimerRef.current !== null) {
        clearTimeout(streamingTimerRef.current)
        streamingTimerRef.current = null
      }
      for (const unsub of unsubs) unsub()
    }
  }, [issueId, sessionId, acpSessionId, issueNumber, isRunning, queryClient, invalidateAndRefetch])

return {
    turns,
    transcriptVersion,
    isNearBottom,
    setIsNearBottom,
    scrollToBottom,
    newContentAvailable,
    acknowledgeNewContent,
    isFinalizing,
    isThinking,
    isStreaming,
  }
}
