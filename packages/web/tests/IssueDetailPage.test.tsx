// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { baseRender, screen, fireEvent, waitFor, within } from './test-utils'
import { IssueDetailPage } from '../src/pages/issue-detail/ui/IssueDetailPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import { IssueHealth, WorkflowStage } from '../src/entities/issue'

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
    workflowProfile: null as any,
    workflowProfileLoading: false,
    workflowProfileError: null as Error | null,
    workflowProfileRefetch: vi.fn(),
    workflowProfileUpdateMutate: vi.fn(),
    workflowProfileDeleteMutate: vi.fn(),
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
    useWorkspaceStatus: () => ({ data: null }),
    useIssueStageState: () => ({ data: null }),
    useWorkflowRun: () => ({ data: null }),
    useIssueWorkflowProfileYaml: () => ({
      data: mocks.workflowProfile,
      isLoading: mocks.workflowProfileLoading,
      error: mocks.workflowProfileError,
      refetch: mocks.workflowProfileRefetch,
    }),
    useUpdateIssueWorkflowProfileYaml: () => ({
      mutate: mocks.workflowProfileUpdateMutate,
      isPending: false,
    }),
    useDeleteIssueWorkflowProfileTemplate: () => ({
      mutate: mocks.workflowProfileDeleteMutate,
      isPending: false,
    }),
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

let resizeObserverInstances: Array<{ observed: Element[]; callback: ResizeObserverCallback }> = []
let resizeObserverSpy: ReturnType<typeof vi.fn>

class StubResizeObserver {
  public observed: Element[] = []
  public callback: ResizeObserverCallback
  constructor(callback: ResizeObserverCallback) {
    this.callback = callback
  }
  observe(target: Element): void {
    this.observed.push(target)
    resizeObserverInstances.push({ observed: this.observed, callback: this.callback })
  }
  unobserve(target: Element): void {
    this.observed = this.observed.filter((el) => el !== target)
  }
  disconnect(): void {
    this.observed = []
  }
  trigger(): void {
    this.callback(
      this.observed.map((target) => ({ target, contentRect: { width: 0, height: 0, top: 0, left: 0, bottom: 0, right: 0, x: 0, y: 0, toJSON: () => ({}) } })) as unknown as ResizeObserverEntry[],
      this as unknown as ResizeObserver,
    )
  }
}

async function withSimulatedReaderHeight(scrollHeight: number, run: () => Promise<void>) {
  const original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight')
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get() {
      return scrollHeight
    },
  })
  try {
    await run()
  } finally {
    if (original) {
      Object.defineProperty(HTMLElement.prototype, 'scrollHeight', original)
    } else {
      delete (HTMLElement.prototype as unknown as { scrollHeight?: number }).scrollHeight
    }
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.issue = null
  mocks.agentStatus = { activeAgents: [], maxConcurrentAgents: 3 }
  mocks.params = { number: '1' }
  mocks.workflowProfile = null
  mocks.workflowProfileLoading = false
  mocks.workflowProfileError = null
  mocks.workflowProfileRefetch = vi.fn()
  mocks.workflowProfileUpdateMutate = vi.fn()
  mocks.workflowProfileDeleteMutate = vi.fn()
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })

  resizeObserverInstances = []
  resizeObserverSpy = vi.fn(function (this: unknown, callback: ResizeObserverCallback) {
    return new StubResizeObserver(callback)
  })
  ;(globalThis as { ResizeObserver?: unknown }).ResizeObserver = resizeObserverSpy
})

