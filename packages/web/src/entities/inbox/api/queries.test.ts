import { beforeEach, describe, expect, it, vi } from 'vitest'
import { inboxQueryKey, invalidateInbox, useArchiveInboxItem, useInbox, useMarkAllInboxRead, useMarkInboxItemRead, useUnreadInboxCount } from './queries'
import type { InboxItem } from '../model/types'

const useQueryMock = vi.fn()
const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const getInboxMock = vi.fn()
const markInboxItemReadMock = vi.fn()
const markAllInboxReadMock = vi.fn()
const archiveInboxItemMock = vi.fn()
const toastSuccessMock = vi.fn()
const toastErrorMock = vi.fn()
const invalidateQueriesMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQuery: (...args: unknown[]) => useQueryMock(...args),
  useMutation: (...args: unknown[]) => useMutationMock(...args),
  useQueryClient: () => useQueryClientMock(),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('./client', () => ({
  getInbox: (...args: unknown[]) => getInboxMock(...args),
  markInboxItemRead: (...args: unknown[]) => markInboxItemReadMock(...args),
  markAllInboxRead: (...args: unknown[]) => markAllInboxReadMock(...args),
  archiveInboxItem: (...args: unknown[]) => archiveInboxItemMock(...args),
}))

vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccessMock(...args),
    error: (...args: unknown[]) => toastErrorMock(...args),
  },
}))

beforeEach(() => {
  useQueryMock.mockReset()
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  useProjectMock.mockReset()
  getInboxMock.mockReset()
  markInboxItemReadMock.mockReset()
  markAllInboxReadMock.mockReset()
  archiveInboxItemMock.mockReset()
  toastSuccessMock.mockReset()
  toastErrorMock.mockReset()
  invalidateQueriesMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  useQueryMock.mockReturnValue({ data: [], isLoading: false })
  useMutationMock.mockImplementation((options: unknown) => ({ mutate: vi.fn(), options }))
})

function getLastMutationOptions() {
  const calls = useMutationMock.mock.calls
  const last = calls[calls.length - 1][0] as {
    mutationFn: (...args: unknown[]) => unknown
    onSuccess: (...args: unknown[]) => void
    onError: (...args: unknown[]) => void
  }
  return last
}

describe('inboxQueryKey', () => {
  it('returns the project-scoped key when projectId is set', () => {
    expect(inboxQueryKey('proj-1')).toEqual(['inbox', 'proj-1'])
  })

  it('returns the shared key when projectId is null', () => {
    expect(inboxQueryKey(null)).toEqual(['inbox'])
  })

  it('returns the shared key when projectId is undefined', () => {
    expect(inboxQueryKey(undefined)).toEqual(['inbox'])
  })
})

describe('invalidateInbox', () => {
  it('invalidates the project-scoped inbox query when projectId is set', () => {
    invalidateInbox({ invalidateQueries: invalidateQueriesMock } as unknown as Parameters<typeof invalidateInbox>[0], 'proj-1')

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('invalidates the shared ["inbox"] query prefix when projectId is absent', () => {
    invalidateInbox({ invalidateQueries: invalidateQueriesMock } as unknown as Parameters<typeof invalidateInbox>[0], null)

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['inbox'] })
  })
})

describe('useInbox', () => {
  it('uses the query key ["inbox", projectId] scoped to the project', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useInbox()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['inbox', 'proj-1'])
  })

  it('forwards a null projectId when useProject returns none', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useInbox()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
    expect(config.queryKey).toEqual(['inbox'])
  })

  it('calls getInbox(projectId) on the queryFn', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-abc' })
    getInboxMock.mockResolvedValue([])

    useInbox()

    const config = useQueryMock.mock.calls[0][0]
    await config.queryFn()
    expect(getInboxMock).toHaveBeenCalledWith('proj-abc')
  })

  it('does NOT prefix the query key with ["issues", ...]', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useInbox()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey[0]).toBe('inbox')
    expect(config.queryKey).not.toContain('issues')
  })
})

