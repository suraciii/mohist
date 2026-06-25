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
    uploads: [] as Array<{ id: string; fileName: string; contentType: string; size: number }>,
    workflowProfile: null as any,
    workflowProfileLoading: false,
    workflowProfileError: null as Error | null,
    workflowProfileRefetch: vi.fn(),
    workflowProfileUpdateMutate: vi.fn(),
    workflowProfileDeleteMutate: vi.fn(),
    workflowProfilesList: null as null | Array<{ id: string; displayName: string; description: string; isDefault: boolean }>,
    updateIssueWorkflowProfileMutate: vi.fn(),
    updateIssueWorkflowProfileMutateAsync: vi.fn(),
    updateIssueWorkflowProfileIsPending: false,
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
    useUpdateIssueWorkflowProfile: () => ({
      mutate: mocks.updateIssueWorkflowProfileMutate,
      mutateAsync: mocks.updateIssueWorkflowProfileMutateAsync,
      isPending: mocks.updateIssueWorkflowProfileIsPending,
    }),
    useDeleteIssueWorkflowProfileTemplate: () => ({
      mutate: mocks.workflowProfileDeleteMutate,
      isPending: false,
    }),
  }
})

vi.mock('../src/entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/settings')>()
  return {
    ...actual,
    useWorkflowProfiles: () => ({ data: mocks.workflowProfilesList }),
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
  mocks.workflowProfilesList = null
  mocks.updateIssueWorkflowProfileMutate = vi.fn()
  mocks.updateIssueWorkflowProfileMutateAsync = vi.fn(() => Promise.resolve({}))
  mocks.updateIssueWorkflowProfileIsPending = false
  mocks.uploads = []
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
  let objectUrlCounter = 0
  vi.stubGlobal('URL', {
    ...URL,
    createObjectURL: vi.fn(() => `blob:test-${++objectUrlCounter}`),
    revokeObjectURL: vi.fn(),
  })
  class MockXMLHttpRequest {
    upload = { onprogress: null as ((event: ProgressEvent) => void) | null }
    status = 200
    responseText = ''
    onload: (() => void) | null = null
    onerror: (() => void) | null = null
    open = vi.fn()
    send = vi.fn(() => {
      const upload = mocks.uploads.shift() ?? { id: 'att_default', fileName: 'default.txt', contentType: 'text/plain', size: 12 }
      this.responseText = JSON.stringify(upload)
      this.upload.onprogress?.({ lengthComputable: true, loaded: upload.size, total: upload.size } as ProgressEvent)
      this.onload?.()
    })
  }
  vi.stubGlobal('XMLHttpRequest', MockXMLHttpRequest)
})

afterEach(() => {
  queryClient.clear()
  delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver
  vi.unstubAllGlobals()
})

