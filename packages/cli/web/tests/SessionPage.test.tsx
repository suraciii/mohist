import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, waitFor, renderHook, act } from './test-utils'
import { SessionPage } from '../src/components/SessionPage'
import { SessionTranscriptView } from '../src/components/SessionTranscriptView'
import { useSessionTranscript } from '../src/hooks/useSessionTranscript'
import { dispatchAgentEvent } from '../src/lib/agent-events'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'
import type { SessionTurn, TextPart, ReasoningPart, ToolPart, ErrorPart, CoderSessionDetail, SessionMetadata } from '../src/lib/types'

const sessionPageMocks = vi.hoisted(() => ({
  sessions: [] as any[],
  sessionsLoading: false,
  issue: null as any,
  detail: null as CoderSessionDetail | null,
  detailError: null as Error | null,
  detailPending: false,
  params: { number: '123', sessionId: 'session-123' },
}))

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom')
  return {
    ...actual,
    useParams: () => sessionPageMocks.params,
  }
})

vi.mock('../src/hooks/useCoderSessions', () => ({
  useCoderSessions: () => ({ sessions: sessionPageMocks.sessions, isLoading: sessionPageMocks.sessionsLoading }),
}))

vi.mock('../src/hooks/useQueries', () => ({
  useIssue: () => ({ data: sessionPageMocks.issue }),
}))

vi.mock('../src/lib/api', () => ({
  api: {
    getCoderSessionDetail: vi.fn(() => {
      if (sessionPageMocks.detailPending) return new Promise(() => {})
      if (sessionPageMocks.detailError) return Promise.reject(sessionPageMocks.detailError)
      return Promise.resolve(sessionPageMocks.detail)
    }),
  },
}))

Object.defineProperty(navigator, 'clipboard', {
  value: { writeText: vi.fn().mockResolvedValue(undefined) },
  configurable: true,
})

const originalScrollTo = Element.prototype.scrollTo
beforeEach(() => {
  vi.clearAllMocks()
  sessionPageMocks.sessions = []
  sessionPageMocks.sessionsLoading = false
  sessionPageMocks.issue = null
  sessionPageMocks.detail = null
  sessionPageMocks.detailError = null
  sessionPageMocks.detailPending = false
  sessionPageMocks.params = { number: '123', sessionId: 'session-123' }
  Element.prototype.scrollTo = vi.fn()
})

afterEach(() => {
  Element.prototype.scrollTo = originalScrollTo
})

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = createMockQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      {ui}
    </QueryClientProvider>,
  )
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

