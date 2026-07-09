import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, fireEvent, waitFor, act, within } from './test-utils'
import { SessionTranscriptView } from '../src/widgets/session-transcript/ui/SessionTranscriptView'
import { useSessionTranscript } from '../src/widgets/session-transcript/model/useSessionTranscript'
import { dispatchAgentEvent } from '../src/entities/agent'
import type { TextPart, ToolPart } from '../src/entities/coder-session'
import { renderWithQueryClient, renderHookWithQueryClient, makeTurn, getAssistantCopyButton, expandChangedFilesTool, queryClients, originalScrollTo } from './session-page-test-utils'

Object.defineProperty(navigator, 'clipboard', {
  value: { writeText: vi.fn().mockResolvedValue(undefined) },
  configurable: true,
})

beforeEach(() => {
  vi.clearAllMocks()
  Element.prototype.scrollTo = vi.fn()
})

afterEach(() => {
  vi.useRealTimers()
  Element.prototype.scrollTo = originalScrollTo
  for (const queryClient of queryClients) queryClient.clear()
  queryClients.length = 0
})

describe('ToolRegistry', () => {
  describe('fallback behavior', () => {
    it('renders unknown tool using registry fallback entry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'SomeUnknownTool',
            status: 'completed',
            input: '{"arg1":"value1","description":"A custom tool"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/SomeUnknownTool/)).toBeInTheDocument()
      })
      expect(screen.queryByText(/^unknown$/i)).not.toBeInTheDocument()
    })

    it('falls back to raw toolName when no parsing signals available', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-fallback',
            toolName: 'MyCustomTool',
            status: 'completed',
            input: '{"foo":"bar"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/MyCustomTool/)).toBeInTheDocument()
      })
    })
  })

  describe('known tool-family renderer selection', () => {
    it('renders bash tool with command label from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-bash',
            normalizedName: 'bash',
            toolName: 'bash',
            status: 'completed',
            input: '{"command":"npm run build","cwd":"/project"}',
            output: 'build success',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/npm run build/)).toBeInTheDocument()
      })
    })

    it('renders read tool with file path label from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-read',
            normalizedName: 'read',
            toolName: 'Read',
            status: 'completed',
            input: '{"file_path":"src/index.ts"}',
            output: 'file content here',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/src\/index\.ts/)).toBeInTheDocument()
      })
    })

    it('renders grep tool with pattern label from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-grep',
            normalizedName: 'grep',
            toolName: 'grep',
            status: 'completed',
            input: '{"pattern":"function foo","type":"typescript"}',
            output: 'found 3 matches',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/function foo/)).toBeInTheDocument()
      })
    })

    it('renders webfetch tool with url subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-webfetch',
            normalizedName: 'webfetch',
            toolName: 'webfetch',
            status: 'completed',
            input: '{"url":"https://api.example.com/data","method":"GET"}',
            output: '{"data":"result"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/https:\/\/api\.example\.com\/data/)).toBeInTheDocument()
      })
    })

    it('renders question tool with query subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-question',
            normalizedName: 'question',
            toolName: 'question',
            status: 'completed',
            input: '{"question":"Which approach is better?"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Which approach is better\?/)).toBeInTheDocument()
      })
    })

    it('renders task tool with description subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-task',
            normalizedName: 'task',
            toolName: 'task',
            status: 'completed',
            input: '{"description":"Run tests on CI"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Run tests on CI/)).toBeInTheDocument()
      })
    })

    it('renders skill tool with name subtitle from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-skill',
            normalizedName: 'skill',
            toolName: 'skill',
            status: 'completed',
            input: '{"name":"debugging-code"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/debugging-code/)).toBeInTheDocument()
      })
    })

    it('renders edit tool with file name from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            normalizedName: 'edit',
            toolName: 'edit',
            status: 'completed',
            input: '{"file_path":"src/app.ts","oldString":"foo","newString":"bar"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/app\.ts/)).toBeInTheDocument()
      })
    })

    it('renders write tool with file name from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-write',
            normalizedName: 'write',
            toolName: 'write',
            status: 'completed',
            input: '{"path":"src/new-file.ts","content":"hello world"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/new-file\.ts/)).toBeInTheDocument()
      })
    })

    it('renders apply_patch tool with file summary from registry', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-patch',
            normalizedName: 'apply_patch',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Add File: src/brand-new.ts\n+++ b/src/brand-new.ts\n@@ -0,0 +1 @@\n+new content' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
    })
  })
})