afterEach(() => {
  queryClient.clear()
  delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver
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
    status: 'backlog',
    health: 'active',
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
    it('renders the description through MarkdownReader in collapsible mode with base heading level 2', async () => {
      mocks.issue = makeIssue({
        body: 'Long description '.repeat(100),
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const reader = screen.getByTestId('markdown-reader')
        expect(reader).toHaveAttribute('data-mode', 'collapsible')
        expect(reader).toHaveAttribute('data-base-heading-level', '2')
      })
    })

    it('shows a Reader-level Expand control for tall description content', async () => {
      mocks.issue = makeIssue({
        body: 'Long description '.repeat(100),
      })
      await withSimulatedReaderHeight(900, async () => {
        renderWithQueryClient(<IssueDetailPage />)
        await waitFor(() => {
          expect(screen.getByTestId('markdown-expand-control')).toBeInTheDocument()
          expect(screen.getByTestId('markdown-expand-control')).toHaveTextContent('Expand')
          expect(screen.getByTestId('markdown-reader-body')).toHaveAttribute('data-overflow', 'constrained')
        })
      })
    })

    it('toggles Expand and Collapse through the Reader-level control', async () => {
      mocks.issue = makeIssue({
        body: 'Long description '.repeat(100),
      })
      await withSimulatedReaderHeight(900, async () => {
        renderWithQueryClient(<IssueDetailPage />)
        await waitFor(() => {
          expect(screen.getByTestId('markdown-expand-control')).toBeInTheDocument()
        })
        fireEvent.click(screen.getByTestId('markdown-expand-control'))
        await waitFor(() => {
          expect(screen.getByTestId('markdown-collapse-control')).toBeInTheDocument()
          expect(screen.getByTestId('markdown-reader-body')).toHaveAttribute('data-overflow', 'free')
        })
        fireEvent.click(screen.getByTestId('markdown-collapse-control'))
        await waitFor(() => {
          expect(screen.getByTestId('markdown-expand-control')).toBeInTheDocument()
          expect(screen.getByTestId('markdown-reader-body')).toHaveAttribute('data-overflow', 'constrained')
        })
      })
    })

    it('does not render a Reader-level Expand control for short description that fits within the threshold', async () => {
      mocks.issue = makeIssue({
        body: 'Short content',
      })
      await withSimulatedReaderHeight(120, async () => {
        renderWithQueryClient(<IssueDetailPage />)
        await waitFor(() => {
          expect(screen.getByTestId('markdown-reader')).toBeInTheDocument()
        })
        expect(screen.queryByTestId('markdown-expand-control')).not.toBeInTheDocument()
        expect(screen.queryByTestId('markdown-collapse-control')).not.toBeInTheDocument()
        expect(screen.getByTestId('markdown-reader-body')).toHaveAttribute('data-overflow', 'free')
      })
    })

    it('does not render any Expand/Collapse control for empty description', async () => {
      mocks.issue = makeIssue({
        body: '',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Test Issue')).toBeInTheDocument()
      })
      expect(screen.queryByTestId('markdown-expand-control')).not.toBeInTheDocument()
      expect(screen.queryByTestId('markdown-collapse-control')).not.toBeInTheDocument()
    })

    it('shows Markdown content after expanding the description', async () => {
      mocks.issue = makeIssue({
        body: '# Long Heading\n\nContent here',
      })
      await withSimulatedReaderHeight(900, async () => {
        renderWithQueryClient(<IssueDetailPage />)
        await waitFor(() => {
          expect(screen.getByTestId('markdown-expand-control')).toBeInTheDocument()
        })
        fireEvent.click(screen.getByTestId('markdown-expand-control'))
        await waitFor(() => {
          expect(screen.getByRole('heading', { level: 2, name: 'Long Heading' })).toBeInTheDocument()
          expect(screen.getByText('Content here')).toBeInTheDocument()
        })
      })
    })

    it('demotes an embedded # heading in description to h2 so the page title stays the only h1', async () => {
      mocks.issue = makeIssue({
        body: '# Embedded Heading\n\nSome content',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByRole('heading', { level: 2, name: 'Embedded Heading' })).toBeInTheDocument()
      })
      const pageTitle = screen.getByRole('heading', { level: 1, name: 'Test Issue' })
      const allH1 = screen.queryAllByRole('heading', { level: 1 })
      expect(allH1).toHaveLength(1)
      expect(allH1[0]).toBe(pageTitle)
    })
  })

  describe('comments render through MarkdownReader', () => {
    it('renders comment Markdown through MarkdownReader with base heading level 3', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: '# Comment Title\n\nComment body',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      const readers = await waitFor(() =>
        screen.getAllByTestId('markdown-reader'),
      )
      const commentReader = readers.find(
        (node) => node.getAttribute('data-base-heading-level') === '3',
      )
      expect(commentReader).toBeDefined()
      expect(commentReader).toHaveAttribute('data-base-heading-level', '3')
    })

    it('demotes an embedded # heading in a comment to h3', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: '# Comment Heading',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByRole('heading', { level: 3, name: 'Comment Heading' })).toBeInTheDocument()
      })
      const allH1 = screen.queryAllByRole('heading', { level: 1 })
      expect(allH1).toHaveLength(1)
      expect(allH1[0]).toHaveTextContent('Test Issue')
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
        status: WorkflowStage.Plan,
        health: IssueHealth.Blocked,
        recovery: { allowedActions: ['retry'], latestAttemptState: 'failed' },
      })
      const issueApi = await import('../src/entities/issue/api/client')
      vi.mocked(issueApi.retryIssue).mockRejectedValueOnce(new Error('no retryable failed work'))
      renderWithQueryClient(<IssueDetailPage />)
      const surface = await waitFor(() => {
        const el = screen.getByTestId('runtime-decision-surface')
        expect(el).toBeInTheDocument()
        return el
      })
      await waitFor(() => {
        expect(within(surface).getByTestId('runtime-action-retry')).toBeInTheDocument()
      })
      fireEvent.click(within(surface).getByTestId('runtime-action-retry'))
      await waitFor(() => {
        expect(screen.getByText('no retryable failed work')).toBeInTheDocument()
      })
    })

    it('allows user to see other recovery actions after retry error appears', async () => {
      mocks.issue = makeIssue({
        body: 'Issue body',
        status: WorkflowStage.Plan,
        health: IssueHealth.Blocked,
        recovery: { allowedActions: ['retry', 'rerun'], latestAttemptState: 'failed' },
      })
      const issueApi = await import('../src/entities/issue/api/client')
      vi.mocked(issueApi.retryIssue).mockRejectedValueOnce(new Error('no retryable failed work'))
      renderWithQueryClient(<IssueDetailPage />)
      const surface = await waitFor(() => {
        const el = screen.getByTestId('runtime-decision-surface')
        expect(el).toBeInTheDocument()
        return el
      })
      await waitFor(() => {
        expect(within(surface).getByTestId('runtime-action-retry')).toBeInTheDocument()
        expect(within(surface).getByTestId('runtime-action-rerun')).toBeInTheDocument()
      })
      fireEvent.click(within(surface).getByTestId('runtime-action-retry'))
      await waitFor(() => {
        expect(screen.getByText('no retryable failed work')).toBeInTheDocument()
        expect(within(surface).getByTestId('runtime-action-rerun')).toBeInTheDocument()
      })
    })
  })
})