describe('SessionTranscriptView', () => {
  describe('prompt card expansion and copy', () => {
    it('renders Mohist prompt card with kind and timestamp', async () => {
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Implement feature X',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Task')).toBeInTheDocument()
      })
      expect(screen.getAllByText(/10:00:00|2024/).length).toBeGreaterThan(0)
    })

    it('expands long prompt when Show full prompt is clicked', async () => {
      const longText = 'A'.repeat(600)
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: longText,
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('Show full prompt'))

      await waitFor(() => {
        expect(screen.getByText('Show less')).toBeInTheDocument()
      })
    })

    it('keeps raw prompt collapsed by default even when it is short', async () => {
      const rawPrompt = '<mohist-task><role>Implement fix</role><contract>proposal.md</contract></mohist-task>'
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: rawPrompt,
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          summary: {
            kind: 'task',
            title: 'Implement fix',
            subtitle: 'Output: proposal.md',
            outputPath: 'proposal.md',
          },
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Implement fix')).toBeInTheDocument()
      })
      expect(screen.getByText('Show full prompt')).toBeInTheDocument()
      expect(screen.queryByText(rawPrompt)).not.toBeInTheDocument()

      fireEvent.click(screen.getByText('Show full prompt'))

      await waitFor(() => {
        expect(screen.getByText(rawPrompt)).toBeInTheDocument()
      })
    })

    it('copies prompt text when Copy button is clicked', async () => {
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: mockWriteText },
        configurable: true,
      })

      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Copy me',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Copy')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('Copy'))

      await waitFor(() => {
        expect(mockWriteText).toHaveBeenCalledWith('Copy me')
        expect(screen.getByText('Copied!')).toBeInTheDocument()
      })
    })
  })

  describe('markdown assistant rendering', () => {
    it('renders markdown text with proper formatting', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: '# Heading\n\nSome **bold** text\n\n- List item\n\n```js\nconsole.log("code")\n```',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Heading')).toBeInTheDocument()
      })
      expect(screen.getByText('bold')).toBeInTheDocument()
      expect(screen.getByText('List item')).toBeInTheDocument()
      expect(screen.getByText('console.log("code")')).toBeInTheDocument()
    })

    it('renders inline code with proper styling', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'text-1',
          type: 'text',
          text: 'Use `const x = 1` for constants',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as TextPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        const codeElement = screen.getByText('const x = 1')
        expect(codeElement.tagName).toBe('CODE')
      })
    })
  })

  describe('collapsed reasoning', () => {
    it('renders reasoning as collapsed details with size and timestamp', async () => {
      const reasoningText = 'This is my thinking process...'.repeat(100)
      const turns = [makeTurn({
        assistant: [{
          id: 'reasoning-1',
          type: 'reasoning',
          text: reasoningText,
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as ReasoningPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })

      const summary = screen.getByText(/Thinking\.\.\./i).closest('details')?.querySelector('summary')
      expect(summary).toBeInTheDocument()
    })

    it('expands reasoning when clicked', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'reasoning-1',
          type: 'reasoning',
          text: 'Detailed reasoning content',
          startedAt: '2024-01-01T10:00:01.000Z',
          completedAt: null,
        } as ReasoningPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })

      const details = screen.getByText(/Thinking\.\.\./i).closest('details')
      if (details) {
        fireEvent.click(details.querySelector('summary')!)
        await waitFor(() => {
          expect(screen.getByText('Detailed reasoning content')).toBeInTheDocument()
        })
      }
    })
  })

  describe('generic unknown tool rendering', () => {
    it('renders unknown tool with generic fallback card', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'UnknownTool',
            status: 'completed',
            title: 'Unknown Tool',
            input: '{"arg1":"value1"}',
            output: '{"result":"ok"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('UnknownTool')).toBeInTheDocument()
      })

      const toolCard = screen.getByText('UnknownTool').closest('[class*="rounded"]')
      expect(toolCard).toBeInTheDocument()
    })

    it('renders tool with expandable input/output', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-unknown',
            toolName: 'CustomTool',
            status: 'completed',
            input: '{"custom":"input"}',
            output: '{"custom":"output"}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('CustomTool')).toBeInTheDocument()
      })

      const toolElement = screen.getByText('CustomTool')
      const button = toolElement.closest('button') ?? toolElement.closest('[class*="rounded-md"]')?.querySelector('button')
      if (button) {
        fireEvent.click(button)
        await waitFor(() => {
          expect(screen.getByText('Input')).toBeInTheDocument()
        })
      }
    })
  })

  describe('error part rendering', () => {
    it('renders error part with message', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'error-1',
          type: 'error',
          message: 'Execution failed',
          kind: 'failed',
          at: '2024-01-01T10:00:05.000Z',
        } as ErrorPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Execution failed/i)).toBeInTheDocument()
      })
    })
  })

  describe('empty and loading states', () => {
    it('shows no activity message when turns are empty and not running', () => {
      renderWithQueryClient(<SessionTranscriptView turns={[]} isRunning={false} />)
      expect(screen.getByText(/No activity recorded/i)).toBeInTheDocument()
    })

    it('shows waiting message when turns are empty and running', () => {
      renderWithQueryClient(<SessionTranscriptView turns={[]} isRunning={true} />)
      expect(screen.getByText(/Waiting for activity/i)).toBeInTheDocument()
    })
  })

  describe('turn rendering', () => {
    it('renders Mohist speaker label with timestamp', async () => {
      const turns = [makeTurn({
        startedAt: '2024-01-01T10:00:00.000Z',
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Mohist')).toBeInTheDocument()
      })
    })

    it('shows incomplete marker for legacy missing prompts', async () => {
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: 'Prompt was not recorded for this historical session',
          kind: 'legacy-missing',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        incomplete: true,
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Missing Prompt/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/Incomplete/i)).toBeInTheDocument()
    })

    it('renders Coder speaker label when assistant parts exist', async () => {
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
    })

    it('renders legacy missing prompt with gray styling and no expand', async () => {
      const turns = [makeTurn({
        user: {
          role: 'mohist',
          text: '',
          kind: 'legacy-missing',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        incomplete: true,
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Missing Prompt/i)).toBeInTheDocument()
      })
      expect(screen.queryByText('Show full prompt')).not.toBeInTheDocument()
    })
  })

  describe('context tool grouping', () => {
    it('groups consecutive context tools into Context gathered card', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'file content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-2',
              toolName: 'grep',
              status: 'completed',
              input: '{"pattern":"function"}',
              output: 'matches',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Context gathered/i)).toBeInTheDocument()
      })
    })

    it('expands context group to show individual tools', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-2',
              toolName: 'glob',
              status: 'completed',
              input: '{"pattern":"**/*.ts"}',
              output: 'files',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Context gathered/i)).toBeInTheDocument()
      })
      expect(screen.queryByText('content')).not.toBeInTheDocument()

      fireEvent.click(screen.getByText(/Context gathered/i))

      await waitFor(() => {
        expect(screen.getByText(/src\/index\.ts/i)).toBeInTheDocument()
      }, { timeout: 3000 })
    })

    it('does not group across text or reasoning boundaries', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'text-1',
            type: 'text',
            text: 'Let me check the files first',
            startedAt: '2024-01-01T10:00:01.000Z',
            completedAt: null,
          } as TextPart,
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Let me check the files first/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/Context gathered/i)).toBeInTheDocument()
    })

    it('shows failed count in context group summary', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'read',
              status: 'failed',
              input: '{"file_path":"missing.txt"}',
              error: 'File not found',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-2',
              toolName: 'grep',
              status: 'completed',
              input: '{"pattern":"function"}',
              output: 'matches',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Context gathered/)).toBeInTheDocument()
      })
    })

    it('groups tools using normalizedName when toolName is unknown', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-1',
            toolName: 'unknown',
            normalizedName: 'read',
            status: 'completed',
            input: '{"file_path":"src/index.ts"}',
            output: 'content',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Context gathered/i)).toBeInTheDocument()
      })
      expect(screen.queryByText('unknown')).not.toBeInTheDocument()
    })
  })

  describe('todowrite summary', () => {
    it('renders todowrite as Updated todo list by default', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'todowrite',
              status: 'completed',
              input: '{"todos":[{"content":"Task 1","status":"completed"},{"content":"Task 2","status":"pending"}]}',
              output: '{"todos":[{"content":"Task 1","status":"completed"},{"content":"Task 2","status":"pending"}]}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/\(2 items\)/i)).toBeInTheDocument()
    })

    it('renders normalized todowrite summary when toolName is unknown', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-1',
            toolName: 'unknown',
            normalizedName: 'todowrite',
            status: 'completed',
            input: '{"todos":[{"content":"Task 1","status":"completed"}]}',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })
    })

    it('expands todowrite to show tool details', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'todowrite',
              status: 'completed',
              input: '{"todos":[{"content":"Task 1","status":"completed"}]}',
              output: '{}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText(/Updated todo list/i))

      await waitFor(() => {
        expect(screen.getByText(/src\/index\.ts|Task 1/i)).toBeInTheDocument()
      }, { timeout: 3000 })
    })

    it('renders failed todowrite with failure indicator', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'todowrite',
              status: 'failed',
              input: '{"todos":[]}',
              error: 'Failed to update todos',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText(/Updated todo list/i)).toBeInTheDocument()
      })
      expect(screen.getByText(/failed/i)).toBeInTheDocument()
    })
  })

  describe('file-changing tool rendering', () => {
    it('renders apply_patch showing file summary', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-apply-patch',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Add File: src/new-file.ts\n+++ b/src/new-file.ts\n@@ -0,0 +1,2 @@\n+line 1\n+line 2' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.queryByText(/@@ -0,0 \+1,2 @@/i)).not.toBeInTheDocument()
    })

    it('renders apply_patch with title=apply_patch without toolName', async () => {
      const patchText = `*** Add File: src/brand-new.ts
+++ b/src/brand-new.ts
@@ -0,0 +1 @@
+new content`

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-patch',
            toolName: 'unknown',
            title: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
    })

    it('renders normalized apply_patch as file summary when toolName is unknown and title is a file', async () => {
      const patchText = `*** Add File: src/normalized.ts
+++ b/src/normalized.ts
@@ -0,0 +1 @@
+new content`

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-patch',
            toolName: 'unknown',
            normalizedName: 'apply_patch',
            title: 'src/normalized.ts',
            status: 'completed',
            input: JSON.stringify({ patchText }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        } as ToolPart],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.queryByText('unknown')).not.toBeInTheDocument()
      expect(screen.queryByText('src/normalized.ts')).not.toBeInTheDocument()
    })

    it('renders write as created file with file name', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-write',
            toolName: 'write',
            status: 'completed',
            input: JSON.stringify({ path: 'src/created.ts', content: 'line 1\nline 2\nline 3' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Created')).toBeInTheDocument()
      })
      expect(screen.getByText(/created\.ts/i)).toBeInTheDocument()
    })

    it('renders edit with modified file', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            toolName: 'edit',
            status: 'completed',
            input: JSON.stringify({ file_path: 'src/example.ts', old_string: 'old', new_string: 'new content' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Edited')).toBeInTheDocument()
      })
      expect(screen.getByText(/example\.ts/i)).toBeInTheDocument()
    })

    it('expands raw patch when Show raw patch is clicked', async () => {
      const patchText = `*** Add File: src/test.ts
+++ b/src/test.ts
@@ -0,0 +1 @@
+test`

      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-apply-patch',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })

      const showRawButton = screen.getByText(/Show raw patch/i)
      fireEvent.click(showRawButton)

      await waitFor(() => {
        expect(screen.getByText(/@@ -0,0 \+1 @@/i)).toBeInTheDocument()
      })
    })

    it('renders failed file-changing tool with error', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit-fail',
            toolName: 'edit',
            status: 'failed',
            input: JSON.stringify({ file_path: 'src/failing.ts', old_string: 'old', new_string: 'new' }),
            error: 'File not found',
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('Edited')).toBeInTheDocument()
      })
      expect(screen.getByText(/File not found/i)).toBeInTheDocument()
    })

    it('renders delete operation with deleted file', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-delete',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** Delete File: src/deleted.ts\n--- a/src/deleted.ts\n+++ b/src/deleted.ts\n@@ -1 +0,0 @@\n-old content' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.getByText(/deleted\.ts/i)).toBeInTheDocument()
    })

    it('renders moved file with new path', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-move',
            toolName: 'apply_patch',
            status: 'completed',
            input: JSON.stringify({ patchText: '*** OldPath: src/old-location.ts\n*** Move to: src/new-location.ts' }),
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
      expect(screen.getByText(/new-location\.ts/i)).toBeInTheDocument()
    })
  })
})