describe('Live-then-refetch transcript equivalence', () => {
  it('live tool grouping matches replayed context-group after refetch simulation', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
        toolCallId: 'tc-read-1',
        toolName: 'Read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
        toolCallId: 'tc-read-1',
        toolName: 'Read',
        state: 'completed',
        rawOutput: 'index content',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
      acpSessionId: 'acp-123',
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
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          coderSessionId: 'session-123',
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
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          coderSessionId: 'session-123',
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
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        text: 'Reading files...',
        coderSessionId: 'session-123',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
        toolCallId: 'tc-file',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'test.txt' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        text: 'Done reading.',
        coderSessionId: 'session-123',
      })
    })

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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

describe('T-006: Transcript affordances', () => {
  describe('assistant reply copy action', () => {
    it('shows copy button on assistant text part', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Hello world',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Coder')).toBeInTheDocument()
      })
      expect(getAssistantCopyButton()).toBeInTheDocument()
    })

    it('copies assistant text when copy button is clicked', async () => {
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: mockWriteText },
        configurable: true,
      })

      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Copy this text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      expect(getAssistantCopyButton()).toBeInTheDocument()

      fireEvent.click(getAssistantCopyButton())

      await waitFor(() => {
        expect(mockWriteText).toHaveBeenCalledWith('Copy this text')
        expect(screen.getByText('Copied!')).toBeInTheDocument()
      })
    })

    it('copy button shows Copy again after timeout', async () => {
      vi.useFakeTimers()
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: mockWriteText },
        configurable: true,
      })

      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Test text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      expect(getAssistantCopyButton()).toBeInTheDocument()

      fireEvent.click(getAssistantCopyButton())

      await act(async () => {
        await Promise.resolve()
      })

      expect(mockWriteText).toHaveBeenCalledWith('Test text')
      expect(screen.getByText('Copied!')).toBeInTheDocument()

      act(() => {
        vi.advanceTimersByTime(2000)
      })

      expect(mockWriteText).toHaveBeenCalledWith('Test text')
      expect(getAssistantCopyButton()).toBeInTheDocument()

      vi.useRealTimers()
    })
  })

  describe('expanded diff inspection', () => {
    it('shows expanded diff view when rawDetail is available', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-diff',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/test.ts', old_string: 'old', new_string: 'new' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
            changedFiles: [
              {
                path: 'src/test.ts',
                operation: 'modified',
                additions: 1,
                deletions: 1,
                rawDetail: '--- a/src/test.ts\n+++ b/src/test.ts\n@@ -1 +1 @@\n-old\n+new',
              },
            ],
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/test.ts')).toBeInTheDocument()
      expect(screen.getByText('+1')).toBeInTheDocument()
      expect(screen.getByText('-1')).toBeInTheDocument()
    })

    it('hides diff content by default and shows it when expanded', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-diff',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/test.ts', old_string: 'old', new_string: 'new' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
            changedFiles: [
              {
                path: 'src/test.ts',
                operation: 'modified',
                additions: 1,
                deletions: 1,
                rawDetail: '--- a/src/test.ts\n+++ b/src/test.ts\n@@ -1 +1 @@\n-old\n+new',
              },
            ],
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/test.ts')).toBeInTheDocument()

      expect(screen.queryByText(/--- a\/src\/test.ts/)).not.toBeInTheDocument()

      fireEvent.click(screen.getByText(/Show raw patch/i))

      await waitFor(() => {
        expect(within(screen.getByText('Changes').closest('div')!.parentElement!).getByText(/old/)).toBeInTheDocument()
        expect(within(screen.getByText('Changes').closest('div')!.parentElement!).getByText(/new/)).toBeInTheDocument()
      })
    })

    it('shows file summary with additions/deletions when rawDetail is not available', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-diff',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Add File: src/new.ts\n+++ b/src/new.ts\n@@ -0,0 +1 @@\n+line1' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/new.ts')).toBeInTheDocument()
      expect(screen.getByText('+1')).toBeInTheDocument()
      expect(screen.getByText(/Show raw patch/i)).toBeInTheDocument()
    })

    it('shows rawDetail content in diff view when available', async () => {
      const rawDetailContent = '--- a/src/app.ts\n+++ b/src/app.ts\n@@ -1,3 +1,3 @@\n const x = 1\n-const y = 2\n+const y = 3\n const z = 4'

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/app.ts', old_string: 'const y = 2', new_string: 'const y = 3' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
            changedFiles: [
              {
                path: 'src/app.ts',
                operation: 'modified',
                additions: 1,
                deletions: 1,
                rawDetail: rawDetailContent,
              },
            ],
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
      expandChangedFilesTool()
      expect(screen.getByText('src/app.ts')).toBeInTheDocument()

      fireEvent.click(screen.getByText(/Show raw patch/i))

      await waitFor(() => {
        expect(screen.getAllByText(/const y = 2/).length).toBeGreaterThan(0)
        expect(screen.getAllByText(/const y = 3/).length).toBeGreaterThan(0)
      })
    })
  })
})