describe('IssueDetailPage workflow profile integration', () => {
  const referenceProfileData = () => ({
    issueNumber: 1,
    projectId: 'test-project',
    issueKey: 'mohist/test-project#1',
    sourceTemplateId: null,
    hasCustomTemplate: false,
    yaml: null,
    workflowRunId: null,
    profileId: 'mohist/default',
    updateMode: 'Reference',
    variables: {},
    updatedAt: '2024-01-01T00:00:00.000Z',
    templateSource: 'system' as const,
  })

  function findDetailsCard() {
    const heading = screen.getByText('Details', { selector: 'h2' })
    let current: HTMLElement | null = heading
    while (current && !(current.tagName === 'SECTION')) {
      current = current.parentElement
    }
    if (!current) throw new Error('Details CardSection not found')
    return current
  }

  it('does not render a duplicate Workflow Profile row in the DETAILS sidebar', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/default',
      projectName: 'Test Project',
      repository: { name: 'main', baseBranch: 'main' },
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('workflow-profile-reference')).toBeInTheDocument()
    })

    const detailsCard = findDetailsCard()
    const labels = within(detailsCard).queryAllByText(/Workflow Profile/i)
    expect(labels).toHaveLength(0)
  })

  it('keeps issue metadata visible in the DETAILS sidebar even after the profile row is removed', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/default',
      projectName: 'Test Project',
      repository: { name: 'main', baseBranch: 'main' },
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('workflow-profile-reference')).toBeInTheDocument()
    })

    const detailsCard = findDetailsCard()
    expect(within(detailsCard).getByText('Issue Stage')).toBeInTheDocument()
    expect(within(detailsCard).getByText('Workflow Stage')).toBeInTheDocument()
    expect(within(detailsCard).getByText('Project')).toBeInTheDocument()
    expect(within(detailsCard).getByText('Test Project')).toBeInTheDocument()
    expect(within(detailsCard).getByText('Repository')).toBeInTheDocument()
    expect(within(detailsCard).getByText('main')).toBeInTheDocument()
  })

  it('renders the Workflow Profile card as the single source of profile identity', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/default',
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const profileCard = await waitFor(() => screen.getByTestId('workflow-profile-reference'))
    expect(profileCard).toBeInTheDocument()
    expect(within(profileCard).getByText('mohist/default')).toBeInTheDocument()
    expect(within(profileCard).getByText('Inherited')).toBeInTheDocument()
  })

  it('keeps the Coder Model and Per-stage overrides controls inside ACTIONS', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/default',
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const actionsHeading = await waitFor(() => screen.getByText('Actions', { selector: 'h2' }))
    let actionsCard: HTMLElement | null = actionsHeading
    while (actionsCard && actionsCard.tagName !== 'SECTION') {
      actionsCard = actionsCard.parentElement
    }
    if (!actionsCard) throw new Error('Actions CardSection not found')

    expect(within(actionsCard).getByText('Coder Model')).toBeInTheDocument()
    expect(within(actionsCard).getByText('Per-stage overrides')).toBeInTheDocument()
  })

  it('labels active run YAML as runtime output, not workflow profile configuration', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Build,
      workflowProfileId: 'mohist/default',
      workflowRunId: 'run-123',
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const trigger = await waitFor(() =>
      screen.getByTestId('active-run-yaml-trigger'),
    )
    expect(trigger).toBeInTheDocument()

    expect(
      within(trigger).getByText('Active run YAML'),
    ).toBeInTheDocument()
    expect(
      within(trigger).getByText(
        /Rendered runtime output of the active workflow run, not the issue's workflow profile configuration\./i,
      ),
    ).toBeInTheDocument()

    expect(
      screen.queryByText(/Workflow Definition/i),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByText(/workflow profile configuration/i, { selector: 'h2' }),
    ).not.toBeInTheDocument()
  })
})
