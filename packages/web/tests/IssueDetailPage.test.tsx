// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { baseRender, screen, fireEvent, waitFor } from './test-utils'
import { IssueDetailPage } from '../src/pages/issue-detail/ui/IssueDetailPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import { IssueStatus, Stage } from '../src/entities/issue'

const mocks = vi.hoisted(() => {
  return {
    issue: null as any,
    agentStatus: null as any,
    params: { number: '1' },
    navigate: vi.fn(),
    addCommentMutation: {
      mutate: vi.fn(),
      mutateAsync: vi.fn(),
      isPending: false,
      error: null,
    },
    deleteCommentMutation: {
      mutate: vi.fn(),
      mutateAsync: vi.fn(),
      isPending: false,
      error: null,
    },
  }
})

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom')
  return {
    ...actual,
    useParams: () => mocks.params,
    useNavigate: () => mocks.navigate,
  }
})

vi.mock('../src/entities/issue/api/queries', async () => {
  const actual = await vi.importActual<typeof import('../src/entities/issue/api/queries')>('../src/entities/issue/api/queries')
  return {
    ...actual,
    useIssue: () => ({ data: mocks.issue, isLoading: !mocks.issue, isError: false }),
    useAgentStatus: () => ({ data: mocks.agentStatus }),
    useIssueDiff: () => ({ data: null }),
    useIssueCommits: () => ({ data: null }),
    useIssueExecutions: () => ({ data: [] as any[] }),
    useWorktreeStatus: () => ({ data: null }),
    useIssueStageState: () => ({ data: null }),
    useWorkflowRun: () => ({ data: null }),
  }
})

vi.mock('../src/entities/issue/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/entities/issue/api/client')>()),
  startIssue: vi.fn(() => Promise.resolve()),
  closeIssue: vi.fn(() => Promise.resolve()),
  forceStopIssue: vi.fn(() => Promise.resolve()),
  reopenIssue: vi.fn(() => Promise.resolve()),
  rerunIssue: vi.fn(() => Promise.resolve()),
  retryIssue: vi.fn(() => Promise.resolve()),
  addComment: vi.fn((_issueNumber, _body) => {
    mocks.addCommentMutation.mutate()
    return Promise.resolve()
  }),
  deleteComment: vi.fn((_issueNumber, _commentId) => {
    mocks.deleteCommentMutation.mutate()
    return Promise.resolve()
  }),
}))

let queryClient: QueryClient

let scrollHeightSpy: ReturnType<typeof vi.spyOn>

beforeEach(() => {
  vi.clearAllMocks()
  mocks.issue = null
  mocks.agentStatus = { activeAgents: [], maxConcurrentAgents: 3 }
  mocks.params = { number: '1' }
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  scrollHeightSpy = vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get')
  scrollHeightSpy.mockReturnValue(700)
})

afterEach(() => {
  queryClient.clear()
  scrollHeightSpy?.mockRestore()
})

function renderWithQueryClient(ui: React.ReactElement) {
  return baseRender(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  )
}

function makeIssue(overrides: any = {}) {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: '',
    stage: 'backlog',
    status: 'backlog',
    projectId: 'project-1',
    labels: [],
    createdAt: '2024-01-01T10:00:00.000Z',
    updatedAt: '2024-01-01T10:00:00.000Z',
    comments: [],
    ...overrides,
  }
}