describe('T-007: Follow-mode scrolling and streaming text pacing', () => {
  describe('follow-mode pause/resume', () => {
    it('auto-scrolls when reader is near bottom during live session', async () => {
      const scrollToMock = vi.fn()
      Element.prototype.scrollTo = scrollToMock

      const initialTurns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Initial content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(true))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: ' More content',
          coderSessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(false)
      })
      expect(scrollToMock).not.toHaveBeenCalled()
    })

    it('does not auto-scroll when user scrolls away from bottom', async () => {
      const scrollToMock = vi.fn()
      Element.prototype.scrollTo = scrollToMock

      const initialTurns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Initial content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: ' More content',
          coderSessionId: 'session-123',
        })
      })

      expect(scrollToMock).not.toHaveBeenCalled()
    })

    it('restores follow mode when scrollToBottom is called', async () => {
      const scrollToMock = vi.fn()
      Element.prototype.scrollTo = scrollToMock

      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        result.current.scrollToBottom()
      })

      expect(result.current.isNearBottom).toBe(true)
      expect(result.current.newContentAvailable).toBe(false)
      expect(scrollToMock).not.toHaveBeenCalled()
    })
  })

  describe('new-content indicator behavior', () => {
    it('sets newContentAvailable when not near bottom and content arrives', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: 'New streaming content',
          coderSessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(true)
      })
    })

    it('does not set newContentAvailable when near bottom', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(true))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: 'New streaming content',
          coderSessionId: 'session-123',
        })
      })

      expect(result.current.newContentAvailable).toBe(false)
    })

    it('clears newContentAvailable when acknowledgeNewContent is called', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(false))
      await waitFor(() => expect(result.current.isNearBottom).toBe(false))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: 'New content',
          coderSessionId: 'session-123',
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

  describe('nested scrollable regions', () => {
    it('does not toggle follow mode when scrolling within nested scrollable region', async () => {
      const scrollToMock = vi.fn()
      Element.prototype.scrollTo = scrollToMock

      const initialTurns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Content with code block',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => result.current.setIsNearBottom(true))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: ' More content',
          coderSessionId: 'session-123',
        })
      })

      await waitFor(() => {
        expect(result.current.newContentAvailable).toBe(false)
      })
      expect(scrollToMock).not.toHaveBeenCalled()
    })
  })

  describe('streaming text pacing', () => {
    it('shows blinking cursor for incomplete text part', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Streaming text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={true} />)

      await waitFor(() => {
        expect(screen.getByText('Streaming text')).toBeInTheDocument()
      })

      const cursor = document.querySelector('span.animate-pulse')
      expect(cursor).toBeInTheDocument()
    })

    it('does not show blinking cursor for completed text part', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Completed text',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: '2024-01-01T10:00:02.000Z',
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Completed text')).toBeInTheDocument()
      })

      const cursors = document.querySelectorAll('span.animate-pulse')
      const textCursors = Array.from(cursors).filter(cursor => {
        const parent = cursor.parentElement
        return parent?.textContent?.includes('Completed text')
      })
      expect(textCursors).toHaveLength(0)
    })

    it('persisted transcript content is unchanged by pacing display', async () => {
      const initialTurns = [makeTurn()]

      const { result } = renderHookWithQueryClient(() => useSessionTranscript({
        issueNumber: 123,
        sessionId: 'session-123',
        acpSessionId: 'acp-123',
        initialTurns,
        isRunning: true,
      }))

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: 'First chunk',
          coderSessionId: 'session-123',
        })
      })

      act(() => {
        dispatchAgentEvent('coder_text_chunk', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          text: ' second chunk',
          coderSessionId: 'session-123',
        })
      })

      await waitFor(() => {
        const textPart = result.current.turns.at(-1)?.assistant.find(
          (p): p is TextPart => p.type === 'text',
        )
        expect(textPart?.text).toBe('First chunk second chunk')
      })
    })
  })
})
