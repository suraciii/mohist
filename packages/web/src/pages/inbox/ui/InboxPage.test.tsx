// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { NOTIFICATION_KINDS, type InboxItem } from '../../../entities/inbox'
import { ProjectProvider } from '../../../entities/project'
import { server } from '../../../../tests/support/msw'
import { InboxPage } from './InboxPage'

function makeItem(overrides: Partial<InboxItem> = {}): InboxItem {
  return {
    itemId: 'inb-1',
    notificationKind: NOTIFICATION_KINDS.WorkflowFailed,
    issueId: 'issue-42',
    issueNumber: 42,
    issueTitle: 'Snapshot me',
    createdAt: '2026-06-29T00:00:00.000Z',
    readAt: null,
    archivedAt: null,
    isRead: false,
    isArchived: false,
    ...overrides,
  }
}

const unreadFailure = makeItem({
  itemId: 'inb-1',
  notificationKind: NOTIFICATION_KINDS.WorkflowFailed,
  issueNumber: 42,
  issueTitle: 'Snapshot me',
  isRead: false,
})

const readFailure = makeItem({
  itemId: 'inb-2',
  notificationKind: NOTIFICATION_KINDS.WorkflowFailed,
  issueNumber: 13,
  issueTitle: 'Older failure',
  isRead: true,
  readAt: '2026-06-28T12:00:00.000Z',
})

const approval = makeItem({
  itemId: 'inb-3',
  notificationKind: NOTIFICATION_KINDS.ApprovalRequested,
  issueNumber: 5,
  issueTitle: 'Approve please',
  isRead: false,
})

const started = makeItem({
  itemId: 'inb-4',
  notificationKind: NOTIFICATION_KINDS.IssueStarted,
  issueNumber: 9,
  issueTitle: 'Started running',
  isRead: false,
})

const completed = makeItem({
  itemId: 'inb-5',
  notificationKind: NOTIFICATION_KINDS.IssueCompleted,
  issueNumber: 7,
  issueTitle: 'Done',
  isRead: false,
})

const TEST_PROJECT = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

let _inboxData: InboxItem[] = []
let markReadHandler: ReturnType<typeof vi.fn>
let markAllReadHandler: ReturnType<typeof vi.fn>
let archiveHandler: ReturnType<typeof vi.fn>

function setupHandlers() {
  server.use(
    http.get('*/api/projects/:projectId/inbox', () =>
      HttpResponse.json({ success: true, data: _inboxData }),
    ),
    http.post('*/api/projects/:projectId/inbox/:itemId/read', markReadHandler as any),
    http.post('*/api/projects/:projectId/inbox/read-all', markAllReadHandler as any),
    http.post('*/api/projects/:projectId/inbox/:itemId/archive', archiveHandler as any),
  )
}

function renderPage(initialRoute: string = '/demo/inbox') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[initialRoute]}>
          <Routes>
            <Route path="/:projectName/inbox" element={<InboxPage />} />
            <Route path="/:projectName/issues/:number" element={<div data-testid="issue-detail-stub" />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('InboxPage empty state', () => {
  beforeEach(() => {
    _inboxData = []
    markReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', read: true } }))
    markAllReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 0 } }))
    archiveHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', archived: true } }))
    vi.clearAllMocks()
    setupHandlers()
  })

  afterEach(() => {
    cleanup()
    server.resetHandlers()
  })

  it('renders the explicit empty state when the project has no items', async () => {
    renderPage()

    expect(await screen.findByTestId('inbox-empty-state')).toBeInTheDocument()
    expect(screen.getByTestId('inbox-empty-state').textContent).toContain('No inbox items')
  })

  it('disables the Mark all read button when there are no items', async () => {
    renderPage()

    await screen.findByTestId('inbox-empty-state')
    expect(screen.getByTestId('inbox-mark-all-read')).toBeDisabled()
  })

  it('shows "No inbox items yet." in the summary line', async () => {
    renderPage()

    await screen.findByTestId('inbox-empty-state')
    expect(screen.getByTestId('inbox-summary').textContent).toContain('No inbox items yet.')
  })

  it('does not render any inbox items', async () => {
    renderPage()

    await screen.findByTestId('inbox-empty-state')
    expect(screen.queryByTestId('inbox-item')).toBeNull()
    expect(screen.queryByTestId('inbox-list')).toBeNull()
  })
})

