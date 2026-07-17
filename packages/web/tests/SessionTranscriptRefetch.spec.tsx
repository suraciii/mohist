import { describe, it, expect } from 'vitest'
import { waitFor, act } from './test-utils'
import { useSessionTranscript } from '../src/widgets/session-transcript/model/useSessionTranscript'
import { dispatchAgentEvent } from '../src/entities/agent'
import type { TextPart, ToolPart } from '../src/entities/coder-session'
import { renderHookWithQueryClient, makeTurn } from './session-page-test-utils'

describe('Live-then-refetch transcript equivalence', () => {
  it('live tool grouping matches replayed context-group after refetch simulation', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-read-1',
        toolName: 'Read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-read-2',
        toolName: 'Read',
        state: 'started',
        rawInput: { file_path: 'src/app.ts' },
      })
    })

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(2)
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-read-1',
        toolName: 'Read',
        state: 'completed',
        rawOutput: 'index content',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-read-2',
        toolName: 'Read',
        state: 'completed',
        rawOutput: 'app content',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })

    const liveTurns = result.current.turns
    const liveToolParts = liveTurns.flatMap(t => t.assistant).filter(
      (part): part is ToolPart => part.type === 'tool',
    )

    expect(liveToolParts).toHaveLength(2)
    expect(liveToolParts.every(p => p.tool.status === 'completed')).toBe(true)
    expect(liveToolParts.every(p => p.tool.normalizedName === 'read')).toBe(true)
  })

  it('live tool identity remains consistent after terminal refetch reconciliation', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-ident',
        toolName: 'grep',
        state: 'started',
        rawInput: { pattern: 'function foo', file_path: 'src/' },
      })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find(
        (part): part is ToolPart => part.type === 'tool' && part.tool.toolCallId === 'tc-ident',
      )
      expect(toolPart?.tool.normalizedName).toBe('grep')
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-ident',
        toolName: 'grep',
        state: 'completed',
        rawOutput: 'found 3 matches',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })

    const toolPart = result.current.turns.at(-1)?.assistant.find(
      (part): part is ToolPart => part.type === 'tool' && part.tool.toolCallId === 'tc-ident',
    )
    expect(toolPart?.tool.normalizedName).toBe('grep')
    expect(toolPart?.tool.status).toBe('completed')
    expect(toolPart?.tool.output).toBe('found 3 matches')
  })

  it('multiple sequential live tool events maintain correct order after refetch', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    const toolSequence = [
      { id: 'tc-seq-1', tool: 'read', input: { file_path: 'a.txt' }, output: 'a' },
      { id: 'tc-seq-2', tool: 'read', input: { file_path: 'b.txt' }, output: 'b' },
      { id: 'tc-seq-3', tool: 'bash', input: { command: 'echo hi' }, output: 'hi' },
    ]

    for (const item of toolSequence) {
      act(() => {
        dispatchAgentEvent('coder_tool_call', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          sessionId: 'session-123',
          toolCallId: item.id,
          toolName: item.tool,
          state: 'started',
          rawInput: item.input,
        })
      })
    }

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolParts).toHaveLength(3)
    })

    for (const item of toolSequence) {
      act(() => {
        dispatchAgentEvent('coder_tool_call', {
          issueNumber: 123,          projectId: 'project-1',
          executionId: 'exec-123',
          runtimeSessionId: 'acp-123',
          sessionId: 'session-123',
          toolCallId: item.id,
          toolName: item.tool,
          state: 'completed',
          rawOutput: item.output,
        })
      })
    }

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })

    const toolParts = result.current.turns.at(-1)?.assistant.filter(
      (part): part is ToolPart => part.type === 'tool',
    )
    expect(toolParts).toHaveLength(3)
    expect(toolParts?.[0].tool.toolCallId).toBe('tc-seq-1')
    expect(toolParts?.[1].tool.toolCallId).toBe('tc-seq-2')
    expect(toolParts?.[2].tool.toolCallId).toBe('tc-seq-3')
    expect(toolParts?.every(p => p.tool.status === 'completed')).toBe(true)
  })

  it('text append before and after tool events preserved through reconciliation', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      runtimeSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        text: 'Reading files...',
        sessionId: 'session-123',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-file',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'test.txt' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        text: 'Done reading.',
        sessionId: 'session-123',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueNumber: 123,        projectId: 'project-1',
        executionId: 'exec-123',
        runtimeSessionId: 'acp-123',
        sessionId: 'session-123',
        toolCallId: 'tc-file',
        toolName: 'read',
        state: 'completed',
        rawOutput: 'file content',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })

    const textPart = result.current.turns.at(-1)?.assistant.find(
      (p): p is TextPart => p.type === 'text',
    )
    expect(textPart?.text).toBe('Reading files...Done reading.')

    const toolPart = result.current.turns.at(-1)?.assistant.find(
      (part): part is ToolPart => part.type === 'tool',
    )
    expect(toolPart?.tool.status).toBe('completed')
    expect(toolPart?.tool.output).toBe('file content')
  })
})
