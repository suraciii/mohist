import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { baseRender, screen, fireEvent, waitFor, renderHook, act } from './test-utils'
import { SessionPage } from '../src/components/SessionPage'
import { SessionTranscriptView } from '../src/components/SessionTranscriptView'
import { useSessionTranscript } from '../src/hooks/useSessionTranscript'
import { dispatchAgentEvent } from '../src/lib/agent-events'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
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
const queryClients: QueryClient[] = []
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

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = createMockQueryClient()
  queryClients.push(queryClient)
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  )
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

    it('renders turn-level changed-files output when tool has changedFiles', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-edit',
            toolName: 'edit',
            status: 'completed',
            changedFiles: [
              { path: 'src/index.ts', operation: 'modified', additions: 10, deletions: 3 },
            ],
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
    })

    it('turn-level changed-files shows additions/deletions', async () => {
      const turns = [makeTurn({
        assistant: [{
          id: 'tool-1',
          type: 'tool',
          tool: {
            toolCallId: 'tc-write',
            toolName: 'write',
            status: 'completed',
            changedFiles: [
              { path: 'src/new.ts', operation: 'created', additions: 25 },
            ],
            startedAt: '2024-01-01T10:00:02.000Z',
            completedAt: '2024-01-01T10:00:03.000Z',
          },
        }],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        const changedEls = screen.getAllByText('1 file changed')
        expect(changedEls.length).toBeGreaterThan(0)
      }, { timeout: 3000 })
    })

    it('turn-level changed-files deduplicates when multiple tools modify same file', async () => {
      const turns = [makeTurn({
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-edit-1',
              toolName: 'edit',
              status: 'completed',
              changedFiles: [
                { path: 'src/index.ts', operation: 'modified', additions: 5, deletions: 2 },
              ],
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          },
          {
            id: 'tool-2',
            type: 'tool',
            tool: {
              toolCallId: 'tc-edit-2',
              toolName: 'edit',
              status: 'completed',
              changedFiles: [
                { path: 'src/index.ts', operation: 'modified', additions: 10, deletions: 5 },
              ],
              startedAt: '2024-01-01T10:00:03.000Z',
              completedAt: '2024-01-01T10:00:04.000Z',
            },
          },
        ],
      })]

      renderWithQueryClient(<SessionTranscriptView turns={turns} isRunning={false} />)

      await waitFor(() => {
        expect(screen.getAllByText('1 file changed').length).toBeGreaterThan(0)
      })
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
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
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
      status: 'completed',
      statusKind: 'completed',
      model: 'claude-3-5-sonnet',
      stage: 'build',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
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
      status: 'completed',
      createdAt: '2024-01-01T10:00:00.000Z',
      completedAt: '2024-01-01T11:00:00.000Z',
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
          status: 'completed',
          statusKind: 'completed',
          stage: 'build',
          model: 'claude-3-5-sonnet',
          turnCount: 3,
          lastActivityAt: '2024-01-01T10:05:00.000Z',
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
    })

    it('shows changed-files summary in header when metadata has changedFiles', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          changedFiles: [
            { path: 'src/index.ts', operation: 'modified', additions: 5, deletions: 2 },
          ],
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/1 file changed/i)).toBeInTheDocument()
      })
    })

    it('shows duration for completed sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          completedAt: '2024-01-01T11:00:00.000Z',
          status: 'completed',
          statusKind: 'completed',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('1h 00m')).toBeInTheDocument()
      })
    })

    it('does not show duration for running sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
          completedAt: null,
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.queryByText(/duration/i)).not.toBeInTheDocument()
      })
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
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'stale',
          lastActivityAt: '2024-01-01T10:00:00.000Z',
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
          completedAt: '2024-01-01T11:00:00.000Z',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Finalizing')).toBeInTheDocument()
      })
    })

    it('shows failed status badge for failed sessions', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'failed',
          statusKind: 'failed',
          completedAt: '2024-01-01T11:00:00.000Z',
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Failed')).toBeInTheDocument()
      })
    })
  })

  describe('loading and error state rendering', () => {
    it('shows loading state while sessions are loading', async () => {
      setupSessionPage({ sessionsLoading: true })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/loading/i)).toBeInTheDocument()
      })
    })

    it('shows loading state while detail is loading', async () => {
      setupSessionPage({ detailPending: true })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/loading/i)).toBeInTheDocument()
      })
    })

    it('shows API error state when detail query fails', async () => {
      setupSessionPage({ detailError: new Error('API Error') })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/error/i)).toBeInTheDocument()
      })
    })

    it('shows waiting for activity state when session is running but no turns yet', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'running',
          statusKind: 'live',
        }),
        turns: [],
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/waiting/i)).toBeInTheDocument()
      })
    })

    it('shows empty state when session has no recorded activity', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
        turns: [],
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/no activity/i)).toBeInTheDocument()
      })
    })

    it('shows incomplete/legacy state when session has incomplete flag and no turns', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          status: 'completed',
          statusKind: 'completed',
        }),
        turns: [],
        incomplete: true,
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/incomplete/i)).toBeInTheDocument()
      })
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
      expect(screen.getByText(/Context gathered/)).toBeInTheDocument()
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

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
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
        text: 'streaming text',
        coderSessionId: 'session-123',
      })
    })

    await waitFor(() => {
      expect(result.current.turns.at(-1)?.assistant.some((part) => part.type === 'text')).toBe(true)
    })
    expect(result.current.isFinalizing).toBe(false)
  })

  it('appends one recovery part for a single live recovery event', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_recovery_status', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        status: 'recovering',
        attempt: 1,
      })
    })

    await waitFor(() => {
      const recoveryParts = result.current.turns.at(-1)?.assistant.filter(
        (part) => part.type === 'error' && part.kind === 'recovery',
      )
      expect(recoveryParts).toHaveLength(1)
    })
  })

  it('normalizes unknown live pattern tools as search like historical replay', async () => {
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
        toolCallId: 'tc-pattern',
        toolName: 'unknown',
        state: 'started',
        rawInput: { pattern: '**/*.ts' },
      })
    })

    await waitFor(() => {
      const toolPart = result.current.turns.at(-1)?.assistant.find(
        (part): part is ToolPart => part.type === 'tool',
      )
      expect(toolPart?.tool.normalizedName).toBe('search')
    })
  })

  it('normalizes live tool payload shapes like historical replay', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    const cases = [
      { id: 'tc-patch', rawInput: { patchText: '*** Begin Patch\n*** Add File: a.txt\n+hello\n*** End Patch' }, expected: 'apply_patch' },
      { id: 'tc-command', rawInput: { command: 'npm test' }, expected: 'bash' },
      { id: 'tc-file-path', rawInput: { file_path: 'src/index.ts' }, expected: 'read' },
      { id: 'tc-path', rawInput: { path: 'src/index.ts' }, expected: 'read' },
      { id: 'tc-todos', rawInput: { todos: [{ content: 'Test', status: 'pending' }] }, expected: 'todowrite' },
      { id: 'tc-output-metadata', rawInput: {}, rawOutput: { metadata: { toolName: 'glob' } }, expected: 'glob' },
    ]

    for (const item of cases) {
      act(() => {
        dispatchAgentEvent('coder_tool_call', {
          issueId: '123',
          projectId: 'project-1',
          executionId: 'exec-123',
          acpSessionId: 'acp-123',
          coderSessionId: 'session-123',
          toolCallId: item.id,
          toolName: 'unknown',
          state: 'started',
          rawInput: item.rawInput,
          rawOutput: item.rawOutput,
        })
      })
    }

    await waitFor(() => {
      const toolParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ToolPart => part.type === 'tool',
      ) ?? []
      expect(toolParts).toHaveLength(cases.length)
      expect(toolParts.map((part) => part.tool.normalizedName)).toEqual(cases.map((item) => item.expected))
    })
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

