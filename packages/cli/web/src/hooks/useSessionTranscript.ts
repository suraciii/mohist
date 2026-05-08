import { useEffect, useRef, useState, useCallback } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../lib/agent-events'
import type { SessionTurn, TextPart, ToolPart, ErrorPart } from '../lib/types'
import { parseEditInput, parsePatchOperations } from '../components/ToolCallCard'

interface UseSessionTranscriptOptions {
  issueNumber: number
  sessionId: string
  acpSessionId: string
  initialTurns: SessionTurn[]
  isRunning: boolean
}

interface LiveToolCall {
  toolCallId: string
  toolName: string
  status: 'started' | 'completed' | 'failed'
  title?: string
  target?: string
  input?: string
  output?: string
  error?: string
  startedAt: string
  completedAt?: string | null
  rawInput?: unknown
  rawOutput?: unknown
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
}

function generateId(): string {
  return Math.random().toString(36).slice(2, 11)
}

function createTextPart(text: string, startedAt: string): TextPart {
  return { id: generateId(), type: 'text', text, startedAt, completedAt: null }
}

function createErrorPart(message: string, kind: ErrorPart['kind'], at: string): ErrorPart {
  return { id: generateId(), type: 'error', message, kind, at }
}

function stringifyPayload(payload: unknown): string | undefined {
  if (payload === undefined || payload === null) return undefined
  return typeof payload === 'string' ? payload : JSON.stringify(payload)
}

function inferToolName(toolName: string | undefined, title?: string, rawInput?: unknown, rawOutput?: unknown): string {
  const explicit = toolName && toolName !== 'unknown' ? toolName : undefined
  const titleName = title && /^[a-zA-Z_][a-zA-Z0-9_-]*$/.test(title) ? title : undefined
  if (explicit) return explicit
  if (titleName) return titleName

  const input = typeof rawInput === 'string' ? rawInput : rawInput && typeof rawInput === 'object' ? rawInput as Record<string, unknown> : null
  if (input && typeof input === 'object') {
    if (typeof input.toolName === 'string') return input.toolName
    if (typeof input.name === 'string') return input.name
    if (typeof input.patchText === 'string') return 'apply_patch'
    if (typeof input.command === 'string') return 'bash'
    if (input.pattern !== undefined || input.query !== undefined || input.search !== undefined) {
      if (input.file_path !== undefined) return 'grep'
      return 'search'
    }
    if (typeof input.filePath === 'string' || typeof input.file_path === 'string' || typeof input.path === 'string') return 'read'
    if (Array.isArray(input.todos)) return 'todowrite'
  }
  if (typeof input === 'string') {
    try {
      return inferToolName(undefined, undefined, JSON.parse(input), rawOutput)
    } catch {
      if (input.includes('*** Begin Patch') || input.includes('*** Add File:') || input.includes('*** Update File:') || input.includes('*** Delete File:')) return 'apply_patch'
    }
  }

  const output = rawOutput && typeof rawOutput === 'object' ? rawOutput as Record<string, unknown> : null
  const metadata = output?.metadata && typeof output.metadata === 'object' ? output.metadata as Record<string, unknown> : null
  if (typeof metadata?.toolName === 'string') return metadata.toolName
  if (typeof metadata?.name === 'string') return metadata.name

  return 'unknown'
}

function normalizeToolName(toolName: string | undefined, title?: string, rawInput?: unknown, rawOutput?: unknown): string {
  const inferred = inferToolName(toolName, title, rawInput, rawOutput)
  if (!inferred || inferred === 'unknown') return 'unknown'
  return inferred.toLowerCase().replace(/[^a-z0-9]/g, '_')
}

function inferDisplayTitle(toolName: string, title?: string): { displayTitle?: string; displaySubtitle?: string } {
  if (title) {
    return { displayTitle: title }
  }
  const normalized = normalizeToolName(toolName)
  const displayTitles: Record<string, string> = {
    apply_patch: 'Patch',
    read: 'Read',
    write: 'Write',
    edit: 'Edit',
    glob: 'Glob',
    grep: 'Search',
    list: 'List',
    todowrite: 'Update todo list',
    membrowse: 'Browse',
    memread: 'Read memory',
    memsearch: 'Search memory',
  }
  return { displayTitle: displayTitles[normalized] ?? toolName }
}

function createToolPart(tool: LiveToolCall): ToolPart {
  const normalizedName = normalizeToolName(tool.toolName, tool.title, tool.rawInput, tool.rawOutput)
  const { displayTitle, displaySubtitle } = inferDisplayTitle(normalizedName, tool.title)
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
      status: tool.status,
      title: tool.title,
      target: tool.target,
      input,
      output,
      error: tool.error,
      startedAt: tool.startedAt,
      completedAt: tool.completedAt,
      rawInput: input,
      rawOutput: output,
      changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
    },
  }
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

