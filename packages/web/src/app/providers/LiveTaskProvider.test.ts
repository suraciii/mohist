import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { LiveTaskProvider, __testing__ } from './LiveTaskProvider'
import { dispatchTimelineEvent, onTimelineEvent, type TimelineLiveEvent } from '../../entities/issue/model/timeline-events'
import { onRebaseEvent, type RebaseEvent } from '../../entities/issue/model/rebase-events'
import type { Issue } from '../../entities/issue'
import { IssueStatus, IssueHealth } from '../../entities/issue/model/issue'
import { useLiveTask } from '../../entities/issue/model/live-task'

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

/**
 * Pin the currently-untested branches of the in-file
 * `handleReverseDnsIntegrationOutcome` (rebase-completed / merge-success /
 * rebase-conflict / merge-failure / no-match) BEFORE the refactor in this
 * issue moves the function. These are characterization tests against the
 * current implementation: any green state here MUST stay green unchanged
 * after the extraction and decoupling in later tasks. See design.md#D2.
 */
describe('LiveTaskProvider reverse-DNS integration outcome (D2 test-first)', () => {
  function makeIssue(id: string, number: number): Issue {
    return {
      id,
      number,
      title: `Issue ${number}`,
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      projectId: TEST_PROJECT.id,
      labels: {},
      createdAt: '2024-01-01T00:00:00.000Z',
      updatedAt: '2024-01-01T00:00:00.000Z',
      isDraft: false,
      canStart: true,
      blocker: null,
    }
  }

  function mountWith(queryClient: QueryClient, stateProbe?: { current: { rebaseConflict: unknown } | null }) {
    const probe = stateProbe ?? { current: null }
    function StateProbe(): null {
      probe.current = useLiveTask()
      return null
    }
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
              {
                children: stateProbe === undefined
                  ? createElement('div', null, 'child')
                  : createElement(StateProbe),
              },
            ),
          },
        ),
      ),
    )
    return mocks.useEventsConnection.mock.calls[0][1] as (eventName: string, data: unknown) => void
  }

  it('clears rebase conflict, dispatches rebase_completed, and invalidates ["issues"] on IssueCompleted + rebase payload', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-rebase', 7)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const stateProbe: { current: { rebaseConflict: unknown } | null } = { current: null }
    const handleEvent = mountWith(queryClient, stateProbe)

    // First seed the conflict via a rebase-failed event so the subsequent
    // rebase-completed has something to clear.
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
        issueId: 'iss-rebase',
        issueNumber: 7,
        outcome: 'rebase_conflict',
        conflicts: ['only-seeded'],
      })
    })
    expect(stateProbe.current?.rebaseConflict).toMatchObject({
      issueNumber: 7,
      conflicts: ['only-seeded'],
      status: 'failed',
    })

    // Reset toast mocks so the assertions below focus on the
    // rebase-completed path itself, not the seeding step that produced
    // its own toast.error('Rebase conflict on Issue #N').
    mocks.toastInfo.mockClear()
    mocks.toastError.mockClear()
    mocks.toastSuccess.mockClear()

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
        issueId: 'iss-rebase',
        issueNumber: 7,
        outcome: 'rebase_completed',
        rebased: true,
      })
    })

    expect(rebaseEvents).toContainEqual({ type: 'rebase_completed', issueNumber: 7, rebased: true })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    // Issue-arm default fallback only fires when the outcome handler returns false;
    // the rebase-completed arm returns true and breaks, so the detail invalidation
    // is intentionally NOT issued. Pin that.
    const detailInvalidations = invalidateSpy.mock.calls
      .map((args) => args[0] as { queryKey?: unknown[] })
      .filter((arg) => Array.isArray(arg.queryKey)
        && arg.queryKey[0] === 'issues'
        && arg.queryKey[1] === 'detail')
    expect(detailInvalidations).toHaveLength(0)
    expect(mocks.toastSuccess).not.toHaveBeenCalled()
    expect(mocks.toastError).not.toHaveBeenCalled()
    // The conflict was cleared.
    expect(stateProbe.current?.rebaseConflict).toBeNull()

    offRebase()
  })

  it('fires toast.success("Issue #N merged successfully") and invalidates ["issues"] on IssueCompleted + merge payload', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-merge', 13)])

    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
        issueId: 'iss-merge',
        issueNumber: 13,
        outcome: 'merge_completed',
      })
    })

    expect(mocks.toastSuccess).toHaveBeenCalledTimes(1)
    expect(mocks.toastSuccess).toHaveBeenCalledWith('Issue #13 merged successfully')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(mocks.toastError).not.toHaveBeenCalled()
  })

  it('sets rebaseConflict, dispatches rebase_conflict, fires toast.error, and invalidates ["issues"] on WorkflowRunFailed + rebase payload', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-conflict', 21)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const stateProbe: { current: { rebaseConflict: unknown } | null } = { current: null }
    const handleEvent = mountWith(queryClient, stateProbe)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
        issueId: 'iss-conflict',
        issueNumber: 21,
        outcome: 'rebase_conflict',
        conflicts: ['src/a.ts', 'src/b.ts'],
        error: 'CONFLICT (content): Merge conflict in src/a.ts',
      })
    })

    expect(rebaseEvents).toEqual([
      {
        type: 'rebase_conflict',
        issueNumber: 21,
        conflicts: ['src/a.ts', 'src/b.ts'],
        status: 'failed',
        error: 'CONFLICT (content): Merge conflict in src/a.ts',
      },
    ])
    expect(mocks.toastError).toHaveBeenCalledTimes(1)
    expect(mocks.toastError).toHaveBeenCalledWith('Rebase conflict on Issue #21')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(mocks.toastSuccess).not.toHaveBeenCalled()
    expect(stateProbe.current?.rebaseConflict).toMatchObject({
      issueNumber: 21,
      conflicts: ['src/a.ts', 'src/b.ts'],
      status: 'failed',
      error: 'CONFLICT (content): Merge conflict in src/a.ts',
    })

    offRebase()
  })

  it('sets rebaseConflict and dispatches rebase_conflict on StageFailed + rebase payload (parallel arm to WorkflowRunFailed)', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-stage', 33)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.StageFailed, {
        issueId: 'iss-stage',
        issueNumber: 33,
        outcome: 'rebase_aborted',
        conflicts: [],
      })
    })

    expect(rebaseEvents).toHaveLength(1)
    expect(rebaseEvents[0]).toMatchObject({
      type: 'rebase_conflict',
      issueNumber: 33,
      status: 'failed',
    })
    expect(mocks.toastError).toHaveBeenCalledWith('Rebase conflict on Issue #33')

    offRebase()
  })

  it('fires toast.error("Merge failed for Issue #N") on WorkflowRunFailed + merge payload', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-mf', 99)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
        issueId: 'iss-mf',
        issueNumber: 99,
        outcome: 'merge_failed',
      })
    })

    expect(mocks.toastError).toHaveBeenCalledTimes(1)
    expect(mocks.toastError).toHaveBeenCalledWith('Merge failed for Issue #99')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(mocks.toastSuccess).not.toHaveBeenCalled()
    // The merge-failure arm intentionally does NOT dispatch a rebase event.
    expect(rebaseEvents).toEqual([])

    offRebase()
  })

  it('returns false (no invalidation, no toast, no rebase dispatch) on a payload that is neither rebase nor merge', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-fall', 5)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const handleEvent = mountWith(queryClient)

    // IssueCompleted with neither a rebase nor a merge payload: the
    // outcome handler must return false (no rebase dispatch, no toast, no
    // setRebaseConflict) and the switch arm runs its default invalidation
    // instead. We pin those default invalidations to make sure the
    // outcome handler did not silently fire any of its own.
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
        issueId: 'iss-fall',
        issueNumber: 5,
        outcome: 'something_else',
      })
    })

    expect(rebaseEvents).toEqual([])
    expect(mocks.toastSuccess).not.toHaveBeenCalled()
    expect(mocks.toastError).not.toHaveBeenCalled()
    expect(mocks.toastInfo).not.toHaveBeenCalled()
    // The switch arm's default invalidations still run (they are not the
    // outcome handler's job):
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 'detail', 'iss-fall'] })

    offRebase()
  })
})

