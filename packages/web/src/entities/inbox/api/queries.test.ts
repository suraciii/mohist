import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  archiveInboxItemMutationOptions,
  inboxQueryKey,
  inboxQueryOptions,
  inboxSubscriptionQueryOptions,
  invalidateInbox,
  markAllInboxReadMutationOptions,
  markInboxItemReadMutationOptions,
  subscriptionQueryKey,
  unreadInboxCountQueryOptions,
  updateInboxSubscriptionMutationOptions,
} from './queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

describe('inboxQueryKey', () => {
  it('returns the project-scoped key when projectId is set', () => {
    expect(inboxQueryKey('proj-1')).toEqual(['inbox-list', 'proj-1'])
  })

  it('returns the shared key when projectId is null', () => {
    expect(inboxQueryKey(null)).toEqual(['inbox-list'])
  })

  it('returns the shared key when projectId is undefined', () => {
    expect(inboxQueryKey(undefined)).toEqual(['inbox-list'])
  })
})

describe('invalidateInbox', () => {
  it('invalidates the project-scoped inbox query when projectId is set', () => {
    const qc = createInvalidationClient()
    invalidateInbox(qc, 'proj-1')
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-list', 'proj-1'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-count', 'proj-1'] })
  })

  it('invalidates the shared ["inbox"] query prefix when projectId is absent', () => {
    const qc = createInvalidationClient()
    invalidateInbox(qc, null)
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-list'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-count'] })
  })
})

describe('inboxQueryOptions', () => {
  it('uses the query key ["inbox", projectId] scoped to the project', () => {
    expect(inboxQueryOptions('proj-1').queryKey).toEqual(['inbox-list', 'proj-1'])
  })

  it('forwards a null projectId and disables the query', () => {
    const opts = inboxQueryOptions(null)
    expect(opts.enabled).toBe(false)
    expect(opts.queryKey).toEqual(['inbox-list'])
  })

  it('calls getInbox(projectId) on the queryFn', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/inbox', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: [] })
      }),
    )

    await inboxQueryOptions('proj-abc').queryFn()

    expect(urls).toEqual(['/api/projects/proj-abc/inbox'])
  })

  it('does NOT prefix the query key with ["issues", ...]', () => {
    expect(inboxQueryOptions('proj-1').queryKey[0]).toBe('inbox-list')
    expect(inboxQueryOptions('proj-1').queryKey).not.toContain('issues')
  })
})

describe('unreadInboxCountQueryOptions', () => {
  it('uses a distinct count query key', () => {
    expect(unreadInboxCountQueryOptions('proj-1').queryKey).toEqual(['inbox-count', 'proj-1'])
  })

  it('disables the query when there is no project', () => {
    expect(unreadInboxCountQueryOptions(null).enabled).toBe(false)
  })

  it('selects the count returned by the count endpoint', () => {
    expect(unreadInboxCountQueryOptions('proj-1').select!({ unreadCount: 2 })).toBe(2)
  })

  it('returns 0 when all items are read', () => {
    expect(unreadInboxCountQueryOptions('proj-1').select!({ unreadCount: 0 })).toBe(0)
  })

  it('forwards projectId to getInbox as queryFn', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/inbox/unread-count', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({ success: true, data: { unreadCount: 0 } })
      }),
    )

    await unreadInboxCountQueryOptions('proj-xyz').queryFn()

    expect(urls).toEqual(['/api/projects/proj-xyz/inbox/unread-count'])
  })
})

describe('subscriptionQueryKey', () => {
  it('returns the project-scoped key when projectId is set', () => {
    expect(subscriptionQueryKey('proj-1')).toEqual(['inbox-subscription', 'proj-1'])
  })

  it('returns the shared key when projectId is null', () => {
    expect(subscriptionQueryKey(null)).toEqual(['inbox-subscription'])
  })

  it('returns the shared key when projectId is undefined', () => {
    expect(subscriptionQueryKey(undefined)).toEqual(['inbox-subscription'])
  })
})

describe('inboxSubscriptionQueryOptions', () => {
  it('uses the query key ["inbox-subscription", projectId] scoped to the project', () => {
    expect(inboxSubscriptionQueryOptions('proj-1').queryKey).toEqual(['inbox-subscription', 'proj-1'])
  })

  it('disables the query when there is no project', () => {
    expect(inboxSubscriptionQueryOptions(null).enabled).toBe(false)
  })

  it('calls getInboxSubscription(projectId) on the queryFn', async () => {
    const urls: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/inbox/subscription', ({ request }) => {
        urls.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: { workflow_failed: true, approval_requested: true, issue_started: true, issue_completed: true },
        })
      }),
    )

    await inboxSubscriptionQueryOptions('proj-abc').queryFn()

    expect(urls).toEqual(['/api/projects/proj-abc/inbox/subscription'])
  })
})

