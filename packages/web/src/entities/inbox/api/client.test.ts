import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { archiveInboxItem, getInbox, getInboxSubscription, getUnreadInboxCount, markAllInboxRead, markInboxItemRead, updateInboxSubscription } from './client'

useMswServer()

function successResponse(payload: unknown) {
  return HttpResponse.json({ success: true, data: payload })
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

describe('getInbox', () => {
  it('requests GET /api/projects/{ref}/inbox', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/inbox', ({ request }) => {
        requests.push(request)
        return successResponse([])
      }),
    )

    const items = await getInbox('proj-1')

    expect(items).toEqual([])
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/inbox')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('returns items when unread timestamp fields are omitted by the server', async () => {
    const payload = [
      {
        itemId: 'inb-1',
        notificationKind: 'workflow_failed',
        issueNumber: 42,
        issueTitle: 'Snapshot me',
        createdAt: '2026-06-29T00:00:00.000Z',
        isRead: false,
        isArchived: false,
      },
    ]
    server.use(
      http.get('*/api/projects/:projectId/inbox', () => successResponse(payload)),
    )

    const items = await getInbox('proj-1')

    expect(items).toEqual(payload)
  })
})

describe('getUnreadInboxCount', () => {
  it('requests only the project-scoped unread count and forwards AbortSignal', async () => {
    const controller = new AbortController()
    let observedRequest: Request | undefined
    server.use(
      http.get('*/api/projects/:projectId/inbox/unread-count', ({ request }) => {
        observedRequest = request
        return successResponse({ unreadCount: 3 })
      }),
    )

    await expect(getUnreadInboxCount('proj-1', controller.signal)).resolves.toEqual({ unreadCount: 3 })
    expect(requestPath(observedRequest!)).toBe('/api/projects/proj-1/inbox/unread-count')
    expect(observedRequest!.signal.aborted).toBe(false)
    controller.abort()
    expect(observedRequest!.signal.aborted).toBe(true)
  })
})

describe('markInboxItemRead', () => {
  it('POSTs to /api/projects/{ref}/inbox/{itemId}/read with no body', async () => {
    const requests: Request[] = []
    const bodies: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/inbox/:itemId/read', async ({ request }) => {
        requests.push(request)
        bodies.push(await request.text())
        return successResponse({ itemId: 'inb-1', read: true })
      }),
    )

    const response = await markInboxItemRead('inb-1', 'proj-1')

    expect(response).toEqual({ itemId: 'inb-1', read: true })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/inbox/inb-1/read')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    expect(bodies).toEqual([''])
  })
})

describe('markAllInboxRead', () => {
  it('POSTs to /api/projects/{ref}/inbox/read-all with no body', async () => {
    const requests: Request[] = []
    const bodies: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/inbox/read-all', async ({ request }) => {
        requests.push(request)
        bodies.push(await request.text())
        return successResponse({ projectId: 'proj-1', marked: 5 })
      }),
    )

    const response = await markAllInboxRead('proj-1')

    expect(response).toEqual({ projectId: 'proj-1', marked: 5 })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/inbox/read-all')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    expect(bodies).toEqual([''])
  })
})

describe('archiveInboxItem', () => {
  it('POSTs to /api/projects/{ref}/inbox/{itemId}/archive with no body', async () => {
    const requests: Request[] = []
    const bodies: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/inbox/:itemId/archive', async ({ request }) => {
        requests.push(request)
        bodies.push(await request.text())
        return successResponse({ itemId: 'inb-1', archived: true })
      }),
    )

    const response = await archiveInboxItem('inb-1', 'proj-1')

    expect(response).toEqual({ itemId: 'inb-1', archived: true })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/inbox/inb-1/archive')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    expect(bodies).toEqual([''])
  })
})

describe('getInboxSubscription', () => {
  it('requests GET /api/projects/{ref}/inbox/subscription', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/inbox/subscription', ({ request }) => {
        requests.push(request)
        return successResponse({ workflow_failed: true, approval_requested: true, issue_started: true, issue_completed: true })
      }),
    )

    const subscription = await getInboxSubscription('proj-1')

    expect(subscription).toEqual({ workflow_failed: true, approval_requested: true, issue_started: true, issue_completed: true })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/inbox/subscription')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })
})

describe('updateInboxSubscription', () => {
  it('PUTs to /api/projects/{ref}/inbox/subscription with the full subscription body', async () => {
    const requests: Request[] = []
    server.use(
      http.put('*/api/projects/:projectId/inbox/subscription', ({ request }) => {
        requests.push(request)
        return successResponse({ workflow_failed: false, approval_requested: true, issue_started: true, issue_completed: true })
      }),
    )

    const result = await updateInboxSubscription('proj-1', {
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })

    expect(result).toEqual({ workflow_failed: false, approval_requested: true, issue_started: true, issue_completed: true })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/inbox/subscription')
    expect(requests[0].method).toBe('PUT')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    await expect(requests[0].json()).resolves.toEqual({
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })
  })
})