/**
 * Pin the currently-untested branches of the in-file `notifyRunLifecycleToast`
 * (pause / error / suppression by currently-viewed-issue /
 * suppression when no issue number resolves) BEFORE the refactor in this
 * issue moves the helper. Characterization tests against the current
 * implementation; see design.md#D2.
 */
describe('LiveTaskProvider notifyRunLifecycleToast (D2 test-first)', () => {
  function makeIssue(id: string, number: number): Issue {
    return {
      id,
      number,
      title: `Issue ${number}`,
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      projectId: TEST_PROJECT.id,
      labels: {},
      createdAt: '2024-01-01T00:00:00.000Z',
      updatedAt: '2024-01-01T00:00:00.000Z',
      isDraft: false,
      canStart: true,
      blocker: null,
    }
  }

  function mountWith(queryClient: QueryClient) {
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
    return mocks.useEventsConnection.mock.calls[0][1] as (eventName: string, data: unknown) => void
  }

  it('fires toast.info("Issue #N needs approval") on WorkflowRunPaused when the issue is not currently viewed', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/issues/other-issue')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-pause', 42)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused, {
        issueId: 'iss-pause',
        issueNumber: 42,
      })
    })

    expect(mocks.toastInfo).toHaveBeenCalledTimes(1)
    expect(mocks.toastInfo).toHaveBeenCalledWith('Issue #42 needs approval')
    expect(mocks.toastError).not.toHaveBeenCalled()
    window.history.pushState({}, '', savedPathname)
  })

  it('fires toast.error("Issue #N encountered an error") on WorkflowRunFailed when the issue is not currently viewed', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/issues/other-issue')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-err', 51)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
        issueId: 'iss-err',
        issueNumber: 51,
      })
    })

    expect(mocks.toastError).toHaveBeenCalledTimes(1)
    expect(mocks.toastError).toHaveBeenCalledWith('Issue #51 encountered an error')
    expect(mocks.toastInfo).not.toHaveBeenCalled()
    window.history.pushState({}, '', savedPathname)
  })

  it('suppresses the lifecycle toast when the event\'s issue is the currently-viewed issue', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/issues/77')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-self-pause', 77)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused, {
        issueId: 'iss-self-pause',
        issueNumber: 77,
      })
    })

    expect(mocks.toastInfo).not.toHaveBeenCalled()
    expect(mocks.toastError).not.toHaveBeenCalled()
    window.history.pushState({}, '', savedPathname)
  })

  it('suppresses the lifecycle toast when findIssueNumber resolves no issue number (issueId not in any cached list)', () => {
    const savedPathname = window.location.pathname
    window.history.pushState({}, '', '/test-project/issues/some-page')
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    // Seed the cache with only unrelated issues — the lookup by issueId will
    // not find a match, so findIssueNumber returns null and the helper bails.
    queryClient.setQueryData(['issues'], [makeIssue('iss-other-1', 1), makeIssue('iss-other-2', 2)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused, {
        issueId: 'iss-unknown',
      })
    })

    expect(mocks.toastInfo).not.toHaveBeenCalled()
    expect(mocks.toastError).not.toHaveBeenCalled()
    window.history.pushState({}, '', savedPathname)
  })
})