describe('SessionPage header and states', () => {
  function makeMockSession() {
    return {
      id: 'session-123',
      acpSessionId: 'acp-123',
      executionId: 'exec-123',
      taskDescription: 'Test task',
      status: 'running',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: null,
      model: 'claude-3-5-sonnet',
      coderType: null,
      stage: 'build',
      title: 'Test Session',
    }
  }

  function makeMockMetadata(overrides: Partial<SessionMetadata> = {}): SessionMetadata {
    return {
      sessionId: 'session-123',
      coderSessionId: 'coder-session-123',
      issueId: 'issue-1',
      acpSessionId: 'acp-123',
      executionId: 'exec-123',
      title: 'Test Session',
      status: 'running',
      statusKind: 'live',
      model: 'claude-3-5-sonnet',
      stage: 'build',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: null,
      lastActivityAt: '2024-01-01T10:05:00.000Z',
      eventCount: 10,
      toolCount: 5,
      turnCount: 2,
      ...overrides,
    }
  }

  function makeMockDetail(overrides: Partial<{ metadata: SessionMetadata; turns: SessionTurn[]; incomplete: boolean; status: string; completedAt: string | null }> = {}): CoderSessionDetail {
    return {
      id: 'session-123',
      acpSessionId: 'acp-123',
      executionId: 'exec-123',
      taskDescription: 'Test task',
      status: 'running',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: null,
      model: 'claude-3-5-sonnet',
      coderType: null,
      stage: 'build',
      title: 'Test Session',
      metadata: makeMockMetadata(),
      turns: [],
      incomplete: false,
      ...overrides,
    }
  }

  function setupSessionPage({
    sessions = [makeMockSession()],
    issue = null,
    detail = makeMockDetail(),
    sessionsLoading = false,
    detailError = null,
    detailPending = false,
  }: {
    sessions?: any[]
    issue?: any
    detail?: CoderSessionDetail | null
    sessionsLoading?: boolean
    detailError?: Error | null
    detailPending?: boolean
  } = {}) {
    sessionPageMocks.sessions = sessions
    sessionPageMocks.issue = issue
    sessionPageMocks.detail = detail
    sessionPageMocks.sessionsLoading = sessionsLoading
    sessionPageMocks.detailError = detailError
    sessionPageMocks.detailPending = detailPending
  }

  describe('header displays session metadata', () => {
    it('shows issue link, stage, model, turn count, last activity, and status badge', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          stage: 'build',
          model: 'claude-3-5-sonnet',
          turnCount: 3,
          lastActivityAt: '2024-01-01T10:05:00.000Z',
          statusKind: 'live',
        }),
      })
      setupSessionPage({ detail, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Issue #123')).toBeInTheDocument()
      })
      expect(screen.getByText('Test Issue')).toBeInTheDocument()
      expect(screen.getByText('Build')).toBeInTheDocument()
      expect(screen.getByText('claude-3-5-sonnet')).toBeInTheDocument()
      expect(screen.getByText('3 turns')).toBeInTheDocument()
      expect(screen.getByText('Live')).toBeInTheDocument()
    })

    it('shows changed-files summary in header when metadata has changedFiles', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          statusKind: 'completed',
          changedFiles: [
            { path: 'src/index.ts', operation: 'modified', additions: 10, deletions: 2 },
            { path: 'src/new.ts', operation: 'created', additions: 5 },
          ],
        }),
      })
      setupSessionPage({ detail, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('2 files changed')).toBeInTheDocument()
      })
    })

    it('shows duration for completed sessions', async () => {
      const sessions = [{
        ...makeMockSession(),
        status: 'completed',
        completedAt: '2024-01-01T10:30:00.000Z',
      }]
      const detail = makeMockDetail({
        status: 'completed',
        completedAt: '2024-01-01T10:30:00.000Z',
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
          completedAt: '2024-01-01T10:30:00.000Z',
          createdAt: '2024-01-01T10:00:00.000Z',
        }),
      })
      setupSessionPage({ sessions, detail, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Completed')).toBeInTheDocument()
      })
      expect(screen.getByText('30m 00s')).toBeInTheDocument()
    })

    it('does not show duration for running sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          statusKind: 'live',
          completedAt: null,
        }),
      })
      setupSessionPage({ detail, issue: { number: 123, title: 'Test Issue' } })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Live')).toBeInTheDocument()
      })
      const headerEl = screen.getByText('Live').closest('.border-b')
      expect(headerEl?.textContent).not.toMatch(/duration/i)
    })
  })

  describe('status kind display', () => {
    it('shows live status badge for running sessions with recent activity', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          lastActivityAt: new Date().toISOString(),
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Live')).toBeInTheDocument()
      })
    })

    it('shows stale status badge for running sessions with old activity', async () => {
      const fiveMinutesAgo = new Date(Date.now() - 5 * 60 * 1000).toISOString()
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'stale',
          lastActivityAt: fiveMinutesAgo,
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Stale')).toBeInTheDocument()
      })
    })

    it('shows finalizing status badge when session is finalizing', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'finalizing',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Finalizing')).toBeInTheDocument()
      })
    })

    it('shows failed status badge for failed sessions', async () => {
      const sessions = [{
        ...makeMockSession(),
        status: 'failed',
        completedAt: '2024-01-01T10:30:00.000Z',
      }]
      const detail = makeMockDetail({
        status: 'failed',
        completedAt: '2024-01-01T10:30:00.000Z',
        metadata: makeMockMetadata({
          status: 'failed',
          statusKind: 'failed',
          completedAt: '2024-01-01T10:30:00.000Z',
          createdAt: '2024-01-01T10:00:00.000Z',
        }),
      })
      setupSessionPage({ sessions, detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Failed')).toBeInTheDocument()
      })
    })
  })

  describe('loading and error state rendering', () => {
    it('shows loading state while sessions are loading', async () => {
      setupSessionPage({ sessions: [], sessionsLoading: true })

      renderWithQueryClient(<SessionPage />)

      expect(screen.getByText('Loading session...')).toBeInTheDocument()
    })

    it('shows loading state while detail is loading', async () => {
      setupSessionPage({ detailPending: true })

      renderWithQueryClient(<SessionPage />)

      expect(screen.getByText('Loading session...')).toBeInTheDocument()
    })

    it('shows API error state when detail query fails', async () => {
      setupSessionPage({ detail: null, detailError: new Error('API Error') })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Failed to load session')).toBeInTheDocument()
      })
      expect(screen.getByText(/An error occurred while fetching session data/i)).toBeInTheDocument()
    })

    it('shows waiting for activity state when session is running but no turns yet', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({ statusKind: 'live' }),
        turns: [],
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Waiting for activity...')).toBeInTheDocument()
      })
    })

    it('shows empty state when session has no recorded activity', async () => {
      const sessions = [{
        ...makeMockSession(),
        status: 'completed',
      }]
      const detail = makeMockDetail({
        status: 'completed',
        turns: [],
        metadata: makeMockMetadata({ statusKind: 'completed' }),
      })
      setupSessionPage({ sessions, detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('No activity recorded for this session')).toBeInTheDocument()
      })
    })

    it('shows incomplete/legacy state when session has incomplete flag and no turns', async () => {
      const sessions = [{
        ...makeMockSession(),
        status: 'completed',
      }]
      const detail = makeMockDetail({
        status: 'completed',
        turns: [],
        incomplete: true,
        metadata: makeMockMetadata({ statusKind: 'completed' }),
      })
      setupSessionPage({ sessions, detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Incomplete Session Data')).toBeInTheDocument()
      })
      expect(screen.getByText(/Prompt was not recorded/i)).toBeInTheDocument()
    })
  })
})