describe('InboxPage loading state', () => {
  beforeEach(() => {
    _inboxData = []
    markReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', read: true } }))
    markAllReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 0 } }))
    archiveHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', archived: true } }))
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
    server.resetHandlers()
  })

  it('shows a Loading message instead of the empty state', () => {
    server.use(
      http.get('*/api/projects/:projectId/inbox', () => new Promise(() => {})),
      http.post('*/api/projects/:projectId/inbox/:itemId/read', markReadHandler as any),
      http.post('*/api/projects/:projectId/inbox/read-all', markAllReadHandler as any),
      http.post('*/api/projects/:projectId/inbox/:itemId/archive', archiveHandler as any),
    )

    renderPage()

    expect(screen.getByText('Loading...')).toBeInTheDocument()
    expect(screen.queryByTestId('inbox-empty-state')).toBeNull()
  })
})

describe('InboxPage error state', () => {
  beforeEach(() => {
    _inboxData = []
    markReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', read: true } }))
    markAllReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 0 } }))
    archiveHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', archived: true } }))
    vi.clearAllMocks()
    setupHandlers()
  })

  afterEach(() => {
    cleanup()
    server.resetHandlers()
  })

  it('renders an error state instead of the empty state when the inbox query fails', async () => {
    server.use(
      http.get('*/api/projects/:projectId/inbox', () =>
        HttpResponse.json({ success: false, error: 'Server unavailable' }, { status: 500 }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('inbox-error-state')).toBeInTheDocument()
    expect(screen.getByText('Server unavailable')).toBeInTheDocument()
    expect(screen.queryByTestId('inbox-empty-state')).toBeNull()
  })

  it('calls refetch when Retry is clicked', async () => {
    server.use(
      http.get('*/api/projects/:projectId/inbox', () =>
        HttpResponse.json({ success: false, error: 'Server unavailable' }, { status: 500 }),
      ),
    )

    renderPage()

    expect(await screen.findByTestId('inbox-error-state')).toBeInTheDocument()
    expect(screen.getByText('Server unavailable')).toBeInTheDocument()

    _inboxData = [makeItem()]
    server.use(
      http.get('*/api/projects/:projectId/inbox', () =>
        HttpResponse.json({ success: true, data: _inboxData }),
      ),
    )

    fireEvent.click(screen.getByTestId('inbox-retry'))

    await waitFor(() => {
      expect(screen.queryByTestId('inbox-error-state')).toBeNull()
    })
    expect(screen.getByTestId('inbox-item')).toBeInTheDocument()
  })
})

