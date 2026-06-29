// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { NOTIFICATION_KINDS, type InboxItem } from '../../../entities/inbox'
import { ProjectProvider } from '../../../entities/project'
import { InboxPage } from './InboxPage'

const mocks = vi.hoisted(() => ({
  useInbox: vi.fn(),
  useMarkInboxItemRead: vi.fn(),
  useMarkAllInboxRead: vi.fn(),
  useArchiveInboxItem: vi.fn(),
  useInboxLiveRefresh: vi.fn(),
}))

vi.mock('../../../entities/inbox', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/inbox')>()
  return {
    ...actual,
    useInbox: mocks.useInbox,
    useMarkInboxItemRead: mocks.useMarkInboxItemRead,
    useMarkAllInboxRead: mocks.useMarkAllInboxRead,
    useArchiveInboxItem: mocks.useArchiveInboxItem,
    useInboxLiveRefresh: mocks.useInboxLiveRefresh,
  }
})

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
    vi.clearAllMocks()
    mocks.useInbox.mockReturnValue({ data: [], isLoading: false })
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the explicit empty state when the project has no items', () => {
    renderPage()

    const empty = screen.getByTestId('inbox-empty-state')
    expect(empty).toBeInTheDocument()
    expect(empty.textContent).toContain('No inbox items')
  })

  it('disables the Mark all read button when there are no items', () => {
    renderPage()

    expect(screen.getByTestId('inbox-mark-all-read')).toBeDisabled()
  })

  it('shows "No inbox items yet." in the summary line', () => {
    renderPage()

    expect(screen.getByTestId('inbox-summary').textContent).toContain('No inbox items yet.')
  })

  it('does not render any inbox items', () => {
    renderPage()

    expect(screen.queryByTestId('inbox-item')).toBeNull()
    expect(screen.queryByTestId('inbox-list')).toBeNull()
  })
})

describe('InboxPage loading state', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useInbox.mockReturnValue({ data: undefined, isLoading: true })
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)
  })

  afterEach(() => {
    cleanup()
  })

  it('shows a Loading message instead of the empty state', () => {
    renderPage()

    expect(screen.getByText('Loading...')).toBeInTheDocument()
    expect(screen.queryByTestId('inbox-empty-state')).toBeNull()
  })
})

describe('InboxPage error state', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)
  })

  afterEach(() => {
    cleanup()
  })

  it('renders an error state instead of the empty state when the inbox query fails', () => {
    mocks.useInbox.mockReturnValue({
      data: undefined,
      error: new Error('Server unavailable'),
      isError: true,
      isLoading: false,
      refetch: vi.fn(),
    })

    renderPage()

    expect(screen.getByTestId('inbox-error-state')).toBeInTheDocument()
    expect(screen.getByText('Server unavailable')).toBeInTheDocument()
    expect(screen.queryByTestId('inbox-empty-state')).toBeNull()
  })

  it('calls refetch when Retry is clicked', () => {
    const refetch = vi.fn()
    mocks.useInbox.mockReturnValue({
      data: undefined,
      error: new Error('Server unavailable'),
      isError: true,
      isLoading: false,
      refetch,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('inbox-retry'))

    expect(refetch).toHaveBeenCalledTimes(1)
  })
})

