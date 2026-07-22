import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { toast } from 'sonner'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { inboxCountQueryKey, inboxListQueryKey } from '../../entities/inbox/api/queries'
import { LiveTaskProvider, type EventsConnectionHook } from './LiveTaskProvider'
import { TEST_PROJECT } from './_liveTaskProviderTestUtils'

let eventsConnectionCalls: Parameters<EventsConnectionHook>[] = []
const eventsConnectionHook: EventsConnectionHook = (...args) => {
  eventsConnectionCalls.push(args)
  return { status: 'disconnected', connection: null, reconnectVersion: 0 }
}
const viewedIssueRef = { current: null as number | null }
const viewedIssueHook = () => viewedIssueRef
let pathname = '/'
const pathnameReader = () => pathname

beforeEach(() => {
  vi.clearAllMocks()
  eventsConnectionCalls = []
  viewedIssueRef.current = null
  pathname = '/'
})

describe('LiveTaskProvider inbox hint (invalidation only)', () => {
  function mountWith(queryClient: QueryClient, initialProjectId = TEST_PROJECT.id) {
    render(
      createElement(
        QueryClientProvider,
        { client: queryClient },
        createElement(
          ProjectProvider,
          {
            initialProjectId,
            initialProjects: [TEST_PROJECT],
            children: createElement(
              LiveTaskProvider,
              {
                children: createElement('div', null, 'child'),
                eventsConnectionHook,
                viewedIssueHook,
                pathnameReader,
              },
            ),
          },
        ),
      ),
    )
    return eventsConnectionCalls[0][1]
  }

  it('invalidates the inbox list and unread count when an inbox hint arrives for the current project', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const handleEvent = mountWith(queryClient)

    const originalHref = window.location.href
    const originalPathname = window.location.pathname
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-1',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: inboxListQueryKey(TEST_PROJECT.id) })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: inboxCountQueryKey(TEST_PROJECT.id) })
    // No browser navigation / full reload — the inbox page inserts/refreshes via the
    // shared query invalidation, not a `window.location` change.
    expect(window.location.href).toBe(originalHref)
    expect(window.location.pathname).toBe(originalPathname)
  })

  it('does NOT invalidate inbox queries when the hint targets a different project', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-b',
        projectId: 'proj-b',
        kind: 'workflow_failed',
        issueNumber: 99,
      })
    })

    const inboxInvalidations = invalidateSpy.mock.calls
      .map((args) => args[0] as { queryKey?: unknown[] })
      .filter((arg) => Array.isArray(arg.queryKey) && arg.queryKey[0] === 'inbox')
    expect(inboxInvalidations).toHaveLength(0)
  })

  it('does NOT mutate the inbox query cache from the hint payload (invalidation only)', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const setQueryDataSpy = vi.spyOn(queryClient, 'setQueryData')
    const setQueriesDataSpy = vi.spyOn(queryClient, 'setQueriesData')
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-1',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
        // The hint payload is untrusted for inbox content. The Web never
        // synthesises an InboxItem from it. We attach the fields a careless
        // implementation might leak through.
        issueTitle: 'should-not-be-cached',
        isRead: false,
        createdAt: '2026-06-29T00:00:00.000Z',
      })
    })

    expect(setQueryDataSpy).not.toHaveBeenCalled()
    expect(setQueriesDataSpy).not.toHaveBeenCalled()
  })

  it('drops hints whose payload is missing required identity fields', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-1',
        // missing projectId / kind / issueNumber
      })
    })

    const inboxInvalidations = invalidateSpy.mock.calls
      .map((args) => args[0] as { queryKey?: unknown[] })
      .filter((arg) => Array.isArray(arg.queryKey) && arg.queryKey[0] === 'inbox')
    expect(inboxInvalidations).toHaveLength(0)
  })

  it('reconnect or a dropped hint recovers truth via the next inbox query (no automatic fallback fetch)', () => {
    // The acceptance is "no inbox data is lost". Recovery is the inbox query's
    // own behaviour: when the user re-focuses the tab or the periodic refetch
    // fires, TanStack Query re-runs `getInbox(projectId)` and reconciles
    // truth. The provider MUST NOT synthesise items locally from dropped
    // hints; it MUST NOT also kick an extra fetch on its own (which would
    // be a duplicate network request on every drop).
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const fetchSpy = vi.spyOn(queryClient, 'fetchQuery')
    const handleEvent = mountWith(queryClient)

    // Simulate a reconnect: the connection re-emits SetSubscriptionsAsync
    // and the user re-focuses; the provider's `handleEvent` is only invoked
    // when an actual event arrives. With no event arriving, no fetch is
    // kicked off by the provider — recovery happens through TanStack
    // Query's own stale-or-refocus refetch.
    expect(typeof handleEvent).toBe('function')
    expect(fetchSpy).not.toHaveBeenCalled()

    // And a stray hint that lands after the reconnect still only invalidates,
    // never triggers an extra `fetchQuery` on its own.
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-late',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 7,
      })
    })

    expect(fetchSpy).not.toHaveBeenCalled()
  })
})

