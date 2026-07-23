import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { waitFor, act } from './test-utils'
import { useSessionTranscript } from '../src/widgets/session-transcript/model/useSessionTranscript'
import { dispatchAgentEvent } from '../src/entities/agent'
import type { TextPart, ToolPart, ErrorPart } from '../src/entities/coder-session'
import { renderHookWithQueryClient, makeTurn, queryClients } from './session-page-test-utils'
import { setScopedValue } from './support/scoped-property'

beforeEach(() => {
  vi.clearAllMocks()
  setScopedValue(navigator, 'clipboard', { writeText: vi.fn().mockResolvedValue(undefined) })
  setScopedValue(Element.prototype, 'scrollTo', vi.fn())
})

afterEach(() => {
  vi.useRealTimers()
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

describe('Live tool updates merge in place', () => {
  it('merges start and update events for same toolCallId into one tool card', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-merge-test',
        toolName: 'read',
        state: 'started',
        title: 'Read src/index.ts',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.toolCallId).toBe('tc-merge-test')
    })

    const firstToolId = result.current.turns.at(-1)?.assistant.find(
      (part): part is ToolPart => part.type === 'tool',
    )?.id

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-merge-test',
        toolName: 'read',
        state: 'completed',
        rawOutput: 'file content here',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].id).toBe(firstToolId)
      expect(toolParts?.[0].tool.status).toBe('completed')
      expect(toolParts?.[0].tool.output).toBe('file content here')
    })
  })

  it('updates existing tool card on terminal events without creating duplicate', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-terminal',
        toolName: 'bash',
        state: 'started',
        title: 'Run tests',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('running')
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-terminal',
        toolName: 'bash',
        state: 'completed',
        rawOutput: 'All tests passed',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('completed')
      expect(toolParts?.[0].tool.output).toBe('All tests passed')
    })
  })
})

describe('Terminal session events trigger refetch', () => {
  it('session.activity=idle closes the live turn and triggers refetch', async () => {
    // issue-484: the deprecated session.closed event no longer drives a status
    // patch (D6). Execution ending is now signalled by session.activity with
    // activity 'idle' (or 'unknown'), which closes the live turn and
    // invalidates session queries (refetch). It does not set isFinalizing —
    // that flag is reserved for recovery/liveness convergence.
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    expect(result.current.isFinalizing).toBe(false)
    expect(result.current.turns.at(-1)?.completedAt).toBeNull()

    act(() => {
      dispatchAgentEvent('session.activity', {
        sessionId: 'session-123',
        runtimeSessionId: 'runtime-123',
        activity: 'idle',
      })
    })

    await waitFor(() => {
      // The live turn is closed (completedAt populated) by the activity event.
      expect(result.current.turns.at(-1)?.completedAt).not.toBeNull()
    })
    // isFinalizing is NOT set by session.activity (no finalizing concept).
    expect(result.current.isFinalizing).toBe(false)
  })

  // issue-484: the two tests below depended on the deprecated session.closed
  // event to mark the session finalizing and append a failed/cancelled error
  // part to the transcript. Under the activity model session.closed is no
  // longer subscribed (D6): sessions never enter a terminal status, and the
  // failure/cancellation surface is now expressed via coder_recovery_status,
  // session.liveness (recovery error parts) and SessionErrorsEvidence /
  // failureReason in the header — none of which are driven by session.closed.
  // The liveness-driven recovery-error path is covered by 'liveness status
  // running or failed triggers refetch and explainable transcript parts'; the
  // recovery-status refetch path is covered by 'recovery status with recovered
  // or failed triggers refetch'. These two session.closed scenarios are
  // intentionally deleted as they no longer represent product behaviour.

  it('recovery status with recovered or failed triggers refetch', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_recovery_status', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        sessionId: 'session-123',
        runtimeSessionId: 'runtime-123',
        status: 'recovered',
        attempt: 1,
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })
  })

  it('liveness status running or failed triggers refetch and explainable transcript parts', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('session.liveness', {
        sessionId: 'session-123',
        runtimeSessionId: 'runtime-123',
        status: 'failed',
        lastDataAt: '2024-01-01T00:00:02.000Z',
        lastActivityType: 'agent_thought_chunk',
        probeSentAt: '2024-01-01T00:00:01.000Z',
        probeDeadlineAt: '2024-01-01T00:00:31.000Z',
        activeProbeVersion: 4,
        failureReason: 'probe_timeout',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
      const errorParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ErrorPart => part.type === 'error' && part.kind === 'recovery',
      )
      expect(errorParts?.some((part) => part.message.includes('Liveness failed: probe_timeout'))).toBe(true)
    })
  })
})

describe('Running session shows only real active tools', () => {
  it('does not create orphan unknown tool cards during streaming', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-known',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.normalizedName).toBe('read')
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-known',
        toolName: 'read',
        state: 'completed',
        rawOutput: 'content',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('completed')
    })

    const allToolParts = result.current.turns.flatMap(t => t.assistant).filter(
      (part): part is ToolPart => part.type === 'tool',
    )
    const orphanUnknown = allToolParts.filter(
      p => p.tool.normalizedName === 'unknown' && p.tool.status === 'running',
    )
    expect(orphanUnknown).toHaveLength(0)
  })

  it('maps started status to running display status', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-running',
        toolName: 'bash',
        state: 'started',
        title: 'Build project',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('running')
    })
  })

  it('maps failed status correctly for tool calls', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-failed',
        toolName: 'edit',
        state: 'failed',
        rawOutput: 'File not found',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('failed')
      expect(toolParts?.[0].tool.error).toBe('File not found')
    })
  })
})