describe('InboxPage list rendering and link', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useInbox.mockReturnValue({ data: [unreadFailure, readFailure, approval, started, completed], isLoading: false })
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)
  })

  afterEach(() => {
    cleanup()
  })

  it('renders one row per item', () => {
    renderPage()

    const items = screen.getAllByTestId('inbox-item')
    expect(items).toHaveLength(5)
  })

  it('renders the kind badge for each kind', () => {
    renderPage()

    const kindTexts = screen.getAllByTestId('inbox-item-kind').map((n) => n.textContent)
    expect(kindTexts).toEqual(expect.arrayContaining(['Failed', 'Approval', 'Started', 'Completed']))
  })

  it('renders the issue number and title for each item', () => {
    renderPage()

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

  it('deep-links each item to /:projectName/issues/:number', () => {
    renderPage()

    const links = screen.getAllByTestId('inbox-item-link')
    expect(links.length).toBeGreaterThan(0)
    const hrefs = links.map((l) => l.getAttribute('href'))
    expect(hrefs).toEqual(expect.arrayContaining(['/demo/issues/42', '/demo/issues/13', '/demo/issues/5', '/demo/issues/9', '/demo/issues/7']))
  })

  it('distinguishes unread items with data-read="false" and read items with data-read="true"', () => {
    renderPage()

    const unread = document.querySelector('[data-item-id="inb-1"]') as HTMLElement
    const read = document.querySelector('[data-item-id="inb-2"]') as HTMLElement
    expect(unread).not.toBeNull()
    expect(read).not.toBeNull()
    expect(unread.getAttribute('data-read')).toBe('false')
    expect(read.getAttribute('data-read')).toBe('true')
  })

  it('applies a distinct border/shadow to unread rows', () => {
    renderPage()

    const unread = document.querySelector('[data-item-id="inb-1"]') as HTMLElement
    const read = document.querySelector('[data-item-id="inb-2"]') as HTMLElement
    expect(unread.className).toContain('border-blue-300')
    expect(read.className).not.toContain('border-blue-300')
    expect(read.className).toContain('border-gray-200')
  })

  it('shows a Mark read control only for unread items', () => {
    renderPage()

    const markReadButtons = screen.getAllByTestId('inbox-item-mark-read')
    expect(markReadButtons).toHaveLength(4)
  })

  it('shows a relative-time string on each item', () => {
    renderPage()

    const timeNodes = screen.getAllByTestId('inbox-item-time')
    expect(timeNodes).toHaveLength(5)
    expect(timeNodes[0].textContent).toMatch(/ago|just now/)
  })

  it('renders the product-facing text from kind -> template, not raw event names', () => {
    renderPage()

    expect(screen.getByText(/Issue #42 workflow failed/)).toBeInTheDocument()
    expect(screen.getByText(/Issue #5 needs approval/)).toBeInTheDocument()
    expect(screen.getByText(/Issue #9 started/)).toBeInTheDocument()
    expect(screen.getByText(/Issue #7 completed/)).toBeInTheDocument()
  })

  it('does not render any raw event type strings on the page', () => {
    renderPage()

    const container = screen.getByTestId('inbox-list')
    expect(container.textContent).not.toContain('workflow_failed')
    expect(container.textContent).not.toContain('approval_requested')
    expect(container.textContent).not.toContain('issue_started')
    expect(container.textContent).not.toContain('issue_completed')
  })

  it('displays the unread count in the summary line', () => {
    renderPage()

    expect(screen.getByTestId('inbox-summary').textContent).toContain('4 unread of 5')
  })
})

describe('InboxPage mutations', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useInbox.mockReturnValue({ data: [unreadFailure, approval], isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('invokes mark-read mutation with the item id when Mark read is clicked', () => {
    const markReadMutate = vi.fn()
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: markReadMutate, isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)

    renderPage()

    fireEvent.click(screen.getAllByTestId('inbox-item-mark-read')[0])

    expect(markReadMutate).toHaveBeenCalledWith('inb-1')
  })

  it('invokes the archive mutation with the item id when Archive is clicked', () => {
    const archiveMutate = vi.fn()
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: archiveMutate, isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)

    renderPage()

    fireEvent.click(screen.getAllByTestId('inbox-item-archive')[0])

    expect(archiveMutate).toHaveBeenCalledWith('inb-1')
  })

  it('invokes the mark-all-read mutation when Mark all read is clicked', () => {
    const markAllMutate = vi.fn()
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: markAllMutate, isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)

    renderPage()

    fireEvent.click(screen.getByTestId('inbox-mark-all-read'))

    expect(markAllMutate).toHaveBeenCalledTimes(1)
  })

  it('disables Mark all read while the mutation is pending', () => {
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: true })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)

    renderPage()

    const button = screen.getByTestId('inbox-mark-all-read')
    expect(button).toBeDisabled()
    expect(button.textContent).toBe('Marking...')
  })
})

describe('InboxPage live refresh', () => {
  afterEach(() => {
    cleanup()
  })

  it('subscribes to the inbox live refresh hook on mount', () => {
    mocks.useInbox.mockReturnValue({ data: [], isLoading: false })
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)

    renderPage()

    expect(mocks.useInboxLiveRefresh).toHaveBeenCalled()
  })

  it('renders directly from the API query result and does not synthesize items locally', () => {
    const seed: InboxItem[] = [
      makeItem({ itemId: 'inb-a', notificationKind: NOTIFICATION_KINDS.IssueStarted, issueNumber: 1, isRead: false }),
    ]
    mocks.useInbox.mockReturnValue({ data: seed, isLoading: false })
    mocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useInboxLiveRefresh.mockReturnValue(undefined)

    renderPage()

    expect(screen.getAllByTestId('inbox-item')).toHaveLength(1)
  })
})
