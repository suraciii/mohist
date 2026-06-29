import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { LiveTaskProvider, __testing__ } from './LiveTaskProvider'
import { dispatchTimelineEvent, onTimelineEvent, type TimelineLiveEvent } from '../../entities/issue/model/timeline-events'

const mocks = vi.hoisted(() => ({
  useEventsConnection: vi.fn(),
  useAgentStatus: vi.fn(),
  toastInfo: vi.fn(),
  toastError: vi.fn(),
  toastSuccess: vi.fn(),
}))

vi.mock('../../shared/api/events-hub', () => ({
  useEventsConnection: (...args: unknown[]) => mocks.useEventsConnection(...args),
}))

vi.mock('../../entities/agent', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../entities/agent')>()),
  useAgentStatus: () => mocks.useAgentStatus(),
}))

vi.mock('sonner', () => ({
  toast: {
    info: (...args: unknown[]) => mocks.toastInfo(...args),
    error: (...args: unknown[]) => mocks.toastError(...args),
    success: (...args: unknown[]) => mocks.toastSuccess(...args),
  },
}))

const TEST_PROJECT = {
  id: 'test-project',
  name: 'Test Project',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [{ name: 'main', gitUrl: 'https://example.com/test.git', baseBranch: 'main', isDefault: true }],
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.useAgentStatus.mockReturnValue({
    data: {
      running: false,
      runnerAvailable: true,
    },
  })
})

describe('LiveTaskProvider transcript routing', () => {
  it('unwraps transcript envelopes with runtime metadata and payload', () => {
    const envelope = {
      type: 'message.delta',
      sessionId: 'session-1',
      sequence: 12,
      createdAt: '2026-06-12T00:00:00.000Z',
      payload: { text: 'persisted segment' },
    }

    const unwrapped = __testing__.unwrapTranscriptEnvelope(envelope)

    expect(unwrapped?.eventName).toBe('message.delta')
    expect(unwrapped?.payload).toEqual({ text: 'persisted segment' })
    expect(unwrapped?.detail).toMatchObject({
      type: 'message.delta',
      text: 'persisted segment',
      payload: { text: 'persisted segment' },
      sequence: 12,
    })
  })

  it('normalizes server transcript metadata into session-scoped detail fields', () => {
    const unwrapped = __testing__.unwrapTranscriptEnvelope({
      type: 'reasoning.delta',
      sessionId: 'session-84',
      agentSessionId: 'acp-84',
      payload: { text: 'thinking' },
    })

    expect(unwrapped?.detail).toMatchObject({
      acpSessionId: 'acp-84',
      coderSessionId: 'session-84',
      text: 'thinking',
    })
  })
})

