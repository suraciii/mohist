import { afterEach, describe, expect, it, vi } from 'vitest'
import { archiveInboxItem, getInbox, getInboxSubscription, markAllInboxRead, markInboxItemRead, updateInboxSubscription } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function mockJsonResponse(payload: unknown, status: number = 200): Response {
  return new Response(JSON.stringify({ success: true, data: payload }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('getInbox', () => {
  it('requests GET /api/projects/{ref}/inbox', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    const items = await getInbox('proj-1')

    expect(items).toEqual([])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/inbox')
    expect(calledInit?.method).toBeUndefined()
  })

  it('returns items when unread timestamp fields are omitted by the server', async () => {
    const payload = [
      {
        itemId: 'inb-1',
        notificationKind: 'workflow_failed',
        issueId: 'issue-1',
        issueNumber: 42,
        issueTitle: 'Snapshot me',
        createdAt: '2026-06-29T00:00:00.000Z',
        isRead: false,
        isArchived: false,
      },
    ]
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse(payload)))

    const items = await getInbox('proj-1')

    expect(items).toEqual(payload)
  })
})

describe('markInboxItemRead', () => {
  it('POSTs to /api/projects/{ref}/inbox/{itemId}/read with no body', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse({ itemId: 'inb-1', read: true }))
    vi.stubGlobal('fetch', fetchMock)

    const response = await markInboxItemRead('inb-1', 'proj-1')

    expect(response).toEqual({ itemId: 'inb-1', read: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/inbox/inb-1/read')
    expect(calledInit?.method).toBe('POST')
  })
})

describe('markAllInboxRead', () => {
  it('POSTs to /api/projects/{ref}/inbox/read-all with no body', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse({ projectId: 'proj-1', marked: 5 }))
    vi.stubGlobal('fetch', fetchMock)

    const response = await markAllInboxRead('proj-1')

    expect(response).toEqual({ projectId: 'proj-1', marked: 5 })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/inbox/read-all')
    expect(calledInit?.method).toBe('POST')
  })
})

describe('archiveInboxItem', () => {
  it('POSTs to /api/projects/{ref}/inbox/{itemId}/archive with no body', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse({ itemId: 'inb-1', archived: true }))
    vi.stubGlobal('fetch', fetchMock)

    const response = await archiveInboxItem('inb-1', 'proj-1')

    expect(response).toEqual({ itemId: 'inb-1', archived: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/inbox/inb-1/archive')
    expect(calledInit?.method).toBe('POST')
  })
})

describe('getInboxSubscription', () => {
  it('requests GET /api/projects/{ref}/inbox/subscription', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse({ workflow_failed: true, approval_requested: true, issue_started: true, issue_completed: true }))
    vi.stubGlobal('fetch', fetchMock)

    const subscription = await getInboxSubscription('proj-1')

    expect(subscription).toEqual({ workflow_failed: true, approval_requested: true, issue_started: true, issue_completed: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/inbox/subscription')
    expect(calledInit?.method).toBeUndefined()
  })
})

describe('updateInboxSubscription', () => {
  it('PUTs to /api/projects/{ref}/inbox/subscription with the full subscription body', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse({ workflow_failed: false, approval_requested: true, issue_started: true, issue_completed: true }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await updateInboxSubscription('proj-1', {
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })

    expect(result).toEqual({ workflow_failed: false, approval_requested: true, issue_started: true, issue_completed: true })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/inbox/subscription')
    expect(calledInit?.method).toBe('PUT')
    expect(JSON.parse(calledInit?.body as string)).toEqual({
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })
  })
})