describe('Live/historical parity', () => {
  it('renders turns with Coder label when assistant parts exist', async () => {
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
  })

  it('renders live tool with normalized name and display title', async () => {
    const turns = [makeTurn({
      assistant: [{
        id: 'tool-1',
        type: 'tool',
        tool: {
          toolCallId: 'tc-1',
          normalizedName: 'read',
          displayTitle: 'Read',
          toolName: 'Read',
          status: 'completed',
          input: '{"file_path":"src/index.ts"}',
          output: 'file content',
          startedAt: '2024-01-01T10:00:02.000Z',
          completedAt: '2024-01-01T10:00:03.000Z',
        },
      } as ToolPart],
    })]

    renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

    await waitFor(() => {
      expect(screen.getByText('Read')).toBeInTheDocument()
    })
  })

  it('shows Jump to bottom button when new content available and not near bottom', async () => {
    const turns = [makeTurn({
      user: {
        role: 'mohist',
        text: 'Test prompt',
        kind: 'task',
        sentAt: '2024-01-01T10:00:00.000Z',
      },
      assistant: [{
        id: 'text-1',
        type: 'text',
        text: 'Initial text',
        startedAt: '2024-01-01T10:00:01.000Z',
        completedAt: null,
      } as TextPart],
    })]

    renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={true} />)

    await waitFor(() => {
      expect(screen.getByText('Coder')).toBeInTheDocument()
    })
  })

  it('displays recovery error part with message for recovery events', async () => {
    const turns = [makeTurn({
      assistant: [{
        id: 'error-1',
        type: 'error',
        message: 'Recovery detected',
        kind: 'recovery',
        at: '2024-01-01T10:00:05.000Z',
      } as ErrorPart],
    })]

    renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

    await waitFor(() => {
      expect(screen.getByText(/Recovery detected/i)).toBeInTheDocument()
    })
  })

  it('displays terminal error parts for failed sessions', async () => {
    const turns = [makeTurn({
      assistant: [{
        id: 'error-1',
        type: 'error',
        message: 'Execution failed',
        kind: 'failed',
        at: '2024-01-01T10:00:05.000Z',
      } as ErrorPart],
    })]

    renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

    await waitFor(() => {
      expect(screen.getByText(/Execution failed/i)).toBeInTheDocument()
    })
  })

  it('marks live transcript finalizing after completion SSE until refetch', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHook(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    expect(result.current.isFinalizing).toBe(false)

    act(() => {
      dispatchAgentEvent('coder_session_completed', {
        issueId: '123',
        projectId: 'project-1',
        coderSessionId: 'session-123',
        status: 'completed',
        duration: 1000,
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })
  })

  it('does not mark live transcript finalizing for ordinary text chunks', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHook(() => useSessionTranscript({
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
        text: 'streaming text',
        coderSessionId: 'session-123',
      })
    })

    await waitFor(() => {
      expect(result.current.turns.at(-1)?.assistant.some((part) => part.type === 'text')).toBe(true)
    })
    expect(result.current.isFinalizing).toBe(false)
  })

  it('renders unknown tool when tool name is not recognized', async () => {
    const turns = [makeTurn({
      assistant: [{
        id: 'tool-1',
        type: 'tool',
        tool: {
          toolCallId: 'tc-unknown',
          toolName: 'UnknownTool',
          status: 'completed',
          input: '{"arg1":"value1"}',
          output: '{"result":"ok"}',
          startedAt: '2024-01-01T10:00:02.000Z',
          completedAt: '2024-01-01T10:00:03.000Z',
        },
      } as ToolPart],
    })]

    renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

    await waitFor(() => {
      expect(screen.getByText('UnknownTool')).toBeInTheDocument()
    })
  })
})