function fileList(files: File[]) {
  const list = files.reduce<Record<number, File>>((acc, file, index) => {
    acc[index] = file
    return acc
  }, {})
  return Object.assign(list, {
    length: files.length,
    item: (index: number) => files[index] ?? null,
  }) as unknown as FileList
}

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

  describe('attachment integration', () => {
    it('uploads pasted files in the issue body editor and comment composer with independent state', async () => {
      mocks.issue = makeIssue({ body: 'Issue body' })
      mocks.uploads = [
        { id: 'att_issue_image.png', fileName: 'issue-image.png', contentType: 'image/png', size: 1024 },
        { id: 'att_comment_log.txt', fileName: 'comment-log.txt', contentType: 'text/plain', size: 2048 },
      ]
      renderWithQueryClient(<IssueDetailPage />)

      fireEvent.click(await screen.findByTestId('edit-issue-button'))
      const description = screen.getByPlaceholderText('Optional description')
      fireEvent.paste(description, {
        clipboardData: { files: fileList([new File(['image'], 'issue-image.png', { type: 'image/png' })]) },
      })

      await waitFor(() => {
        expect(screen.getByText('issue-image.png')).toBeInTheDocument()
      })
      expect(screen.queryByText('comment-log.txt')).not.toBeInTheDocument()

      const comment = screen.getByPlaceholderText('Add a comment...')
      fireEvent.paste(comment, {
        clipboardData: { files: fileList([new File(['log'], 'comment-log.txt', { type: 'text/plain' })]) },
      })

      await waitFor(() => {
        expect(screen.getByText('comment-log.txt')).toBeInTheDocument()
      })
      expect((description as HTMLTextAreaElement).value).toContain('![issue-image.png](att:att_issue_image.png)')
      expect((comment as HTMLTextAreaElement).value).toContain('[comment-log.txt](att:att_comment_log.txt)')
    })

    it('uploads dropped files on both issue and comment composer surfaces', async () => {
      mocks.issue = makeIssue({ body: 'Issue body' })
      mocks.uploads = [
        { id: 'att_issue_drop.txt', fileName: 'issue-drop.txt', contentType: 'text/plain', size: 30 },
        { id: 'att_comment_drop.png', fileName: 'comment-drop.png', contentType: 'image/png', size: 40 },
      ]
      renderWithQueryClient(<IssueDetailPage />)

      fireEvent.click(await screen.findByTestId('edit-issue-button'))
      const description = screen.getByPlaceholderText('Optional description')
      fireEvent.drop(description.closest('div')!, {
        dataTransfer: { files: fileList([new File(['issue'], 'issue-drop.txt', { type: 'text/plain' })]) },
      })

      const comment = screen.getByPlaceholderText('Add a comment...')
      fireEvent.drop(comment.closest('div')!, {
        dataTransfer: { files: fileList([new File(['comment'], 'comment-drop.png', { type: 'image/png' })]) },
      })

      await waitFor(() => {
        expect((description as HTMLTextAreaElement).value).toContain('[issue-drop.txt](att:att_issue_drop.txt)')
        expect((comment as HTMLTextAreaElement).value).toContain('![comment-drop.png](att:att_comment_drop.png)')
      })
    })

    it('renders issue and comment attachments through serving URLs with lightbox and file-card download names', async () => {
      mocks.issue = makeIssue({
        body: 'See ![screen](att:att_image_real) and [report](att:att_report_real)',
        attachments: [
          { id: 'att_image_real', fileName: 'screen.png', contentType: 'image/png', size: 1024 },
          { id: 'att_report_real', fileName: 'report.pdf', contentType: 'application/pdf', size: 2048 },
        ],
        comments: [
          {
            id: 'comment-1',
            issueId: 'issue-1',
            body: 'Comment image ![comment](att:att_comment_image_real)',
            createdAt: '2024-01-01T11:00:00.000Z',
            attachments: [
              { id: 'att_comment_image_real', fileName: 'comment-image.png', contentType: 'image/png', size: 512 },
            ],
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)

      const issueImage = await screen.findByRole('img', { name: 'screen' })
      expect(issueImage).toHaveAttribute('src', '/api/projects/project-1/issues/1/attachments/att_image_real/content')
      fireEvent.click(screen.getAllByTestId('markdown-attachment-image-trigger')[0])
      expect(await screen.findByTestId('markdown-attachment-lightbox')).toBeInTheDocument()
      fireEvent.click(screen.getByTestId('markdown-attachment-lightbox'))
      await waitFor(() => expect(screen.queryByTestId('markdown-attachment-lightbox')).not.toBeInTheDocument())

      const card = screen.getByTestId('markdown-attachment-file-card')
      expect(card).toHaveAttribute('href', '/api/projects/project-1/issues/1/attachments/att_report_real/content')
      expect(card).toHaveAttribute('download', 'report.pdf')
      expect(card).toHaveTextContent('2.0 KB')
      expect(screen.getByRole('img', { name: 'comment' })).toHaveAttribute(
        'src',
        '/api/projects/project-1/issues/1/comments/comment-1/attachments/att_comment_image_real/content',
      )
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
    return findCardByHeading('Details')
  }

  function findCardByHeading(name: string) {
    const heading = screen.getByText(name, { selector: 'h2' })
    let current: HTMLElement | null = heading
    while (current && !(current.tagName === 'SECTION')) {
      current = current.parentElement
    }
    if (!current) throw new Error(`${name} CardSection not found`)
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
    expect(within(detailsCard).getByTestId('repository-name')).toHaveTextContent('main')
    expect(within(detailsCard).getByTestId('repository-base-branch')).toHaveTextContent('main')
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

  it('groups issue detail right rail panels by user intent', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowRunId: 'run-123',
      workflowProfileId: 'mohist/default',
      projectName: 'Test Project',
      repository: { name: 'main', baseBranch: 'main' },
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByText('Details', { selector: 'h2' })).toBeInTheDocument()
      expect(screen.getByText('Latest Artifacts', { selector: 'h3' })).toBeInTheDocument()
      expect(screen.getByText('Runtime/Sessions', { selector: 'h2' })).toBeInTheDocument()
      expect(screen.getByText('Configuration', { selector: 'h2' })).toBeInTheDocument()
      expect(screen.getByText('Actions', { selector: 'h2' })).toBeInTheDocument()
    })

    const runtimeSessionsCard = findCardByHeading('Runtime/Sessions')
    expect(within(runtimeSessionsCard).getByText('Task Progress')).toBeInTheDocument()
    expect(within(runtimeSessionsCard).getByText('Sessions')).toBeInTheDocument()

    const configurationCard = findCardByHeading('Configuration')
    expect(within(configurationCard).getByText('Coder Model')).toBeInTheDocument()
    expect(within(configurationCard).getByText('Per-stage overrides')).toBeInTheDocument()

    const actionsCard = findCardByHeading('Actions')
    expect(within(actionsCard).queryByText('Coder Model')).not.toBeInTheDocument()
    expect(within(actionsCard).queryByText('Per-stage overrides')).not.toBeInTheDocument()
  })

  it('groups backlog prerequisite controls with configuration instead of a separate rail card', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: 'mohist/default',
      prerequisites: [
        { number: 2, title: 'Prepare dependency', completed: false },
      ],
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByText('Configuration', { selector: 'h2' })).toBeInTheDocument()
    })

    const configurationCard = findCardByHeading('Configuration')
    expect(within(configurationCard).getByTestId('prerequisite-configuration-controls')).toBeInTheDocument()
    expect(within(configurationCard).getByText('Prerequisites')).toBeInTheDocument()
    expect(screen.queryByText('Add Prerequisite', { selector: 'h2' })).not.toBeInTheDocument()
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
      screen.queryByText(/workflow profile configuration/i, { selector: 'h2' }),
    ).not.toBeInTheDocument()
  })

  it('displays the issue-level workflow profile on the detail page from the read model', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/github-pr',
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const control = await waitFor(() => screen.getByTestId('issue-workflow-profile-control'))
    expect(control.dataset.effectiveProfile).toBe('mohist/github-pr')
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr')
  })

  it('disables the profile change selector and explains why on a started issue', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/github-pr',
      workflowRunId: 'run-123',
    })
    mocks.workflowProfile = referenceProfileData()
    mocks.workflowProfilesList = [
      { id: 'mohist/default', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderWithQueryClient(<IssueDetailPage />)

    const select = await waitFor(() => screen.getByTestId('issue-workflow-profile-select') as HTMLSelectElement)
    expect(select.disabled).toBe(true)
    const reason = screen.getByTestId('issue-workflow-profile-locked-reason')
    expect(reason).toHaveTextContent(/started/i)
  })

  it('sends the new profile id to the PATCH endpoint when the user changes profile on a backlog issue', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: 'mohist/default',
      isDraft: false,
      canStart: true,
      blocker: null,
    })
    mocks.workflowProfile = referenceProfileData()
    mocks.workflowProfilesList = [
      { id: 'mohist/default', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderWithQueryClient(<IssueDetailPage />)

    const select = await waitFor(() => screen.getByTestId('issue-workflow-profile-select') as HTMLSelectElement)
    expect(select.disabled).toBe(false)

    fireEvent.change(select, { target: { value: 'mohist/github-pr' } })

    await waitFor(() => expect(mocks.updateIssueWorkflowProfileMutateAsync).toHaveBeenCalledTimes(1))
    expect(mocks.updateIssueWorkflowProfileMutateAsync).toHaveBeenCalledWith({
      issueNumber: 1,
      workflowProfileId: 'mohist/github-pr',
    })
  })

  it('surfaces the server error and keeps the previous profile when PATCH is rejected', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: 'mohist/default',
      isDraft: false,
      canStart: true,
      blocker: null,
    })
    mocks.workflowProfile = referenceProfileData()
    mocks.workflowProfilesList = [
      { id: 'mohist/default', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]
    mocks.updateIssueWorkflowProfileMutateAsync = vi.fn(() => Promise.reject(
      new Error('Cannot change workflow profile: workflow run wr-1 is active'),
    ))

    renderWithQueryClient(<IssueDetailPage />)

    const select = await waitFor(() => screen.getByTestId('issue-workflow-profile-select') as HTMLSelectElement)
    fireEvent.change(select, { target: { value: 'mohist/github-pr' } })

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-error')).toHaveTextContent(/active/))
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/default')
  })

  it('renders the inherited default when neither the read model nor the workflow-profile response carry a selection', async () => {
    mocks.issue = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: null,
      isDraft: false,
      canStart: true,
      blocker: null,
    })
    mocks.workflowProfile = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const control = await waitFor(() => screen.getByTestId('issue-workflow-profile-control'))
    expect(control.dataset.effectiveProfile).toBe('mohist/default')
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/default')
  })
})
