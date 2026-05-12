import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { baseRender, screen, fireEvent, waitFor } from './test-utils'
import { SessionPage } from '../src/components/SessionPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import type { CoderSessionDetail, SessionMetadata, SessionTurn, TextPart, ToolPart } from '../src/lib/types'

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

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = createMockQueryClient()
  queryClients.push(queryClient)
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  )
}

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

describe('SessionPage centered transcript layout', () => {
  describe('sticky session title', () => {
    it('shows sticky title with session title and turn count', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          title: 'Build UI Components',
          status: 'completed',
          statusKind: 'completed',
          turnCount: 5,
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Build UI Components')).toBeInTheDocument()
      })
      expect(screen.getByText('5 turns')).toBeInTheDocument()
    })

    it('shows live indicator when session is running', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          title: 'Running Session',
          status: 'running',
          statusKind: 'live',
          turnCount: 1,
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Running Session')).toBeInTheDocument()
      })
      expect(screen.getByText('Running')).toBeInTheDocument()
    })

    it('shows finalizing status when session is finalizing', async () => {
      const detail = makeMockDetail({
        metadata: makeMockMetadata({
          title: 'Finalizing Session',
          status: 'running',
          statusKind: 'finalizing',
          completedAt: '2024-01-01T11:00:00.000Z',
          turnCount: 2,
        }),
      })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Finalizing Session')).toBeInTheDocument()
      })
      expect(screen.getByText('Finalizing')).toBeInTheDocument()
    })
  })

  describe('prompt block rendering', () => {
    it('renders prompt as right-aligned low-saturation block', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Build the new feature',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          summary: {
            kind: 'task',
            title: 'Build the new feature',
          },
        },
        assistant: [],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Build the new feature')).toBeInTheDocument()
      })
      expect(screen.getByText('Task')).toBeInTheDocument()
    })

    it('renders prompt with collapsed raw content and expand button', async () => {
      const longPrompt = '<mohist-task><role>Implement</role><contract>details</contract></mohist-task>'
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: longPrompt,
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          summary: {
            kind: 'task',
            title: 'Implement feature',
          },
        },
        assistant: [],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Implement feature')).toBeInTheDocument()
      })
      expect(screen.getByText('Show full prompt')).toBeInTheDocument()
    })

    it('has copy button for prompt text', async () => {
      const mockWriteText = vi.fn().mockResolvedValue(undefined)
      Object.defineProperty(navigator, 'clipboard', {
        value: { writeText: mockWriteText },
        configurable: true,
      })

      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Copy me please',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
          summary: {
            kind: 'task',
            title: 'Copy test',
          },
        },
        assistant: [],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Copy test')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('Copy'))

      await waitFor(() => {
        expect(mockWriteText).toHaveBeenCalledWith('Copy me please')
      })
    })
  })

  describe('assistant parts rendering', () => {
    it('renders text parts in order', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Hello',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'text-1',
            type: 'text',
            text: 'Hello! How can I help?',
            startedAt: '2024-01-01T10:00:01.000Z',
            completedAt: '2024-01-01T10:00:02.000Z',
          } as TextPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('Hello! How can I help?')).toBeInTheDocument()
      })
    })

    it('renders reasoning as collapsible section', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Think about this',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'reasoning-1',
            type: 'reasoning',
            text: 'My thinking process here...'.repeat(50),
            startedAt: '2024-01-01T10:00:01.000Z',
            completedAt: '2024-01-01T10:00:02.000Z',
          },
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/Thinking\.\.\./i)).toBeInTheDocument()
      })
    })
  })

  describe('empty and error states', () => {
    it('shows transcript-empty state when no turns and not running', async () => {
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
        expect(screen.getByText(/No activity recorded/i)).toBeInTheDocument()
      })
    })

    it('shows waiting state when no turns and running', async () => {
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
        expect(screen.getByText(/Waiting for activity/i)).toBeInTheDocument()
      })
    })

    it('shows loading state while detail is loading', async () => {
      setupSessionPage({ detailPending: true })

      renderWithQueryClient(<SessionPage />)

      expect(screen.getByText(/loading/i)).toBeInTheDocument()
    })

    it('shows error state when API fails', async () => {
      setupSessionPage({ detailError: new Error('API Error') })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/error/i)).toBeInTheDocument()
      })
    })
  })

  describe('compact tool rendering', () => {
    it('renders read tool as compact row with icon and file path', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Read the file',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'read',
              displayTitle: 'src/index.ts',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/index.ts"}',
              output: 'file content',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('src/index.ts')).toBeInTheDocument()
      })
    })

    it('renders glob tool with pattern subtitle', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Find files',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'glob',
              displayTitle: '**/*.ts',
              toolName: 'glob',
              status: 'completed',
              input: '{"pattern":"**/*.ts"}',
              output: 'files',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('**/*.ts')).toBeInTheDocument()
      })
    })

    it('renders grep tool with query subtitle', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Search',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'grep',
              displayTitle: 'function foo',
              toolName: 'grep',
              status: 'completed',
              input: '{"pattern":"function foo"}',
              output: 'matches',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('function foo')).toBeInTheDocument()
      })
    })

    it('renders bash tool with command subtitle', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Run command',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'bash',
              displayTitle: 'npm run build',
              toolName: 'bash',
              status: 'completed',
              input: '{"command":"npm run build"}',
              output: 'done',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('npm run build')).toBeInTheDocument()
      })
    })

    it('renders webfetch tool with url subtitle', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Fetch url',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'webfetch',
              displayTitle: 'https://example.com',
              toolName: 'webfetch',
              status: 'completed',
              input: '{"url":"https://example.com"}',
              output: 'response',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('https://example.com')).toBeInTheDocument()
      })
    })

    it('renders edit tool showing file name when no displayTitle', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Edit file',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'edit',
              toolName: 'edit',
              status: 'completed',
              input: '{"file_path":"src/app.ts","old_string":"old","new_string":"new"}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('src/app.ts')).toBeInTheDocument()
      })
    })

    it('renders write tool showing file name', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Write file',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'write',
              toolName: 'write',
              status: 'completed',
              input: '{"path":"src/new.ts"}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('src/new.ts')).toBeInTheDocument()
      })
    })

    it('renders apply_patch showing changed file count', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Apply patch',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'apply_patch',
              toolName: 'apply_patch',
              status: 'completed',
              input: '{"patchText":"*** Update File: src/app.ts\\n--- a/src/app.ts\\n+++ b/src/app.ts\\n@@ -1 +1 @@\\n-old\\n+new"}',
              changedFiles: [
                { path: 'src/app.ts', operation: 'modified', additions: 1, deletions: 1 },
              ],
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
    })

    it('renders generic unknown tool without Called unknown prefix', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Test unknown tool',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              toolName: 'SomeUnknown',
              status: 'completed',
              input: '{"arg":"value"}',
              output: 'result',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.queryByText(/Called Unknown/i)).not.toBeInTheDocument()
      })
    })

    it('expands completed tool to show input/output', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Test expand',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'read',
              displayTitle: 'src/test.ts',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/test.ts"}',
              output: 'file content here',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('src/test.ts')).toBeInTheDocument()
      })

      const row = screen.getAllByText('src/test.ts').at(-1)?.closest('button')
      expect(row).not.toBeNull()
      fireEvent.click(row!)

      const nestedRow = screen.getAllByText('src/test.ts').at(-1)?.closest('button')
      expect(nestedRow).not.toBeNull()
      fireEvent.click(nestedRow!)

      await waitFor(() => {
        expect(screen.getByText('Input')).toBeInTheDocument()
      })
    })

    it('does not expand running or pending tools by default', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: null,
        user: {
          role: 'mohist',
          text: 'Test running',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'read',
              displayTitle: 'src/running.ts',
              toolName: 'read',
              status: 'running',
              input: '{"file_path":"src/running.ts"}',
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: null,
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('src/running.ts')).toBeInTheDocument()
      })
      expect(screen.queryByText('Input')).not.toBeInTheDocument()
    })
  })

  describe('grouped context rows', () => {
    it('groups consecutive read and grep tools into context gathering group', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Research',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'read',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/a.ts"}',
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
              normalizedName: 'grep',
              toolName: 'grep',
              status: 'completed',
              input: '{"pattern":"foo"}',
              output: 'matches',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/Gathering context/)).toBeInTheDocument()
      })
    })

    it('expands context group to show individual tool rows', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Research',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'read',
              toolName: 'read',
              status: 'completed',
              input: '{"file_path":"src/a.ts"}',
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
              normalizedName: 'glob',
              toolName: 'glob',
              status: 'completed',
              input: '{"pattern":"**/*.ts"}',
              output: 'files',
              startedAt: '2024-01-01T10:00:04.000Z',
              completedAt: '2024-01-01T10:00:05.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText(/Gathering context/)).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText(/Gathering context/))

      await waitFor(() => {
        expect(screen.getByText('src/a.ts')).toBeInTheDocument()
      })
    })
  })

  describe('per-file diff summaries', () => {
    it('shows turn-level changed files summary', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Make changes',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'edit',
              toolName: 'edit',
              status: 'completed',
              input: '{"file_path":"src/app.ts"}',
              changedFiles: [
                { path: 'src/app.ts', operation: 'modified', additions: 10, deletions: 3 },
              ],
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })
    })

    it('expands diff section to show per-file details', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Make changes',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'edit',
              toolName: 'edit',
              status: 'completed',
              input: '{"file_path":"src/app.ts"}',
              changedFiles: [
                { path: 'src/app.ts', operation: 'modified', additions: 10, deletions: 3 },
              ],
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('1 file changed'))

      await waitFor(() => {
        expect(screen.getAllByText('src/app.ts').length).toBeGreaterThan(0)
      })

      expect(screen.getByText('+10')).toBeInTheDocument()
      expect(screen.getByText('-3')).toBeInTheDocument()
    })

    it('shows additions and deletions in diff', async () => {
      const turns: SessionTurn[] = [{
        id: 'turn-1',
        startedAt: '2024-01-01T10:00:00.000Z',
        completedAt: '2024-01-01T10:01:00.000Z',
        user: {
          role: 'mohist',
          text: 'Make changes',
          kind: 'task',
          sentAt: '2024-01-01T10:00:00.000Z',
        },
        assistant: [
          {
            id: 'tool-1',
            type: 'tool',
            tool: {
              toolCallId: 'tc-1',
              normalizedName: 'edit',
              toolName: 'edit',
              status: 'completed',
              input: '{"file_path":"src/app.ts"}',
              changedFiles: [
                { path: 'src/app.ts', operation: 'modified', additions: 10, deletions: 3 },
              ],
              startedAt: '2024-01-01T10:00:02.000Z',
              completedAt: '2024-01-01T10:00:03.000Z',
            },
          } as ToolPart,
        ],
      }]

      const detail = makeMockDetail({ turns })
      setupSessionPage({ detail })

      renderWithQueryClient(<SessionPage />)

      await waitFor(() => {
        expect(screen.getByText('1 file changed')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByText('1 file changed'))

      await waitFor(() => {
        expect(screen.getByText('+10')).toBeInTheDocument()
        expect(screen.getByText('-3')).toBeInTheDocument()
      })
    })
  })
})