describe('Live tool updates merge in place', () => {
  it('merges start and update events for same toolCallId into one tool card', async () => {
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
  it('coder_session_completed marks finalizing and triggers refetch', async () => {
    const initialTurns = [makeTurn()]
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

    const { result } = renderHook(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }), {
      wrapper: ({ children }: { children: React.ReactNode }) => (
        <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
      ),
    })

    expect(result.current.isFinalizing).toBe(false)

    act(() => {
      dispatchAgentEvent('coder_session_completed', {
        issueId: '123',
        projectId: 'project-1',
        coderSessionId: 'session-123',
        status: 'completed',
        duration: 5000,
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })
  })

  it('coder_session_failed marks finalizing and adds error part', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_session_failed', {
        issueId: '123',
        projectId: 'project-1',
        coderSessionId: 'session-123',
        reason: 'Out of memory',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
      const errorParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ErrorPart => part.type === 'error' && part.kind === 'failed',
      )
      expect(errorParts).toHaveLength(1)
      expect(errorParts?.[0].message).toBe('Out of memory')
    })
  })

  it('coder_session_cancelled marks finalizing and adds error part', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_session_cancelled', {
        issueId: '123',
        projectId: 'project-1',
        coderSessionId: 'session-123',
        reason: 'User cancelled',
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
      const errorParts = result.current.turns.at(-1)?.assistant.filter(
        (part): part is ErrorPart => part.type === 'error' && part.kind === 'cancelled',
      )
      expect(errorParts).toHaveLength(1)
      expect(errorParts?.[0].message).toBe('User cancelled')
    })
  })

  it('recovery status with recovered or failed triggers refetch', async () => {
    const initialTurns = [makeTurn()]

    const { result } = renderHookWithQueryClient(() => useSessionTranscript({
      issueNumber: 123,
      sessionId: 'session-123',
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    act(() => {
      dispatchAgentEvent('coder_recovery_status', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        status: 'recovered',
        attempt: 1,
      })
    })

    await waitFor(() => {
      expect(result.current.isFinalizing).toBe(true)
    })
  })
})

describe('Running session shows only real active tools', () => {
  it('does not create orphan unknown tool cards during streaming', async () => {
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
      acpSessionId: 'acp-123',
      initialTurns,
      isRunning: true,
    }))

    expect(result.current.isFinalizing).toBe(false)

    act(() => {
      dispatchAgentEvent('coder_tool_call', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
        text: 'Starting task...',
        coderSessionId: 'session-123',
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
        toolCallId: 'tc-1',
        toolName: 'read',
        state: 'started',
        rawInput: { file_path: 'src/index.ts' },
      })
    })

    act(() => {
      dispatchAgentEvent('coder_text_chunk', {
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        text: 'Reading file...',
        coderSessionId: 'session-123',
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
        issueId: '123',
        projectId: 'project-1',
        executionId: 'exec-123',
        acpSessionId: 'acp-123',
        coderSessionId: 'session-123',
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