describe('markInboxItemReadMutationOptions', () => {
  it('forwards (itemId, projectId) to markInboxItemRead', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/inbox/:itemId/read', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { itemId: 'inb-1', read: true } })
      }),
    )

    await markInboxItemReadMutationOptions('proj-1', createInvalidationClient()).mutationFn('inb-1')

    expect(captured).toEqual([{ url: '/api/projects/proj-1/inbox/inb-1/read', method: 'POST' }])
  })

  it('invalidates the project-scoped inbox query on success', () => {
    const qc = createInvalidationClient()
    markInboxItemReadMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-list', 'proj-1'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-count', 'proj-1'] })
  })

  it('toasts the error message on failure', () => {
    markInboxItemReadMutationOptions('proj-1', createInvalidationClient()).onError(new Error('inbox item gone'))
    expect(toast.error).toHaveBeenCalledWith('inbox item gone')
  })

  it('falls back to "Request failed" on empty error message', () => {
    markInboxItemReadMutationOptions('proj-1', createInvalidationClient()).onError(new Error(''))
    expect(toast.error).toHaveBeenCalledWith('Request failed')
  })
})

describe('markAllInboxReadMutationOptions', () => {
  it('forwards projectId to markAllInboxRead', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/inbox/read-all', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { projectId: 'proj-1', marked: 3 } })
      }),
    )

    await markAllInboxReadMutationOptions('proj-1', createInvalidationClient()).mutationFn()

    expect(captured).toEqual([{ url: '/api/projects/proj-1/inbox/read-all', method: 'POST' }])
  })

  it('invalidates the project-scoped inbox query on success', () => {
    const qc = createInvalidationClient()
    markAllInboxReadMutationOptions('proj-1', qc).onSuccess({ projectId: 'proj-1', marked: 3 })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-list', 'proj-1'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-count', 'proj-1'] })
  })

  it('toasts "Marked N inbox items as read" when items were marked', () => {
    markAllInboxReadMutationOptions('proj-1', createInvalidationClient()).onSuccess({ projectId: 'proj-1', marked: 5 })
    expect(toast.success).toHaveBeenCalledWith('Marked 5 inbox items as read')
  })

  it('does NOT toast success when zero items were marked', () => {
    markAllInboxReadMutationOptions('proj-1', createInvalidationClient()).onSuccess({ projectId: 'proj-1', marked: 0 })
    expect(toast.success).not.toHaveBeenCalled()
  })

  it('toasts the error message on failure', () => {
    markAllInboxReadMutationOptions('proj-1', createInvalidationClient()).onError(new Error('mark all failed'))
    expect(toast.error).toHaveBeenCalledWith('mark all failed')
  })
})

describe('archiveInboxItemMutationOptions', () => {
  it('forwards (itemId, projectId) to archiveInboxItem', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/inbox/:itemId/archive', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({ success: true, data: { itemId: 'inb-1', archived: true } })
      }),
    )

    await archiveInboxItemMutationOptions('proj-1', createInvalidationClient()).mutationFn('inb-1')

    expect(captured).toEqual([{ url: '/api/projects/proj-1/inbox/inb-1/archive', method: 'POST' }])
  })

  it('invalidates the project-scoped inbox query on success', () => {
    const qc = createInvalidationClient()
    archiveInboxItemMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-list', 'proj-1'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-count', 'proj-1'] })
  })

  it('toasts "Inbox item archived" on success', () => {
    archiveInboxItemMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Inbox item archived')
  })

  it('toasts the error message on failure', () => {
    archiveInboxItemMutationOptions('proj-1', createInvalidationClient()).onError(new Error('archive failed'))
    expect(toast.error).toHaveBeenCalledWith('archive failed')
  })
})

describe('updateInboxSubscriptionMutationOptions', () => {
  it('forwards data to updateInboxSubscription', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.put('*/api/projects/:projectId/inbox/subscription', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({
          success: true,
          data: { workflow_failed: false, approval_requested: true, issue_started: true, issue_completed: true },
        })
      }),
    )

    await updateInboxSubscriptionMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      workflow_failed: false,
      approval_requested: true,
      issue_started: true,
      issue_completed: true,
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/inbox/subscription',
        method: 'PUT',
        body: { workflow_failed: false, approval_requested: true, issue_started: true, issue_completed: true },
      },
    ])
  })

  it('invalidates only the subscription query on success (NOT the inbox query)', () => {
    const qc = createInvalidationClient()
    updateInboxSubscriptionMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['inbox-subscription', 'proj-1'] })
    expect(qc.invalidateQueries).not.toHaveBeenCalledWith({ queryKey: ['inbox-list', 'proj-1'] })
  })

  it('toasts the error message on failure', () => {
    updateInboxSubscriptionMutationOptions('proj-1', createInvalidationClient()).onError(new Error('update failed'))
    expect(toast.error).toHaveBeenCalledWith('update failed')
  })

  it('falls back to "Request failed" on empty error message', () => {
    updateInboxSubscriptionMutationOptions('proj-1', createInvalidationClient()).onError(new Error(''))
    expect(toast.error).toHaveBeenCalledWith('Request failed')
  })
})