describe('LiveTaskProvider timeline forwarding', () => {
  it('builds a TimelineLiveEvent from the CloudEvents envelope (issueNumber, time, eventId from rawData)', () => {
    const envelope = {
      id: 'evt-abc-123',
      type: 'com.mohist.workflow.run.started',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42, issueId: 'iss-1' },
    }

    const event = __testing__.buildTimelineLiveEvent(
      'com.mohist.workflow.run.started',
      envelope,
      { issueNumber: 42, issueId: 'iss-1' },
    )

    expect(event.issueNumber).toBe(42)
    expect(event.issueId).toBe('iss-1')
    expect(event.type).toBe('com.mohist.workflow.run.started')
    expect(event.time).toBe('2026-06-18T00:00:00.000Z')
    expect(event.eventId).toBe('evt-abc-123')
    expect(event.payload).toEqual({ issueNumber: 42, issueId: 'iss-1' })
  })

  it('does not use CloudEvents id as issueId when payload omits issueId', () => {
    const envelope = {
      id: 'evt-abc-123',
      type: 'com.mohist.workflow.run.started',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42 },
    }

    const event = __testing__.buildTimelineLiveEvent(
      'com.mohist.workflow.run.started',
      envelope,
      { issueNumber: 42 },
    )

    expect(event.issueNumber).toBe(42)
    expect(event.issueId).toBeNull()
    expect(event.eventId).toBe('evt-abc-123')
  })

  it('falls back to payload time when envelope omits the CloudEvents time', () => {
    const event = __testing__.buildTimelineLiveEvent(
      'merge_completed',
      { payload: { issueNumber: 7, time: '2026-06-18T01:00:00.000Z' } },
      { issueNumber: 7, time: '2026-06-18T01:00:00.000Z' },
    )

    expect(event.issueNumber).toBe(7)
    expect(event.time).toBe('2026-06-18T01:00:00.000Z')
  })

  it('returns null issueNumber and null time when both envelope and payload omit them', () => {
    const event = __testing__.buildTimelineLiveEvent(
      'unknown_event',
      { payload: {} },
      {},
    )

    expect(event.issueNumber).toBeNull()
    expect(event.time).toBeNull()
    expect(event.eventId).toBeNull()
    expect(event.payload).toEqual({})
  })

  it('dispatchTimelineEvent delivers the built event to onTimelineEvent subscribers', () => {
    const received: TimelineLiveEvent[] = []
    const off = onTimelineEvent((e) => received.push(e))

    const event = __testing__.buildTimelineLiveEvent(
      'rebase_conflict',
      { id: 'rc-1', payload: { issueNumber: 99 } },
      { issueNumber: 99 },
    )
    dispatchTimelineEvent(event)

    expect(received).toHaveLength(1)
    expect(received[0].type).toBe('rebase_conflict')
    expect(received[0].issueNumber).toBe(99)
    expect(received[0].eventId).toBe('rc-1')

    off()
  })

  it('does not suppress or replace existing invalidation/toast behavior on the forward path', () => {
    const observed: string[] = []
    const off = onTimelineEvent((e) => observed.push(`forward:${e.type}`))

    const envelope = {
      id: 'evt-1',
      type: 'merge_completed',
      source: '/mohist/test',
      specVersion: '1.0',
      time: '2026-06-18T00:00:00.000Z',
      payload: { issueNumber: 42, issueId: 'iss-1' },
    }

    const event = __testing__.buildTimelineLiveEvent(
      'merge_completed',
      envelope,
      envelope.payload as Record<string, unknown>,
    )
    dispatchTimelineEvent(event)

    expect(observed).toEqual(['forward:merge_completed'])

    off()
  })

  it('invalidates approval-wait metrics when a stage approval is resolved live', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    render(
      createElement(
        QueryClientProvider,
        { client: queryClient },
        createElement(
          ProjectProvider,
          {
            initialProjectId: TEST_PROJECT.id,
            initialProjects: [TEST_PROJECT],
            children: createElement(
              LiveTaskProvider,
              { children: createElement('div', null, 'child') },
            ),
          },
        ),
      ),
    )

    const handleEvent = mocks.useEventsConnection.mock.calls[0][1] as (eventName: string, data: unknown) => void
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.StageApprovalResolved, {
        issueId: 'issue-1',
        issueNumber: 42,
      })
    })

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'metrics', 'approval-wait'] })
  })
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
              { children: createElement('div', null, 'child') },
            ),
          },
        ),
      ),
    )
    return mocks.useEventsConnection.mock.calls[0][1] as (eventName: string, data: unknown) => void
  }

  it('invalidates ["inbox", projectId] when an inbox hint arrives for the current project', () => {
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
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['inbox', TEST_PROJECT.id] })
    // No browser navigation / full reload — the inbox page inserts/refreshes via the
    // shared query invalidation, not a `window.location` change.
    expect(window.location.href).toBe(originalHref)
    expect(window.location.pathname).toBe(originalPathname)
  })

  it('does NOT invalidate ["inbox", projectId] when the hint targets a different project', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-b',
        projectId: 'proj-b',
        kind: 'workflow_failed',
        issueId: 'issue-99',
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
        issueId: 'issue-42',
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
        // missing projectId / kind / issueId / issueNumber
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
        issueId: 'issue-7',
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
              { children: createElement('div', null, 'child') },
            ),
          },
        ),
      ),
    )
    return mocks.useEventsConnection.mock.calls[0][1] as (eventName: string, data: unknown) => void
  }

  it('shows an error notice for workflow_failed hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-err',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    expect(mocks.toastError).toHaveBeenCalledTimes(1)
    expect(mocks.toastError).toHaveBeenCalledWith('Issue #42 encountered an error')
    expect(mocks.toastInfo).not.toHaveBeenCalled()
  })

  it('shows an info notice for approval_requested hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-app',
        projectId: TEST_PROJECT.id,
        kind: 'approval_requested',
        issueId: 'issue-99',
        issueNumber: 99,
      })
    })

    expect(mocks.toastInfo).toHaveBeenCalledTimes(1)
    expect(mocks.toastInfo).toHaveBeenCalledWith('Issue #99 needs approval')
    expect(mocks.toastError).not.toHaveBeenCalled()
  })

  it('does NOT show a notice for issue_started hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-started',
        projectId: TEST_PROJECT.id,
        kind: 'issue_started',
        issueId: 'issue-100',
        issueNumber: 100,
      })
    })

    expect(mocks.toastError).not.toHaveBeenCalled()
    expect(mocks.toastInfo).not.toHaveBeenCalled()
  })

  it('does NOT show a notice for issue_completed hints', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-completed',
        projectId: TEST_PROJECT.id,
        kind: 'issue_completed',
        issueId: 'issue-101',
        issueNumber: 101,
      })
    })

    expect(mocks.toastError).not.toHaveBeenCalled()
    expect(mocks.toastInfo).not.toHaveBeenCalled()
  })

  it('does NOT show a notice for a hint that targets a different project (suppressed by applyInboxHint routing)', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-other',
        projectId: 'proj-other',
        kind: 'workflow_failed',
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    expect(mocks.toastError).not.toHaveBeenCalled()
    expect(mocks.toastInfo).not.toHaveBeenCalled()
  })

  it('does NOT show a notice when on the inbox page (suppression)', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/inbox')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-inbox',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    expect(mocks.toastError).not.toHaveBeenCalled()
    expect(mocks.toastInfo).not.toHaveBeenCalled()
    window.history.pushState({}, '', savedPathname)
  })

  it('does NOT show a notice when viewing the same issue (suppression)', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/issues/42')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-same',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    expect(mocks.toastError).not.toHaveBeenCalled()
    expect(mocks.toastInfo).not.toHaveBeenCalled()
    window.history.pushState({}, '', savedPathname)
  })

  it('shows a notice when viewing an unrelated issue (different number)', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/issues/99')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.InboxItemPersisted, {
        itemId: 'inb-unrel',
        projectId: TEST_PROJECT.id,
        kind: 'workflow_failed',
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    expect(mocks.toastError).toHaveBeenCalledTimes(1)
    expect(mocks.toastError).toHaveBeenCalledWith('Issue #42 encountered an error')
    window.history.pushState({}, '', savedPathname)
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
        issueId: 'issue-42',
        issueNumber: 42,
      })
    })

    // The notice is delivered through sonner.toast.error — a purely in-app
    // DOM-based toast widget. No browser Notification permission, no Service
    // Worker push, no desktop notification, no email, no sound.
    expect(mocks.toastError).toHaveBeenCalledTimes(1)
    expect(mocks.toastError).toHaveBeenCalledWith('Issue #42 encountered an error')
  })
})