describe('Live convergence with refetch', () => {
  it('marks isFinalizing after terminal tool event', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

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

  it('preserves text chunk appends before and after tool events', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'Starting task...',
        sessionId: 'session-123',
      })
    })

    await waitFor(() => {
      const textPart = result.current.turns.at(-1)?.assistant.find(
        (p): p is TextPart => p.type === 'text',
      )
      expect(textPart?.text).toBe('Starting task...')
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-1',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'Reading file...',
        sessionId: 'session-123',
      })
    })

    await waitFor(() => {
      const textPart = result.current.turns.at(-1)?.assistant.find(
        (p): p is TextPart => p.type === 'text',
      )
      expect(textPart?.text).toBe('Starting task...Reading file...')
    })
  })
})

describe('Correlation-based tool merging', () => {
  it('merges update-only events with pending tools by normalized name plus target', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-pending',
        toolName: 'read',
        state: 'started',
        title: 'src/app.ts',
        rawInput: { file_path: 'src/app.ts' },
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-update',
        toolName: 'read',
        state: 'completed',
        rawOutput: 'file content',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.output).toBe('file content')
    })
  })
})

describe('Thinking state for live sessions', () => {
  it('sets isThinking true when session is running with no visible assistant content', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    await waitFor(() => {
      expect(result.current.isThinking).toBe(true)
    })
  })

  it('sets isThinking false when text chunk arrives', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    await waitFor(() => {
      expect(result.current.isThinking).toBe(true)
    })

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'Hello world',
        sessionId: 'session-123',
      })
    })

    await waitFor(() => {
      expect(result.current.isThinking).toBe(false)
    })
  })

  it('sets isThinking false when tool call starts', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    await waitFor(() => {
      expect(result.current.isThinking).toBe(true)
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-thinking',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    await waitFor(() => {
      expect(result.current.isThinking).toBe(false)
    })
  })

  it('resets isThinking when initialTurns change', async () => {
    const initialTurns = [makeTurn()]
    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: false,
    }))

    expect(result.current.isThinking).toBe(false)
  })
})

describe('Scroll follow behavior', () => {
  it('does not auto-scroll when user is not near bottom', async () => {
    const scrollToMock = vi.fn()
    setScopedValue(Element.prototype, 'scrollTo', scrollToMock)

    const initialTurns = [makeTurn()]

    renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    await waitFor(() => {
      expect(scrollToMock).not.toHaveBeenCalled()
    })
  })

  it('newContentAvailable is set when not near bottom and new content arrives', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => result.current.setIsNearBottom(false))
    await waitFor(() => expect(result.current.isNearBottom).toBe(false))

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'New content',
        sessionId: 'session-123',
      })
    })

    await waitFor(() => {
      expect(result.current.newContentAvailable).toBe(true)
    })
  })

  it('acknowledgeNewContent clears newContentAvailable', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => result.current.setIsNearBottom(false))
    await waitFor(() => expect(result.current.isNearBottom).toBe(false))

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'New content',
        sessionId: 'session-123',
      })
    })

    await waitFor(() => {
      expect(result.current.newContentAvailable).toBe(true)
    })

    act(() => {
      result.current.acknowledgeNewContent()
    })

    expect(result.current.newContentAvailable).toBe(false)
  })
})

describe('Live update convergence', () => {
  it('tool start and completion update same tool part without duplication', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-converge',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].tool.status).toBe('running')
    })

    const firstToolId = result.current.turns.at(-1)?.assistant.find(
      (part): part is ToolPart => part.type === 'tool',
    )?.id

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-converge',
        toolName: 'read',
        state: 'completed',
        rawOutput: 'file content',
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(1)
      expect(toolParts?.[0].id).toBe(firstToolId)
      expect(toolParts?.[0].tool.status).toBe('completed')
      expect(toolParts?.[0].tool.output).toBe('file content')
    })
  })

  it('preserves turn order after multiple live events', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'runtime-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'First text',
        sessionId: 'session-123',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        sessionId: 'session-123',
        toolCallId: 'tc-order-1',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'runtime-123',
        text: 'Second text',
        sessionId: 'session-123',
      })
    })

    await waitFor(() => {
      const textParts = result.current.turns.at(-1)?.assistant.filter(
        (p): p is TextPart => p.type === 'text',
      )
      expect(textParts).toHaveLength(1)
      expect(textParts?.[0].text).toBe('First textSecond text')
    })

    const toolParts = result.current.turns.at(-1)?.assistant.filter(
      (part): part is ToolPart => part.type === 'tool',
    )
    expect(toolParts).toHaveLength(1)
    expect(toolParts?.[0].tool.toolCallId).toBe('tc-order-1')
  })
})