function updateToolInTurn(turn: SessionTurn, toolCallId: string, updates: Partial<LiveToolCall>): SessionTurn {
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
        const startedAt = updates.startedAt ?? toolPart.tool.startedAt
        const normalizedName = normalizeToolName(
          updates.toolName ?? toolPart.tool.toolName,
          updates.title ?? toolPart.tool.title,
          rawInput,
          rawOutput,
        )
        const input = stringifyPayload(rawInput) ?? toolPart.tool.input
        const output = stringifyPayload(rawOutput) ?? toolPart.tool.output
        const parsedEdit = parseEditInput(input)
        const changedFiles = parsedEdit?.patch ? parsePatchOperations(parsedEdit.patch) : toolPart.tool.changedFiles
        return {
          ...toolPart,
          tool: {
            ...toolPart.tool,
            ...updates,
            normalizedName,
            input,
            output,
            rawInput: input,
            rawOutput: output,
            changedFiles: changedFiles && changedFiles.length > 0 ? changedFiles : undefined,
            startedAt,
            completedAt: updates.status === 'completed' || updates.status === 'failed' ? now : toolPart.tool.completedAt,
          },
        }
      }),
    }
  }

  return {
    ...turn,
    assistant: [
      ...turn.assistant,
      createToolPart({
        toolCallId,
        toolName: updates.toolName ?? 'unknown',
        status: updates.status ?? 'started',
        title: updates.title,
        target: updates.target,
        input: updates.input,
        output: updates.output,
        error: updates.error,
        rawInput: updates.rawInput,
        rawOutput: updates.rawOutput,
        startedAt: now,
        completedAt: updates.status === 'completed' || updates.status === 'failed' ? now : null,
      }),
    ],
  }
}

export function useSessionTranscript({
  issueNumber,
  sessionId,
  acpSessionId,
  initialTurns,
  isRunning,
}: UseSessionTranscriptOptions): UseSessionTranscriptResult {
  const queryClient = useQueryClient()
  const [turns, setTurns] = useState<SessionTurn[]>(initialTurns)
  const [transcriptVersion, setTranscriptVersion] = useState(0)
  const [isNearBottom, setIsNearBottom] = useState(true)
  const [newContentAvailable, setNewContentAvailable] = useState(false)
  const [isFinalizing, setIsFinalizing] = useState(false)

  const turnsRef = useRef(turns)
  turnsRef.current = turns

  const liveToolCallMapRef = useRef<Map<string, LiveToolCall>>(new Map())
  const mountedRef = useRef(true)

  const issueId = String(issueNumber)

  const scrollToBottom = useCallback(() => {
    setIsNearBottom(true)
    setNewContentAvailable(false)
  }, [])

  const bumpTranscriptVersion = useCallback(() => {
    setTranscriptVersion((version) => version + 1)
  }, [])

  const markNewContent = useCallback(() => {
    bumpTranscriptVersion()
    if (!isNearBottom) {
      setNewContentAvailable(true)
    }
  }, [bumpTranscriptVersion, isNearBottom])

  const markNewContentRef = useRef(markNewContent)
  markNewContentRef.current = markNewContent

  const acknowledgeNewContent = useCallback(() => {
    setNewContentAvailable(false)
  }, [])

  useEffect(() => {
    setTurns(initialTurns)
    liveToolCallMapRef.current.clear()
    setIsFinalizing(false)
    setTranscriptVersion((version) => version + 1)
  }, [initialTurns])

  useEffect(() => {
    if (!isRunning) return

    mountedRef.current = true
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        setTurns((prev) => {
          const next = ensureLiveTurn(prev, new Date().toISOString())
          const lastTurn = next[next.length - 1]
          next[next.length - 1] = appendTextToTurn(lastTurn, detail.text)
          return next
        })
        markNewContentRef.current()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

        const now = new Date().toISOString()

        if (detail.state === 'started') {
          liveToolCallMapRef.current.set(detail.toolCallId, {
            toolCallId: detail.toolCallId,
            toolName: detail.toolName,
            status: 'started',
            title: detail.title,
            target: detail.title,
            input: stringifyPayload(detail.rawInput),
            output: stringifyPayload(detail.rawOutput),
            error: '',
            rawInput: detail.rawInput,
            rawOutput: detail.rawOutput,
            startedAt: now,
            completedAt: null,
          })

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, detail.toolCallId, {
              toolName: detail.toolName,
              status: 'started',
              title: detail.title,
              target: detail.title,
              input: stringifyPayload(detail.rawInput),
              output: stringifyPayload(detail.rawOutput),
              rawInput: detail.rawInput,
              rawOutput: detail.rawOutput,
              startedAt: now,
            })
            return next
          })
          markNewContentRef.current()
        } else {
          const existing = liveToolCallMapRef.current.get(detail.toolCallId)
          if (existing) {
            existing.status = detail.state
            existing.output = stringifyPayload(detail.rawOutput)
            existing.rawOutput = detail.rawOutput
            existing.completedAt = now
          }

          setTurns((prev) => {
            const next = ensureLiveTurn(prev, now)
            const lastTurn = next[next.length - 1]
            next[next.length - 1] = updateToolInTurn(lastTurn, detail.toolCallId, {
              status: detail.state,
              output: stringifyPayload(detail.rawOutput),
              rawOutput: detail.rawOutput,
              completedAt: now,
            })
            return next
          })
          markNewContentRef.current()
        }
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.coderSessionId !== sessionId) return

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
        setIsFinalizing(true)
        markNewContentRef.current()

        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions', sessionId] })
      }),
    )

    unsubs.push(
      onAgentEvent('coder_recovery_status', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        if (detail.acpSessionId !== acpSessionId) return

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
          queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'coder-sessions', sessionId] })
        }
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
    }
  }, [issueId, sessionId, acpSessionId, issueNumber, isRunning, queryClient])

  return {
    turns,
    transcriptVersion,
    isNearBottom,
    setIsNearBottom,
    scrollToBottom,
    newContentAvailable,
    acknowledgeNewContent,
    isFinalizing,
  }
}