describe('IssueDetailPage Markdown rendering', () => {
  describe('description Markdown', () => {
    it('renders Markdown headings in description', async () => {
      mocks.issue = makeIssue({ body: '# Heading\n\nSome content' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Heading')).toBeInTheDocument()
      })
    })

    it('renders Markdown lists in description', async () => {
      mocks.issue = makeIssue({ body: '- Item 1\n- Item 2\n- Item 3' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Item 1')).toBeInTheDocument()
        expect(screen.getByText('Item 2')).toBeInTheDocument()
        expect(screen.getByText('Item 3')).toBeInTheDocument()
      })
    })

    it('renders strikethrough in description', async () => {
      mocks.issue = makeIssue({ body: '~~deleted text~~' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('deleted text')).toBeInTheDocument()
      })
    })

    it('renders bare URL autolinks in description', async () => {
      mocks.issue = makeIssue({ body: 'Visit https://example.com for more' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const link = screen.getByRole('link', { name: /https:\/\/example\.com/i })
        expect(link).toBeInTheDocument()
        expect(link).toHaveAttribute('href', 'https://example.com')
      })
    })

    it('renders inline code with distinct styling in description', async () => {
      mocks.issue = makeIssue({ body: 'Use `const x = 1` for constants' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const code = screen.getByText('const x = 1')
        expect(code.tagName).toBe('CODE')
      })
    })

    it('renders fenced code blocks in description', async () => {
      mocks.issue = makeIssue({
        body: '```js\nconsole.log("hello")\n```',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('console.log("hello")')).toBeInTheDocument()
      })
    })

    it('renders ordered lists in description', async () => {
      mocks.issue = makeIssue({ body: '1. First\n2. Second\n3. Third' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('First')).toBeInTheDocument()
        expect(screen.getByText('Second')).toBeInTheDocument()
        expect(screen.getByText('Third')).toBeInTheDocument()
      })
    })

    it('renders emphasis and strong text in description', async () => {
      mocks.issue = makeIssue({ body: '**bold** and *italic*' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('bold')).toBeInTheDocument()
        expect(screen.getByText('italic')).toBeInTheDocument()
      })
    })

    it('renders blockquotes in description', async () => {
      mocks.issue = makeIssue({ body: '> This is a quote' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('This is a quote')).toBeInTheDocument()
      })
    })
  })

  describe('comment Markdown', () => {
    it('renders Markdown in comments', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: '# Comment Heading\n\n**Bold** text',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Comment Heading')).toBeInTheDocument()
        expect(screen.getByText('Bold')).toBeInTheDocument()
      })
    })

    it('renders inline code in comments', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: 'Use `code` in comments',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const code = screen.getByText('code')
        expect(code.tagName).toBe('CODE')
      })
    })

    it('renders fenced code blocks in comments', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: '```\nconst x = 1;\n```',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('const x = 1;')).toBeInTheDocument()
      })
    })

    it('shows comment timestamp alongside rendered Markdown', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: 'Comment with **formatting**',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText(/formatting/i)).toBeInTheDocument()
      })
    })

    it('shows delete button for each comment', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: 'Comment body',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Delete')).toBeInTheDocument()
      })
    })
  })

  describe('description expand/collapse', () => {
    it('shows Expand button for description by default', async () => {
      mocks.issue = makeIssue({
        body: 'Long description '.repeat(100),
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Expand')).toBeInTheDocument()
      })
    })

    it('shows Collapse button after expanding', async () => {
      mocks.issue = makeIssue({
        body: 'Long description '.repeat(100),
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Expand')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Expand'))
      await waitFor(() => {
        expect(screen.getByText('Collapse')).toBeInTheDocument()
      })
    })

    it('shows Expand button again after collapsing', async () => {
      mocks.issue = makeIssue({
        body: 'Long description '.repeat(100),
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Expand')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Expand'))
      await waitFor(() => {
        expect(screen.getByText('Collapse')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Collapse'))
      await waitFor(() => {
        expect(screen.getByText('Expand')).toBeInTheDocument()
      })
    })

    it('does not show expand button for short description that fits within threshold', async () => {
      scrollHeightSpy.mockReturnValue(300)
      mocks.issue = makeIssue({
        body: 'Short content',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.queryByText('Expand')).not.toBeInTheDocument()
      })
    })

    it('does not show expand/collapse for empty description', async () => {
      mocks.issue = makeIssue({
        body: '',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.queryByText('Expand')).not.toBeInTheDocument()
        expect(screen.queryByText('Collapse')).not.toBeInTheDocument()
      })
    })

    it('shows Markdown content after expansion', async () => {
      mocks.issue = makeIssue({
        body: '# Long Heading\n\nContent here',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Expand')).toBeInTheDocument()
      })
      fireEvent.click(screen.getByText('Expand'))
      await waitFor(() => {
        expect(screen.getByText(/Long Heading/i)).toBeInTheDocument()
        expect(screen.getByText('Content here')).toBeInTheDocument()
      })
    })
  })

  describe('existing comment actions', () => {
    it('shows comment textarea', async () => {
      mocks.issue = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByPlaceholderText('Add a comment...')).toBeInTheDocument()
      })
    })

    it('shows Comment submit button', async () => {
      mocks.issue = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByRole('button', { name: 'Comment' })).toBeInTheDocument()
      })
    })

    it('submit button is disabled when comment text is empty', async () => {
      mocks.issue = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const button = screen.getByRole('button', { name: 'Comment' })
        expect(button).toBeDisabled()
      })
    })

    it('submit button is enabled when comment text is present', async () => {
      mocks.issue = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByPlaceholderText('Add a comment...')).toBeInTheDocument()
      })
      const textarea = screen.getByPlaceholderText('Add a comment...')
      fireEvent.change(textarea, { target: { value: 'Test comment' } })
      const button = screen.getByRole('button', { name: 'Comment' })
      expect(button).not.toBeDisabled()
    })

    it('displays delete button for existing comments', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: 'Existing comment',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Delete')).toBeInTheDocument()
      })
    })

    it('shows empty comments message when no comments exist', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('No comments yet.')).toBeInTheDocument()
      })
    })
  })

  describe('action error display', () => {
    it('displays retry mutation error in action error area', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        stage: Stage.Plan,
        status: IssueStatus.Blocked,
        recovery: { allowedActions: ['retry'], latestAttemptState: 'failed' },
      })
      const issueApi = await import('../src/entities/issue/api/client')
      vi.mocked(issueApi.retryIssue).mockRejectedValueOnce(new Error('no retryable failed work'))
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
      })
      fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
      await waitFor(() => {
        expect(screen.getByText('no retryable failed work')).toBeInTheDocument()
      })
    })

    it('allows user to see other recovery actions after retry error appears', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        stage: Stage.Plan,
        status: IssueStatus.Blocked,
        recovery: { allowedActions: ['retry', 'rerun'], latestAttemptState: 'failed' },
      })
      const issueApi = await import('../src/entities/issue/api/client')
      vi.mocked(issueApi.retryIssue).mockRejectedValueOnce(new Error('no retryable failed work'))
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
        expect(screen.getAllByRole('button', { name: 'Rerun Stage' }).length).toBeGreaterThan(0)
      })
      fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
      await waitFor(() => {
        expect(screen.getByText('no retryable failed work')).toBeInTheDocument()
        expect(screen.getAllByRole('button', { name: 'Rerun Stage' }).length).toBeGreaterThan(0)
      })
    })
  })
})
