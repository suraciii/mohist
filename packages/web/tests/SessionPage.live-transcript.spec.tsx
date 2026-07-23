import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { waitFor, renderHook, act } from './test-utils'
import { useSessionTranscript } from '../src/widgets/session-transcript/model/useSessionTranscript'
import { dispatchAgentEvent } from '../src/entities/agent'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'
import type { SessionTurn, TextPart, ToolPart, ErrorPart } from '../src/entities/coder-session'
import { setScopedValue } from './support/scoped-property'

const queryClients: QueryClient[] = []

beforeEach(() => {
  vi.clearAllMocks()
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
})

afterEach(() => {
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
}

function renderHookWithQueryClient<T>(callback: () => T) {
  const queryClient = createMockQueryClient()
  queryClients.push(queryClient)
  return renderHook(callback, {
    wrapper: ({ children }: { children: React.ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  })
}

function makeTurn(overrides: Partial<SessionTurn> = {}): SessionTurn {
  return {
    id: 'turn-1',
    startedAt: '2024-01-01T10:00:00.000Z',
    completedAt: null,
    user: {
      role: 'mohist',
      text: 'Test prompt text',
      kind: 'task',
      sentAt: '2024-01-01T10:00:00.000Z',
    },
    assistant: [],
    ...overrides,
  }
}

function renderLiveTranscript(initialTurns: SessionTurn[] = [makeTurn()]) {
  return renderHookWithQueryClient(() => useSessionTranscript({
    issueNumber: 123,
    sessionId: 'session-123',
    runtimeSessionId: 'runtime-123',
    initialTurns,
    isRunning: true,
  }))
}

describe('useSessionTranscript live parity and convergence', () => {
  it('reclassifies live unknown tool to skill from title and payload like replay', async () => {
    const { result } = renderLiveTranscript()

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-skill-live',
        toolName: 'unknown',
        state: 'started',
        title: 'Loaded skill: software-design',
        rawInput: { name: 'software-design' },
      })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.normalizedName).toBe('skill')
      expect(toolPart?.tool.displayTitle).toBe('Loaded skill: software-design')
      expect(toolPart?.tool.details).toMatchObject({ family: 'skill', skillName: 'software-design' })
    })
  })

  it('reclassifies live unknown tool to websearch from semantic payload', async () => {
    const { result } = renderLiveTranscript()

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-websearch-live',
        toolName: 'unknown',
        state: 'started',
        rawInput: { url: 'https://example.com', search_query: 'semantic titles' },
      })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.normalizedName).toBe('websearch')
      expect(toolPart?.tool.details).toMatchObject({ family: 'interaction', url: 'https://example.com', query: 'semantic titles' })
    })
  })

  it('reclassifies live unknown tool to todo from semantic title', async () => {
    const { result } = renderLiveTranscript()

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-todo-live',
        toolName: 'unknown',
        state: 'started',
        title: 'Todo: sync transcript tests',
        rawInput: { todos: [{ content: 'sync transcript tests', status: 'pending' }] },
      })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.normalizedName).toBe('todo')
      expect(toolPart?.tool.details).toMatchObject({ family: 'planning', totalCount: 1, statusCounts: { pending: 1 } })
    })
  })

  it('keeps live todo rows visible with semantic planning details', async () => {
    const { result } = renderLiveTranscript()

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-todowrite-live',
        toolName: 'todowrite',
        state: 'started',
        rawInput: { todos: [{ content: 'ship parity test', status: 'in_progress' }] },
      })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.normalizedName).toBe('todowrite')
      expect(toolPart?.hidden).toBeUndefined()
      expect(toolPart?.tool.details).toMatchObject({ family: 'planning', totalCount: 1, statusCounts: { in_progress: 1 } })
    })
  })

  it('marks isFinalizing after terminal tool event', async () => {
    const { result } = renderLiveTranscript()

    expect(result.current.isFinalizing).toBe(false)

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-final',
        toolName: 'bash',
        state: 'started',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(false)
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-final',
        toolName: 'bash',
        state: 'completed',
        rawOutput: 'done',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })
  })

  it('closes the live turn after a bound session.activity=idle event until refetch', async () => {
    // issue-484: session.closed is deprecated (D6). Execution ending is now
    // signalled by session.activity with activity 'idle', which closes the
    // live turn and invalidates session queries. It does not set isFinalizing
    // (that flag is reserved for recovery/liveness convergence).
    const { result } = renderLiveTranscript()
    expect(result.current.isFinalizing).toBe(false)
    expect(result.current.turns.at(-1)?.completedAt).toBeNull()
    act(() => {
      dispatchAgentEvent('session.activity', { sessionId: 'session-123', runtimeSessionId: 'runtime-123', activity: 'idle' })
    })
    await waitFor(() => expect(result.current.turns.at(-1)?.completedAt).not.toBeNull())
    expect(result.current.isFinalizing).toBe(false)
  })

  it('appends one recovery part for a single live recovery event', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_recovery_status', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', sessionId: 'session-123', runtimeSessionId: 'runtime-123', status: 'recovering', attempt: 1 })
    })
    await waitFor(() => {
      const recoveryParts = result.current.turns.at(-1)?.assistant.filter((part) => part.type === 'error' && part.kind === 'recovery')
      expect(recoveryParts).toHaveLength(1)
    })
  })

  it('appends liveness probe and recovery parts for live liveness events', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('session.liveness', {
        sessionId: 'session-123',
        runtimeSessionId: 'runtime-123',
        status: 'probing',
        lastDataAt: '2024-01-01T00:00:00.000Z',
        lastActivityType: 'agent_thought_chunk',
        probeSentAt: '2024-01-01T00:00:01.000Z',
        probeDeadlineAt: '2024-01-01T00:00:31.000Z',
        activeProbeVersion: 4,
      })
      dispatchAgentEvent('session.liveness', {
        sessionId: 'session-123',
        runtimeSessionId: 'runtime-123',
        status: 'running',
        lastDataAt: '2024-01-01T00:00:02.000Z',
        lastActivityType: 'tool_result',
        satisfiedProbeVersion: 4,
      })
    })
    await waitFor(() => {
      const recoveryParts = result.current.turns.at(-1)?.assistant.filter((part): part is ErrorPart => part.type === 'error' && part.kind === 'recovery')
      expect(recoveryParts).toHaveLength(2)
      expect(recoveryParts?.[0].message).toContain('Liveness probe sent')
      expect(recoveryParts?.[1].message).toContain('Liveness recovered')
    })
  })

  it('tool start and completion update same tool part without duplication', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-converge', toolName: 'read', state: 'started', rawInput: { file_path: 'src/index.ts' } })
    })
    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter((part): part is ToolPart => part.type === 'tool')
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('running')
    })
    const firstToolId = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')?.id
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-converge', toolName: 'read', state: 'completed', rawOutput: 'file content' })
    })
    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter((part): part is ToolPart => part.type === 'tool')
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].id).toBe(firstToolId)
      expect(toolParts?.[0].tool.status).toBe('completed')
      expect(toolParts?.[0].tool.output).toBe('file content')
    })
  })

  it('merges update-only events with pending tools by normalized name plus target', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-pending', toolName: 'read', state: 'started', title: 'src/app.ts', rawInput: { file_path: 'src/app.ts' } })
    })
    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter((part): part is ToolPart => part.type === 'tool')
      expect(toolParts).toHaveLength(1)
    })
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-update', toolName: 'read', state: 'completed', rawOutput: 'file content' })
    })
    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter((part): part is ToolPart => part.type === 'tool')
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.output).toBe('file content')
    })
  })

  it('updates existing tool card on terminal events without creating duplicate', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-terminal', toolName: 'bash', state: 'started', title: 'Run tests' })
    })
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-terminal', toolName: 'bash', state: 'completed', rawOutput: 'All tests passed' })
    })
    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter((part): part is ToolPart => part.type === 'tool')
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.output).toBe('All tests passed')
    })
  })

  it('derives execution output preview from structured terminal output', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-structured-terminal',
        toolName: 'bash',
        state: 'completed',
        rawInput: { command: 'npm test' },
        rawOutput: { stdout: 'All tests passed', exitCode: 0 },
      })
    })
    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter((part): part is ToolPart => part.type === 'tool')
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.details?.outputPreview).toBe('All tests passed')
      expect(toolParts?.[0].tool.details?.exitCode).toBe(0)
    })
  })

  it('replaces generic fallback titles with later semantic event titles', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-late-title', toolName: 'read', state: 'started', rawInput: {} })
    })
    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.displayTitle).toBe('Read')
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-late-title', toolName: 'read', state: 'completed', title: 'Read src/app.ts', rawOutput: 'file content' })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.displayTitle).toBe('Read src/app.ts')
      expect(toolPart?.tool.status).toBe('completed')
    })
  })

  it('maps failed status correctly for tool calls', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-failed', toolName: 'edit', state: 'failed', rawOutput: 'File not found' })
    })
    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(toolPart?.tool.status).toBe('failed')
      expect(toolPart?.tool.error).toBe('File not found')
    })
  })

  it('sets and clears thinking state from live content', async () => {
    const { result } = renderLiveTranscript()
    await waitFor(() => expect(result.current.isThinking).toBe(true))
    act(() => {
      dispatchAgentEvent('coder_text_chunk', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', text: 'Hello world', sessionId: 'session-123' })
    })
    await waitFor(() => expect(result.current.isThinking).toBe(false))
  })

  it('tracks new content while reader is away from bottom', async () => {
    const { result } = renderLiveTranscript()
    act(() => result.current.setIsNearBottom(false))
    await waitFor(() => expect(result.current.isNearBottom).toBe(false))
    act(() => {
      dispatchAgentEvent('coder_text_chunk', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', text: 'New content', sessionId: 'session-123' })
    })
    await waitFor(() => expect(result.current.newContentAvailable).toBe(true))
    act(() => result.current.acknowledgeNewContent())
    expect(result.current.newContentAvailable).toBe(false)
  })

  it('restores follow mode when scrollToBottom is called', () => {
    const { result } = renderLiveTranscript()
    act(() => result.current.setIsNearBottom(false))
    act(() => result.current.scrollToBottom())
    expect(result.current.isNearBottom).toBe(true)
  })

  it('preserves text append before and after tool events through reconciliation', async () => {
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('coder_text_chunk', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', text: 'Reading files...', sessionId: 'session-123' })
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-file', toolName: 'read', state: 'started', rawInput: { file_path: 'test.txt' } })
      dispatchAgentEvent('coder_text_chunk', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', text: 'Done reading.', sessionId: 'session-123' })
      dispatchAgentEvent('coder_tool_call', { issueNumber: 123, projectId: 'project-1', executionId: 'exec-123', runtimeSessionId: 'runtime-123', sessionId: 'session-123', toolCallId: 'tc-file', toolName: 'read', state: 'completed', rawOutput: 'file content' })
    })
    await waitFor(() => {
      const textPart = result.current.turns.at(-1)?.assistant.find((p): p is TextPart => p.type === 'text')
      const toolPart = result.current.turns.at(-1)?.assistant.find((part): part is ToolPart => part.type === 'tool')
      expect(textPart?.text).toBe('Reading files...Done reading.')
      expect(toolPart?.tool.output).toBe('file content')
    })
  })

  it('appends recovery/terminal errors as dedicated parts', async () => {
    // issue-484: session.closed no longer appends a failed error part (D6 —
    // the event is deprecated and no longer subscribed). Terminal/recovery
    // errors are now surfaced via session.liveness (status=failed), which
    // appends a recovery-kind error part carrying the failure reason.
    const { result } = renderLiveTranscript()
    act(() => {
      dispatchAgentEvent('session.liveness', { sessionId: 'session-123', runtimeSessionId: 'runtime-123', status: 'failed', failureReason: 'Out of memory', lastDataAt: '2024-01-01T00:00:02.000Z', lastActivityType: 'agent_thought_chunk' })
    })
    await waitFor(() => {
      const errorParts = result.current.turns.at(-1)?.assistant.filter((part): part is ErrorPart => part.type === 'error' && part.kind === 'recovery')
      expect(errorParts).toHaveLength(1)
      expect(errorParts?.[0].message).toContain('Out of memory')
    })
  })
})