describe('SessionHeader navigation', () => {
  it('session header link routes to /issue/:number/session/:sessionId', async () => {
    const { SessionHeader } = await import('../src/components/SessionHeader')
    const session = {
      id: 'session-abc',
      acpSessionId: 'acp-123',
      executionId: null,
      taskDescription: 'Test task',
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T10:30:00.000Z',
      model: 'claude-3-5-sonnet',
      coderType: null,
      stage: 'build',
      title: 'T-001',
      workflowLogs: [],
    }

    renderWithQueryClient(<SessionHeader session={session} issueNumber={42} />)

    const link = screen.getByRole('link')
    expect(link.getAttribute('href')).toBe('/issue/42/session/session-abc')
  })

  it('session header shows session label', async () => {
    const { SessionHeader } = await import('../src/components/SessionHeader')
    const session = {
      id: 'session-abc',
      acpSessionId: 'acp-123',
      executionId: null,
      taskDescription: 'Implement the feature',
      status: 'running',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: null,
      model: 'claude-3-5-sonnet',
      coderType: null,
      stage: 'build',
      title: null,
      workflowLogs: [],
    }

    renderWithQueryClient(<SessionHeader session={session} issueNumber={42} />)

    expect(screen.getByText(/Implement the feature/)).toBeInTheDocument()
  })

  it('showTranscriptLink renders View transcript link instead of full row link', async () => {
    const { SessionHeader } = await import('../src/components/SessionHeader')
    const session = {
      id: 'session-abc',
      acpSessionId: 'acp-123',
      executionId: null,
      taskDescription: 'Test task',
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T10:30:00.000Z',
      model: 'claude-3-5-sonnet',
      coderType: null,
      stage: 'build',
      title: 'T-001',
      workflowLogs: [],
    }

    renderWithQueryClient(<SessionHeader session={session} issueNumber={42} showTranscriptLink />)

    const link = screen.getByRole('link')
    expect(link.getAttribute('href')).toBe('/issue/42/session/session-abc')
    expect(screen.getByText('View transcript')).toBeInTheDocument()
  })

  it('getSessionLabel returns title when present', async () => {
    const { getSessionLabel } = await import('../src/components/SessionHeader')
    const session = { id: 's1', title: 'T-001 My Task', executionId: null, stage: null, taskDescription: null, status: 'completed', createdAt: '', completedAt: null, model: null, coderType: null, acpSessionId: '', workflowLogs: [] } as any
    expect(getSessionLabel(session)).toBe('T-001 My Task')
  })

  it('getSessionLabel extracts T-N pattern from executionId', async () => {
    const { getSessionLabel } = await import('../src/components/SessionHeader')
    const session = { id: 's1', title: null, executionId: 'build-T-042-description', stage: null, taskDescription: null, status: 'completed', createdAt: '', completedAt: null, model: null, coderType: null, acpSessionId: '', workflowLogs: [] } as any
    expect(getSessionLabel(session)).toBe('T-042')
  })

  it('getSessionLabel uses stage label when no title or executionId', async () => {
    const { getSessionLabel } = await import('../src/components/SessionHeader')
    const session = { id: 's1', title: null, executionId: null, stage: 'plan', taskDescription: null, status: 'completed', createdAt: '', completedAt: null, model: null, coderType: null, acpSessionId: '', workflowLogs: [] } as any
    expect(getSessionLabel(session)).toBe('Plan')
  })

  it('getSessionLabel falls back to taskDescription', async () => {
    const { getSessionLabel } = await import('../src/components/SessionHeader')
    const session = { id: 's1', title: null, executionId: null, stage: null, taskDescription: 'Do something important', status: 'completed', createdAt: '', completedAt: null, model: null, coderType: null, acpSessionId: '', workflowLogs: [] } as any
    expect(getSessionLabel(session)).toBe('Do something important')
  })

  it('getSessionLabel defaults to Session', async () => {
    const { getSessionLabel } = await import('../src/components/SessionHeader')
    const session = { id: 's1', title: null, executionId: null, stage: null, taskDescription: null, status: 'completed', createdAt: '', completedAt: null, model: null, coderType: null, acpSessionId: '', workflowLogs: [] } as any
    expect(getSessionLabel(session)).toBe('Session')
  })
})