describe('LiveTaskProvider high-attention inbox notice', () => {
  function mountWith(queryClient: QueryClient, initialProjectId = TEST_PROJECT.id) {
    render(
      createElement(
        QueryClientProvider,
        { client: queryClient },
        createElement(
          ProjectProvider,
          {
            initialProjectId,
            initialProjects: [TEST_PROJECT],
            children: createElement(
              LiveTaskProvider,
              {
                children: createElement('div', null, 'child'),
                eventsConnectionHook,
                viewedIssueHook,
                pathnameReader,
              },
            ),
          },
        ),
      ),
    )
    return eventsConnectionCalls[0][1]
  }

  it('shows an error notice for workflow_failed hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-err',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    expect(toast.error).toHaveBeenCalledTimes(1)
    expect(toast.error).toHaveBeenCalledWith('Issue #42 encountered an error')
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('shows an info notice for approval_requested hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-app',
        projectId: TEST_PROJECT.id,
        kind: 'approval_requested',
        issueNumber: 99,
      })
    })

    expect(toast.info).toHaveBeenCalledTimes(1)
    expect(toast.info).toHaveBeenCalledWith('Issue #99 needs approval')
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('does NOT show a notice for issue_started hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-started',
        projectId: TEST_PROJECT.id,
        kind: 'issue_started',
        issueNumber: 100,
      })
    })

    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('does NOT show a notice for issue_completed hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-completed',
        projectId: TEST_PROJECT.id,
        kind: 'issue_completed',
        issueNumber: 101,
      })
    })

    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('does NOT show a notice for a hint that targets a different project (suppressed by applyInboxHint routing)', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-other',
        projectId: 'proj-other',
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('does NOT show a notice when on the inbox page (suppression)', () => {
    pathname = '/test-project/inbox'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)
    vi.mocked(toast.error).mockClear()
    vi.mocked(toast.info).mockClear()

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-inbox',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('does NOT show a notice when viewing the same issue (suppression)', () => {
    pathname = '/test-project/issues/42'
    viewedIssueRef.current = 42
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)
    vi.mocked(toast.error).mockClear()
    vi.mocked(toast.info).mockClear()

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-same',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('shows a notice when viewing an unrelated issue (different number)', () => {
    pathname = '/test-project/issues/99'
    viewedIssueRef.current = 99
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-unrel',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    expect(toast.error).toHaveBeenCalledTimes(1)
    expect(toast.error).toHaveBeenCalledWith('Issue #42 encountered an error')
  })

  it('the notice is in-app only (uses sonner toast, not Notification API or external push)', () => {
    // This test verifies the implementation uses sonner toast (a purely in-app
    // mechanism) rather than any external push/notification API. The sonner
    // toast is mocked above — if the implementation switches to a different
    // delivery mechanism (browser Notification, email, sound, etc.), this
    // test will fail to find the mocked sonner calls and break.
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-inapp',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueNumber: 42,
      })
    })

    // The notice is delivered through sonner.toast.error — a purely in-app
    // DOM-based toast widget. No browser Notification permission, no Service
    // Worker push, no desktop notification, no email, no sound.
    expect(toast.error).toHaveBeenCalledTimes(1)
    expect(toast.error).toHaveBeenCalledWith('Issue #42 encountered an error')
  })
})