describe('InboxPage list rendering and link', () => {
  beforeEach(() => {
    _inboxData = [unreadFailure, readFailure, approval, started, completed]
    markReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', read: true } }))
    markAllReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 0 } }))
    archiveHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', archived: true } }))
    vi.clearAllMocks()
    setupHandlers()
  })

  afterEach(() => {
    cleanup()
    server.resetHandlers()
  })

  it('renders one row per item', async () => {
    renderPage()

    const items = await screen.findAllByTestId('inbox-item')
    expect(items).toHaveLength(5)
  })

  it('renders the kind badge for each kind', async () => {
    renderPage()

    const kindTexts = (await screen.findAllByTestId('inbox-item-kind')).map((n) => n.textContent)
    expect(kindTexts).toEqual(expect.arrayContaining(['Failed', 'Approval', 'Started', 'Completed']))
  })

  it('renders the issue number and title for each item', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    expect(screen.getByText('Snapshot me')).toBeInTheDocument()
    expect(screen.getByText('Older failure')).toBeInTheDocument()
    expect(screen.getByText('Approve please')).toBeInTheDocument()
    expect(screen.getByText('Started running')).toBeInTheDocument()
    expect(screen.getByText('Done')).toBeInTheDocument()

    const links = screen.getAllByTestId('inbox-item-link')
    const linkTexts = links.map((l) => l.textContent ?? '')
    expect(linkTexts.some((t) => t.includes('#42'))).toBe(true)
    expect(linkTexts.some((t) => t.includes('#13'))).toBe(true)
    expect(linkTexts.some((t) => t.includes('#5'))).toBe(true)
    expect(linkTexts.some((t) => t.includes('#9'))).toBe(true)
    expect(linkTexts.some((t) => t.includes('#7'))).toBe(true)
  })

  it('deep-links each item to /:projectName/issues/:number', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    const links = screen.getAllByTestId('inbox-item-link')
    expect(links.length).toBeGreaterThan(0)
    const hrefs = links.map((l) => l.getAttribute('href'))
    expect(hrefs).toEqual(expect.arrayContaining(['/demo/issues/42', '/demo/issues/13', '/demo/issues/5', '/demo/issues/9', '/demo/issues/7']))
  })

  it('distinguishes unread items with data-read="false" and read items with data-read="true"', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    const unread = document.querySelector('[data-item-id="inb-1"]') as HTMLElement
    const read = document.querySelector('[data-item-id="inb-2"]') as HTMLElement
    expect(unread).not.toBeNull()
    expect(read).not.toBeNull()
    expect(unread.getAttribute('data-read')).toBe('false')
    expect(read.getAttribute('data-read')).toBe('true')
  })

  it('applies a distinct border/shadow to unread rows', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    const unread = document.querySelector('[data-item-id="inb-1"]') as HTMLElement
    const read = document.querySelector('[data-item-id="inb-2"]') as HTMLElement
    expect(unread.className).toContain('border-blue-300')
    expect(read.className).not.toContain('border-blue-300')
    expect(read.className).toContain('border-gray-200')
  })

  it('shows a Mark read control only for unread items', async () => {
    renderPage()

    const markReadButtons = await screen.findAllByTestId('inbox-item-mark-read')
    expect(markReadButtons).toHaveLength(4)
  })

  it('shows a relative-time string on each item', async () => {
    renderPage()

    const timeNodes = await screen.findAllByTestId('inbox-item-time')
    expect(timeNodes).toHaveLength(5)
    expect(timeNodes[0].textContent).toMatch(/ago|just now/)
  })

  it('renders the product-facing text from kind -> template, not raw event names', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    expect(screen.getByText(/Issue #42 workflow failed/)).toBeInTheDocument()
    expect(screen.getByText(/Issue #5 needs approval/)).toBeInTheDocument()
    expect(screen.getByText(/Issue #9 started/)).toBeInTheDocument()
    expect(screen.getByText(/Issue #7 completed/)).toBeInTheDocument()
  })

  it('does not render any raw event type strings on the page', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    const container = screen.getByTestId('inbox-list')
    expect(container.textContent).not.toContain('workflow_failed')
    expect(container.textContent).not.toContain('approval_requested')
    expect(container.textContent).not.toContain('issue_started')
    expect(container.textContent).not.toContain('issue_completed')
  })

  it('displays the unread count in the summary line', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    expect(screen.getByTestId('inbox-summary').textContent).toContain('4 unread of 5')
  })
})

describe('InboxPage mutations', () => {
  beforeEach(() => {
    _inboxData = [unreadFailure, approval]
    markReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', read: true } }))
    markAllReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 2 } }))
    archiveHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', archived: true } }))
    vi.clearAllMocks()
    setupHandlers()
  })

  afterEach(() => {
    cleanup()
    server.resetHandlers()
  })

  it('invokes mark-read mutation with the item id when Mark read is clicked', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    fireEvent.click(screen.getAllByTestId('inbox-item-mark-read')[0])

    await waitFor(() => {
      expect(markReadHandler).toHaveBeenCalledTimes(1)
    })
    expect(markReadHandler.mock.calls[0][0].params.itemId).toBe('inb-1')
  })

  it('invokes the archive mutation with the item id when Archive is clicked', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    fireEvent.click(screen.getAllByTestId('inbox-item-archive')[0])

    await waitFor(() => {
      expect(archiveHandler).toHaveBeenCalledTimes(1)
    })
    expect(archiveHandler.mock.calls[0][0].params.itemId).toBe('inb-1')
  })

  it('invokes the mark-all-read mutation when Mark all read is clicked', async () => {
    renderPage()

    await screen.findAllByTestId('inbox-item')
    fireEvent.click(screen.getByTestId('inbox-mark-all-read'))

    await waitFor(() => {
      expect(markAllReadHandler).toHaveBeenCalledTimes(1)
    })
  })

  it('disables Mark all read while the mutation is pending', async () => {
    markAllReadHandler.mockImplementation(() => new Promise(() => {}))

    renderPage()

    await screen.findAllByTestId('inbox-item')
    fireEvent.click(screen.getByTestId('inbox-mark-all-read'))

    await waitFor(() => {
      expect(screen.getByTestId('inbox-mark-all-read')).toBeDisabled()
    })
    expect(screen.getByTestId('inbox-mark-all-read').textContent).toBe('Marking...')
  })
})

describe('InboxPage live refresh', () => {
  afterEach(() => {
    cleanup()
    server.resetHandlers()
  })

  it('renders directly from the API query result and does not synthesize items locally', async () => {
    _inboxData = [
      makeItem({ itemId: 'inb-a', notificationKind: NOTIFICATION_KINDS.IssueStarted, issueNumber: 1, isRead: false }),
    ]
    markReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', read: true } }))
    markAllReadHandler = vi.fn(() => HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 0 } }))
    archiveHandler = vi.fn(() => HttpResponse.json({ success: true, data: { itemId: '', archived: true } }))
    vi.clearAllMocks()
    setupHandlers()

    renderPage()

    const items = await screen.findAllByTestId('inbox-item')
    expect(items).toHaveLength(1)
  })
})