describe('useMarkInboxItemRead', () => {
  it('forwards (itemId, projectId) to markInboxItemRead', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    markInboxItemReadMock.mockResolvedValue({ itemId: 'inb-1', read: true })

    useMarkInboxItemRead()

    const options = getLastMutationOptions()
    void options.mutationFn('inb-1')

    expect(markInboxItemReadMock).toHaveBeenCalledWith('inb-1', 'proj-1')
  })

  it('invalidates the project-scoped inbox query on success', () => {
    useMarkInboxItemRead()

    const options = getLastMutationOptions()
    options.onSuccess()

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('toasts the error message on failure', () => {
    useMarkInboxItemRead()

    const options = getLastMutationOptions()
    options.onError(new Error('inbox item gone'))

    expect(toastErrorMock).toHaveBeenCalledWith('inbox item gone')
  })

  it('falls back to "Request failed" on empty error message', () => {
    useMarkInboxItemRead()

    const options = getLastMutationOptions()
    options.onError(new Error(''))

    expect(toastErrorMock).toHaveBeenCalledWith('Request failed')
  })
})

describe('useMarkAllInboxRead', () => {
  it('forwards projectId to markAllInboxRead', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    markAllInboxReadMock.mockResolvedValue({ projectId: 'proj-1', marked: 3 })

    useMarkAllInboxRead()

    const options = getLastMutationOptions()
    void options.mutationFn()

    expect(markAllInboxReadMock).toHaveBeenCalledWith('proj-1')
  })

  it('invalidates the project-scoped inbox query on success', () => {
    useMarkAllInboxRead()

    const options = getLastMutationOptions()
    options.onSuccess({ projectId: 'proj-1', marked: 3 })

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('toasts "Marked N inbox items as read" when items were marked', () => {
    useMarkAllInboxRead()

    const options = getLastMutationOptions()
    options.onSuccess({ projectId: 'proj-1', marked: 5 })

    expect(toastSuccessMock).toHaveBeenCalledWith('Marked 5 inbox items as read')
  })

  it('does NOT toast success when zero items were marked', () => {
    useMarkAllInboxRead()

    const options = getLastMutationOptions()
    options.onSuccess({ projectId: 'proj-1', marked: 0 })

    expect(toastSuccessMock).not.toHaveBeenCalled()
  })

  it('toasts the error message on failure', () => {
    useMarkAllInboxRead()

    const options = getLastMutationOptions()
    options.onError(new Error('mark all failed'))

    expect(toastErrorMock).toHaveBeenCalledWith('mark all failed')
  })
})

describe('useArchiveInboxItem', () => {
  it('forwards (itemId, projectId) to archiveInboxItem', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    archiveInboxItemMock.mockResolvedValue({ itemId: 'inb-1', archived: true })

    useArchiveInboxItem()

    const options = getLastMutationOptions()
    void options.mutationFn('inb-1')

    expect(archiveInboxItemMock).toHaveBeenCalledWith('inb-1', 'proj-1')
  })

  it('invalidates the project-scoped inbox query on success', () => {
    useArchiveInboxItem()

    const options = getLastMutationOptions()
    options.onSuccess({ itemId: 'inb-1', archived: true })

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['inbox', 'proj-1'] })
  })

  it('toasts "Inbox item archived" on success', () => {
    useArchiveInboxItem()

    const options = getLastMutationOptions()
    options.onSuccess({ itemId: 'inb-1', archived: true })

    expect(toastSuccessMock).toHaveBeenCalledWith('Inbox item archived')
  })

  it('toasts the error message on failure', () => {
    useArchiveInboxItem()

    const options = getLastMutationOptions()
    options.onError(new Error('archive failed'))

    expect(toastErrorMock).toHaveBeenCalledWith('archive failed')
  })
})

describe('useUnreadInboxCount', () => {
  it('uses the same query key as useInbox', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useUnreadInboxCount()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['inbox', 'proj-1'])
  })

  it('disables the query when there is no project', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useUnreadInboxCount()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('selects the count of unread items from the inbox list', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useUnreadInboxCount()

    const config = useQueryMock.mock.calls[0][0]
    const items: InboxItem[] = [
      { itemId: 'inb-1', notificationKind: 'workflow_failed', issueId: 'i-1', issueNumber: 1, issueTitle: 'A', createdAt: '2024-01-01T00:00:00.000Z', isRead: false, isArchived: false, readAt: null, archivedAt: null },
      { itemId: 'inb-2', notificationKind: 'issue_started', issueId: 'i-2', issueNumber: 2, issueTitle: 'B', createdAt: '2024-01-01T00:00:00.000Z', isRead: true, isArchived: false, readAt: '2024-01-02T00:00:00.000Z', archivedAt: null },
      { itemId: 'inb-3', notificationKind: 'approval_requested', issueId: 'i-3', issueNumber: 3, issueTitle: 'C', createdAt: '2024-01-01T00:00:00.000Z', isRead: false, isArchived: false, readAt: null, archivedAt: null },
    ]
    expect(config.select(items)).toBe(2)
  })

  it('returns 0 when all items are read', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useUnreadInboxCount()

    const config = useQueryMock.mock.calls[0][0]
    const items: InboxItem[] = [
      { itemId: 'inb-1', notificationKind: 'workflow_failed', issueId: 'i-1', issueNumber: 1, issueTitle: 'A', createdAt: '2024-01-01T00:00:00.000Z', isRead: true, isArchived: false, readAt: '2024-01-02T00:00:00.000Z', archivedAt: null },
      { itemId: 'inb-2', notificationKind: 'issue_started', issueId: 'i-2', issueNumber: 2, issueTitle: 'B', createdAt: '2024-01-01T00:00:00.000Z', isRead: true, isArchived: false, readAt: '2024-01-02T00:00:00.000Z', archivedAt: null },
    ]
    expect(config.select(items)).toBe(0)
  })

  it('forwards projectId to getInbox as queryFn', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-xyz' })
    getInboxMock.mockResolvedValue([])

    useUnreadInboxCount()

    const config = useQueryMock.mock.calls[0][0]
    await config.queryFn()
    expect(getInboxMock).toHaveBeenCalledWith('proj-xyz')
  })
})
