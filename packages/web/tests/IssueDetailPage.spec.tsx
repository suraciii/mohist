import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { baseRender, screen, fireEvent, waitFor, within } from './test-utils'
import userEvent from '@testing-library/user-event'
import { IssueDetailPage } from '../src/pages/issue-detail/ui/IssueDetailPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { TEST_PROJECT } from './test-utils'
import { server, useMswServer } from './support/msw'
import { setScopedProperty, setScopedValue } from './support/scoped-property'
import React from 'react'
import { IssueHealth, WorkflowStage } from '../src/entities/issue'

let _issueData: any = null
let _workflowProfileData: any = null
let _workflowProfileLoading = false
let _workflowProfileError: string | null = null
let _workflowProfilesListData: any = null
let _retryError: string | null = null
let _uploads: Array<{ id: string; fileName: string; contentType: string; size: number }> = []
let _addCommentHandler = vi.fn()
let _deleteCommentHandler = vi.fn()
let _retryHandler = vi.fn()
let _closeHandler = vi.fn()

useMswServer(
  http.get('*/api/projects/:projectId/issues/:number', () =>
    HttpResponse.json({ success: true, data: _issueData }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/diff', () =>
    HttpResponse.json({ success: true, data: null }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/commits', () =>
    HttpResponse.json({ success: true, data: null }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/workspace-status', () =>
    HttpResponse.json({ success: true, data: null }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/workflow-profile', () => {
    if (_workflowProfileLoading) return new Promise(() => {})
    if (_workflowProfileError) {
      return HttpResponse.json({ success: false, error: _workflowProfileError }, { status: 500 })
    }
    return HttpResponse.json({ success: true, data: _workflowProfileData })
  }),
  http.put('*/api/projects/:projectId/issues/:number/workflow-profile/template', () =>
    HttpResponse.json({ success: true, data: _workflowProfileData }),
  ),
  http.delete('*/api/projects/:projectId/issues/:number/workflow-profile/template', () =>
    HttpResponse.json({ success: true, data: _workflowProfileData }),
  ),
  http.patch('*/api/projects/:projectId/issues/:number', () =>
    HttpResponse.json({ success: true, data: _issueData }),
  ),
  http.get('*/api/projects/:projectId/workflow-profile', () =>
    HttpResponse.json({ success: true, data: { projectId: 'test-project', defaultTemplateId: null, disabledWorkflowProfileIds: [] } }),
  ),
  http.get('*/api/workflow-templates/system', () =>
    HttpResponse.json({ success: true, data: _workflowProfilesListData?.map
      ? _workflowProfilesListData.map((p: any) => ({ id: p.id, name: p.displayName, description: p.description, isDefault: p.isDefault }))
      : null }),
  ),
  http.get('*/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/workflow-profile/variables', () =>
    HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
  ),
  http.get('*/api/projects/:projectId/workflow-profile/variables', () =>
    HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({
      success: true,
      data: {
        running: false,
        issueNumber: null,
        activeAgents: [],
        runnerAvailable: true,
        capacity: { active: 0, max: 1 },
      },
    }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/workflow/artifacts', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/workflow/tasks/:taskId/logs', () =>
    HttpResponse.json({ success: true, data: { lines: [], nextCursor: null, truncated: false } }),
  ),
  http.get('*/api/workflow-runs/:runId/sessions', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.patch('*/api/projects/:projectId/issues/:number/workflow-profile/variables', () =>
    HttpResponse.json({ success: true, data: { vars: {}, stages: {} } }),
  ),
  http.post('*/api/projects/:projectId/issues/:number/start', () =>
    HttpResponse.json({ success: true, data: {} }),
  ),
  http.post('*/api/projects/:projectId/issues/:number/close', ({ params }) => {
    _closeHandler(Number(params.number), params.projectId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:number/force-stop', () =>
    HttpResponse.json({ success: true, data: {} }),
  ),
  http.post('*/api/projects/:projectId/issues/:number/reopen', () =>
    HttpResponse.json({ success: true, data: {} }),
  ),
  http.post('*/api/projects/:projectId/issues/:number/rerun', () =>
    HttpResponse.json({ success: true, data: {} }),
  ),
  http.post('*/api/projects/:projectId/issues/:number/retry', () => {
    _retryHandler()
    if (_retryError) {
      return HttpResponse.json({ success: false, error: _retryError }, { status: 400 })
    }
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:number/comments', async ({ params, request }) => {
    const body = await request.json() as any
    _addCommentHandler(Number(params.number), body.body, params.projectId)
    return HttpResponse.json({ success: true, data: { id: 'comment-new', body: body.body, createdAt: new Date().toISOString() } })
  }),
  http.delete('*/api/projects/:projectId/issues/:number/comments/:commentId', ({ params }) => {
    _deleteCommentHandler(Number(params.number), params.commentId, params.projectId)
    return HttpResponse.json({ success: true, data: { message: 'Deleted' } })
  }),
  http.get('*/api/projects/:projectId/issues/:number/comments/:commentId/attachments/:attachmentId/content', () =>
    new HttpResponse('fake content', { headers: { 'content-type': 'text/plain' } }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/attachments/:attachmentId/content', () =>
    new HttpResponse('fake content', { headers: { 'content-type': 'text/plain' } }),
  ),
  http.get('*/api/projects/:projectId/issues/:number/workflow/status', () =>
    HttpResponse.json({ success: true, data: { workflow: null } }),
  ),
)

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
  setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get() {
      return scrollHeight
    },
  })
  await run()
}

beforeEach(() => {
  vi.clearAllMocks()
  _issueData = null
  _workflowProfileData = null
  _workflowProfileLoading = false
  _workflowProfileError = null
  _workflowProfilesListData = null
  _retryError = null
  _uploads = []
  _addCommentHandler = vi.fn()
  _deleteCommentHandler = vi.fn()
  _retryHandler = vi.fn()
  _closeHandler = vi.fn()
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
  setScopedValue(globalThis, 'ResizeObserver', resizeObserverSpy)
  let objectUrlCounter = 0
  vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:test-${++objectUrlCounter}`)
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(vi.fn())
  class MockXMLHttpRequest {
    upload = { onprogress: null as ((event: ProgressEvent) => void) | null }
    status = 200
    responseText = ''
    onload: (() => void) | null = null
    onerror: (() => void) | null = null
    open = vi.fn()
    send = vi.fn(() => {
      this.status = 200
      const upload = _uploads.shift() ?? { id: 'att_default', fileName: 'default.txt', contentType: 'text/plain', size: 12 }
      this.responseText = JSON.stringify(upload)
      this.upload.onprogress?.({ lengthComputable: true, loaded: upload.size, total: upload.size } as ProgressEvent)
      this.onload?.()
    })
  }
  vi.stubGlobal('XMLHttpRequest', MockXMLHttpRequest)
})

afterEach(() => {
  queryClient.clear()
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
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={['/issues/1']}>
          <Routes>
            <Route path="/issues/:number" element={ui} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeIssue(overrides: any = {}) {
  return {
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
      _issueData = makeIssue({ body: '# Heading\n\nSome content' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Heading')).toBeInTheDocument()
      })
    })

    it('renders Markdown lists in description', async () => {
      _issueData = makeIssue({ body: '- Item 1\n- Item 2\n- Item 3' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('Item 1')).toBeInTheDocument()
        expect(screen.getByText('Item 2')).toBeInTheDocument()
        expect(screen.getByText('Item 3')).toBeInTheDocument()
      })
    })

    it('renders strikethrough in description', async () => {
      _issueData = makeIssue({ body: '~~deleted text~~' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('deleted text')).toBeInTheDocument()
      })
    })

    it('renders bare URL autolinks in description', async () => {
      _issueData = makeIssue({ body: 'Visit https://example.com for more' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const link = screen.getByRole('link', { name: /https:\/\/example\.com/i })
        expect(link).toBeInTheDocument()
        expect(link).toHaveAttribute('href', 'https://example.com')
      })
    })

    it('renders inline code with distinct styling in description', async () => {
      _issueData = makeIssue({ body: 'Use `const x = 1` for constants' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const code = screen.getByText('const x = 1')
        expect(code.tagName).toBe('CODE')
      })
    })

    it('renders fenced code blocks in description', async () => {
      _issueData = makeIssue({
        body: '```js\nconsole.log("hello")\n```',
      })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('console.log("hello")')).toBeInTheDocument()
      })
    })

    it('renders ordered lists in description', async () => {
      _issueData = makeIssue({ body: '1. First\n2. Second\n3. Third' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('First')).toBeInTheDocument()
        expect(screen.getByText('Second')).toBeInTheDocument()
        expect(screen.getByText('Third')).toBeInTheDocument()
      })
    })

    it('renders emphasis and strong text in description', async () => {
      _issueData = makeIssue({ body: '**bold** and *italic*' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('bold')).toBeInTheDocument()
        expect(screen.getByText('italic')).toBeInTheDocument()
      })
    })

    it('renders blockquotes in description', async () => {
      _issueData = makeIssue({ body: '> This is a quote' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByText('This is a quote')).toBeInTheDocument()
      })
    })
  })

  describe('comment Markdown', () => {
    it('renders Markdown in comments', async () => {
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
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
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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
      _issueData = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByPlaceholderText('Add a comment...')).toBeInTheDocument()
      })
    })

    it('shows Comment submit button', async () => {
      _issueData = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByRole('button', { name: 'Comment' })).toBeInTheDocument()
      })
    })

    it('submit button is disabled when comment text is empty', async () => {
      _issueData = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        const button = screen.getByRole('button', { name: 'Comment' })
        expect(button).toBeDisabled()
      })
    })

    it('submit button is enabled when comment text is present', async () => {
      _issueData = makeIssue({ body: 'Issue body' })
      renderWithQueryClient(<IssueDetailPage />)
      await waitFor(() => {
        expect(screen.getByPlaceholderText('Add a comment...')).toBeInTheDocument()
      })
      fireEvent.change(screen.getByRole('textbox', { name: 'Author' }), { target: { value: 'Ada' } })
      const textarea = screen.getByPlaceholderText('Add a comment...')
      fireEvent.change(textarea, { target: { value: 'Test comment' } })
      const button = screen.getByRole('button', { name: 'Comment' })
      expect(button).not.toBeDisabled()
    })

    it('displays delete button for existing comments', async () => {
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
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

    it('opens an AlertDialog instead of window.confirm when clicking Delete and only deletes on confirm', async () => {
      _issueData = makeIssue({
        body: 'Issue body',
        comments: [
          {
            id: 'comment-1',
            body: 'Existing comment',
            createdAt: '2024-01-01T11:00:00.000Z',
          },
        ],
      })
      renderWithQueryClient(<IssueDetailPage />)

      const deleteBtn = await screen.findByTestId('comment-delete-button')

      const windowConfirmSpy = vi.spyOn(window, 'confirm')

      fireEvent.click(deleteBtn)

      expect(windowConfirmSpy).not.toHaveBeenCalled()
      expect(_deleteCommentHandler).not.toHaveBeenCalled()

      const dialog = await screen.findByTestId('comment-delete-alert')
      expect(dialog).toBeInTheDocument()

      fireEvent.click(within(dialog).getByTestId('comment-delete-alert-cancel'))

      await waitFor(() => {
        expect(screen.queryByTestId('comment-delete-alert')).not.toBeInTheDocument()
      })
      expect(_deleteCommentHandler).not.toHaveBeenCalled()

      fireEvent.click(screen.getByTestId('comment-delete-button'))
      const confirmDialog = await screen.findByTestId('comment-delete-alert')
      fireEvent.click(within(confirmDialog).getByTestId('comment-delete-alert-confirm'))

      await waitFor(() => {
        expect(_deleteCommentHandler).toHaveBeenCalledTimes(1)
      })
      expect(windowConfirmSpy).not.toHaveBeenCalled()
    })

    it('shows empty comments message when no comments exist', async () => {
      _issueData = makeIssue({
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
      _retryError = 'no retryable failed work'
      _issueData = makeIssue({
        body: 'Issue body',
        status: WorkflowStage.Plan,
        health: IssueHealth.Blocked,
        recovery: { allowedActions: ['retry'], latestAttemptState: 'failed' },
      })
      renderWithQueryClient(<IssueDetailPage />)
      const surface = await waitFor(() => {
        const el = screen.getByTestId('issue-decision-surface')
        expect(el).toBeInTheDocument()
        return el
      })
      await waitFor(() => {
        expect(within(surface).getByTestId('decision-action-retry')).toBeInTheDocument()
      })
      fireEvent.click(within(surface).getByTestId('decision-action-retry'))
      await waitFor(() => {
        const errorEl = screen.queryByTestId('decision-action-error')
        expect(errorEl?.textContent ?? '').toMatch(/no retryable failed work/)
      })
    })

    it('allows user to see other recovery actions after retry error appears', async () => {
      _retryError = 'no retryable failed work'
      _issueData = makeIssue({
        body: 'Issue body',
        status: WorkflowStage.Plan,
        health: IssueHealth.Blocked,
        recovery: { allowedActions: ['retry', 'rerun'], latestAttemptState: 'failed' },
      })
      renderWithQueryClient(<IssueDetailPage />)
      const surface = await waitFor(() => {
        const el = screen.getByTestId('issue-decision-surface')
        expect(el).toBeInTheDocument()
        return el
      })
      await waitFor(() => {
        expect(within(surface).getByTestId('decision-action-retry')).toBeInTheDocument()
        expect(within(surface).getByTestId('decision-action-rerun')).toBeInTheDocument()
      })
      fireEvent.click(within(surface).getByTestId('decision-action-retry'))
      await waitFor(() => {
        expect(screen.getByText('no retryable failed work')).toBeInTheDocument()
        expect(within(surface).getByTestId('decision-action-rerun')).toBeInTheDocument()
      })
    })
  })

  describe('attachment integration', () => {
    it('uploads pasted files in the issue body editor and comment composer with independent state', async () => {
      _issueData = makeIssue({ body: 'Issue body' })
      _uploads = [
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
      expect((description as HTMLTextAreaElement).value).toContain('![issue-image.png](att:')
      expect((comment as HTMLTextAreaElement).value).toContain('[comment-log.txt](att:')
    })

    it('uploads dropped files on both issue and comment composer surfaces', async () => {
      _issueData = makeIssue({ body: 'Issue body' })
      _uploads = [
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
        expect((description as HTMLTextAreaElement).value).toContain('[issue-drop.txt](att:')
        expect((comment as HTMLTextAreaElement).value).toContain('![comment-drop.png](att:')
      })
    })

    it('renders issue and comment attachments through serving URLs with lightbox and file-card download names', async () => {
      _issueData = makeIssue({
        body: 'See ![screen](att:att_image_real) and [report](att:att_report_real)',
        attachments: [
          { id: 'att_image_real', fileName: 'screen.png', contentType: 'image/png', size: 1024 },
          { id: 'att_report_real', fileName: 'report.pdf', contentType: 'application/pdf', size: 2048 },
        ],
        comments: [
          {
            id: 'comment-1',
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
      expect(issueImage).toHaveAttribute('src', '/api/projects/test-project/issues/1/attachments/att_image_real/content')
      fireEvent.click(screen.getAllByTestId('markdown-attachment-image-trigger')[0])
      expect(await screen.findByTestId('markdown-attachment-lightbox')).toBeInTheDocument()
      fireEvent.click(screen.getByTestId('markdown-attachment-lightbox'))
      await waitFor(() => expect(screen.queryByTestId('markdown-attachment-lightbox')).not.toBeInTheDocument())

      const card = screen.getByTestId('markdown-attachment-file-card')
      expect(card).toHaveAttribute('href', '/api/projects/test-project/issues/1/attachments/att_report_real/content')
      expect(card).toHaveAttribute('download', 'report.pdf')
      expect(card).toHaveTextContent('2.0 KB')
      expect(screen.getByRole('img', { name: 'comment' })).toHaveAttribute(
        'src',
        '/api/projects/test-project/issues/1/comments/comment-1/attachments/att_comment_image_real/content',
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
    profileId: 'mohist/local',
    updateMode: 'Reference',
    variables: {},
    updatedAt: '2024-01-01T00:00:00.000Z',
    templateSource: 'system' as const,
  })

  function findDetailsCard() {
    return screen.getByTestId('reference-rail-details')
  }

  function findRailCard(_name: 'Configuration') {
    const testId = 'reference-rail-configuration'
    return screen.getByTestId(testId)
  }

  it('does not render a duplicate Workflow Profile row in the DETAILS sidebar', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/local',
      projectName: 'Test Project',
      repository: { name: 'main', baseBranch: 'main' },
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('workflow-profile-reference')).toBeInTheDocument()
    })

    const detailsCard = findDetailsCard()
    const labels = within(detailsCard).queryAllByText(/Workflow Profile/i)
    expect(labels).toHaveLength(0)
  })

  it('keeps issue metadata visible in the DETAILS sidebar even after the profile row is removed', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/local',
      projectName: 'Test Project',
      repository: { name: 'main', baseBranch: 'main' },
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('workflow-profile-reference')).toBeInTheDocument()
    })

    const detailsCard = findDetailsCard()
    expect(within(detailsCard).queryByText('Issue Stage')).toBeNull()
    expect(within(detailsCard).queryByText('Workflow Stage')).toBeNull()
    expect(within(detailsCard).getByText('Project')).toBeInTheDocument()
    expect(within(detailsCard).getByText('Test Project')).toBeInTheDocument()
    expect(within(detailsCard).getByText('Repository')).toBeInTheDocument()
    expect(within(detailsCard).getByTestId('repository-name')).toHaveTextContent('main')
    expect(within(detailsCard).getByTestId('repository-base-branch')).toHaveTextContent('main')
  })

  it('renders the Workflow Profile card as the single source of profile identity', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/local',
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const profileCard = await waitFor(() => screen.getByTestId('workflow-profile-reference'))
    expect(profileCard).toBeInTheDocument()
    expect(within(profileCard).getByText('mohist/local')).toBeInTheDocument()
    expect(within(profileCard).getByText('Inherited')).toBeInTheDocument()
  })

  it('groups reference-rail panels by user intent and keeps workflow outputs in the reading flow', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowRunId: 'run-123',
      workflowProfileId: 'mohist/local',
      projectName: 'Test Project',
      repository: { name: 'main', baseBranch: 'main' },
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('reference-rail-details-toggle')).toBeInTheDocument()
      expect(screen.getByTestId('reference-rail-configuration-toggle')).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: 'Artifacts' })).toBeInTheDocument()
    })

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))

    expect(referenceRail.contains(screen.getByTestId('reference-rail-details'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('reference-rail-configuration'))).toBe(true)
    expect(referenceRail.querySelector('[data-testid="reference-rail-actions"]')).toBeNull()
    expect(readingFlow.contains(screen.getByRole('heading', { name: 'Artifacts' }))).toBe(true)
    expect(readingFlow.contains(screen.getByText('Tasks', { selector: 'h3' }))).toBe(true)
    expect(screen.queryByText('Task Progress')).toBeNull()
    expect(readingFlow.contains(screen.getByText('Sessions'))).toBe(true)
    expect(referenceRail.contains(screen.getByRole('heading', { name: 'Artifacts' }))).toBe(false)

    const configurationCard = findRailCard('Configuration')
    expect(within(configurationCard).getByText('Coder Model')).toBeInTheDocument()
    expect(within(configurationCard).getByText('Per-stage overrides')).toBeInTheDocument()
  })

  it('groups backlog prerequisite controls with configuration instead of a separate rail card', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: 'mohist/local',
      prerequisites: [
        { number: 2, title: 'Prepare dependency', completed: false },
      ],
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('reference-rail-configuration')).toBeInTheDocument()
    })

    const configurationCard = findRailCard('Configuration')
    expect(within(configurationCard).getByTestId('prerequisite-configuration-controls')).toBeInTheDocument()
    expect(within(configurationCard).getByText('Prerequisites')).toBeInTheDocument()
    expect(screen.queryByText('Add Prerequisite', { selector: 'h2' })).not.toBeInTheDocument()
  })

  it('labels active run YAML as runtime output, not workflow profile configuration', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Build,
      workflowProfileId: 'mohist/local',
      workflowRunId: 'run-123',
    })
    _workflowProfileData = referenceProfileData()

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
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/github-pr',
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    const control = await waitFor(() => screen.getByTestId('issue-workflow-profile-control'))
    expect(control.dataset.effectiveProfile).toBe('mohist/github-pr')
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr')
  })

  it('disables the profile change selector and explains why on a started issue', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'in_progress',
      workflowStage: WorkflowStage.Plan,
      workflowProfileId: 'mohist/github-pr',
      workflowRunId: 'run-123',
    })
    _workflowProfileData = referenceProfileData()
    _workflowProfilesListData = [
      { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderWithQueryClient(<IssueDetailPage />)

    const select = await waitFor(() => screen.getByTestId('issue-workflow-profile-select'))
    expect(select).toBeDisabled()
    const reason = screen.getByTestId('issue-workflow-profile-locked-reason')
    expect(reason).toHaveTextContent(/started/i)
  })

  it('sends the new profile id to the PATCH endpoint when the user changes profile on a backlog issue', async () => {
    const _patchHandler = vi.fn()
    server.use(
      http.patch('*/api/projects/:projectId/issues/:number', async ({ params, request }) => {
        const body = await request.json() as any
        _patchHandler(Number(params.number), body.workflowProfileId, params.projectId)
        return HttpResponse.json({ success: true, data: _issueData })
      }),
    )

    _issueData = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: 'mohist/local',
      isDraft: false,
      canStart: true,
      blocker: null,
    })
    _workflowProfileData = referenceProfileData()
    _workflowProfilesListData = [
      { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderWithQueryClient(<IssueDetailPage />)

    const user = userEvent.setup()
    const select = await waitFor(() => screen.getByTestId('issue-workflow-profile-select'))
    expect(select).not.toBeDisabled()

    await user.click(select)
    await user.click(await screen.findByRole('option', { name: 'PR' }))

    await waitFor(() => expect(_patchHandler).toHaveBeenCalledTimes(1))
    expect(_patchHandler).toHaveBeenCalledWith(1, 'mohist/github-pr', TEST_PROJECT.id)
  })

  it('surfaces the server error and keeps the previous profile when PATCH is rejected', async () => {
    let patchCallCount = 0
    server.use(
      http.patch('*/api/projects/:projectId/issues/:number', async () => {
        patchCallCount++
        if (patchCallCount === 1) {
          return HttpResponse.json(
            { success: false, error: 'Cannot change workflow profile: workflow run wr-1 is active' },
            { status: 400 },
          )
        }
        return HttpResponse.json({ success: true, data: _issueData })
      }),
    )

    _issueData = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: 'mohist/local',
      isDraft: false,
      canStart: true,
      blocker: null,
    })
    _workflowProfileData = referenceProfileData()
    _workflowProfilesListData = [
      { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderWithQueryClient(<IssueDetailPage />)

    const user = userEvent.setup()
    const select = await waitFor(() => screen.getByTestId('issue-workflow-profile-select'))
    await user.click(select)
    await user.click(await screen.findByRole('option', { name: 'PR' }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-error')).toHaveTextContent(/active/))
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local')
  })

  it('renders the inherited default when neither the read model nor the workflow-profile response carry a selection', async () => {
    _issueData = makeIssue({
      body: 'Issue body',
      status: 'backlog',
      workflowProfileId: null,
      isDraft: false,
      canStart: true,
      blocker: null,
    })
    _workflowProfileData = referenceProfileData()

    renderWithQueryClient(<IssueDetailPage />)

    await waitFor(() => {
      expect(screen.getByTestId('issue-workflow-profile-control').dataset.effectiveProfile).toBe('mohist/local')
    })
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local')
  })
})
