import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { toast } from 'sonner'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import {
  LiveTaskProvider,
  type EventsConnectionHook,
  type ViewedIssueHook,
} from './LiveTaskProvider'
import { onRebaseEvent, type RebaseEvent } from '../../entities/issue/model/rebase-events'
import { useLiveTask } from '../../entities/issue/model/live-task'
import { TEST_PROJECT, makeBaseIssue } from './_liveTaskProviderTestUtils'

let eventsConnectionCalls: Parameters<EventsConnectionHook>[] = []
let reconnectVersion = 0
const eventsConnectionHook: EventsConnectionHook = (...args) => {
  eventsConnectionCalls.push(args)
  return { status: 'disconnected', connection: null, reconnectVersion }
}
const viewedIssueRef = { current: null as number | null }
const viewedIssueHook: ViewedIssueHook = () => viewedIssueRef
let pathname = '/'

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(toast.info).mockClear()
  vi.mocked(toast.error).mockClear()
  vi.mocked(toast.success).mockClear()
  eventsConnectionCalls = []
  reconnectVersion = 0
  viewedIssueRef.current = null
  pathname = '/'
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
  function makeIssue(id: string, number: number) {
    return makeBaseIssue(id, number)
  }

  function mountWith(queryClient: QueryClient, stateProbe?: { current: { rebaseConflict: unknown; eventsReconnectVersion: number } | null }) {
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
                eventsConnectionHook,
                viewedIssueHook,
                pathnameReader: () => pathname,
              },
            ),
          },
        ),
      ),
    )
    return eventsConnectionCalls[0][1]
  }

  it('clears rebase conflict, dispatches rebase_completed, and invalidates ["issues"] on IssueCompleted + rebase payload', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-rebase', 7)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const stateProbe: { current: { rebaseConflict: unknown; eventsReconnectVersion: number } | null } = { current: null }
    const handleEvent = mountWith(queryClient, stateProbe)

    // First seed the conflict via a rebase-failed event so the subsequent
    // rebase-completed has something to clear.
    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
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
    vi.mocked(toast.info).mockClear()
    vi.mocked(toast.error).mockClear()
    vi.mocked(toast.success).mockClear()

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.IssueCompleted, {
        issueNumber: 7,
        outcome: 'rebase_completed',
        rebased: true,
      })
    })

    expect(rebaseEvents).toContainEqual({ type: 'rebase_completed', issueNumber: 7, rebased: true })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    // Issue-arm default fallback only fires when the outcome handler returns false;
    // the rebase-completed arm returns true and breaks, so scoped detail invalidation
    // is intentionally not issued. Pin that.
    const detailInvalidations = invalidateSpy.mock.calls
      .map((args) => args[0] as { queryKey?: unknown[] })
      .filter((arg) => Array.isArray(arg.queryKey)
        && arg.queryKey[0] === 'issues'
        && arg.queryKey[1] === 7)
    expect(detailInvalidations).toHaveLength(0)
    expect(toast.success).not.toHaveBeenCalled()
    expect(toast.error).not.toHaveBeenCalled()
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
        issueNumber: 13,
        outcome: 'merge_completed',
      })
    })

    expect(toast.success).toHaveBeenCalledTimes(1)
    expect(toast.success).toHaveBeenCalledWith('Issue #13 merged successfully')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('sets rebaseConflict, dispatches rebase_conflict, fires toast.error, and invalidates ["issues"] on WorkflowRunFailed + rebase payload', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    queryClient.setQueryData(['issues'], [makeIssue('iss-conflict', 21)])

    const rebaseEvents: RebaseEvent[] = []
    const offRebase = onRebaseEvent((event) => rebaseEvents.push(event))

    const stateProbe: { current: { rebaseConflict: unknown; eventsReconnectVersion: number } | null } = { current: null }
    const handleEvent = mountWith(queryClient, stateProbe)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
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
    expect(toast.error).toHaveBeenCalledTimes(1)
    expect(toast.error).toHaveBeenCalledWith('Rebase conflict on Issue #21')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(toast.success).not.toHaveBeenCalled()
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
    expect(toast.error).toHaveBeenCalledWith('Rebase conflict on Issue #33')

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
        issueNumber: 99,
        outcome: 'merge_failed',
      })
    })

    expect(toast.error).toHaveBeenCalledTimes(1)
    expect(toast.error).toHaveBeenCalledWith('Merge failed for Issue #99')
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(toast.success).not.toHaveBeenCalled()
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
        issueNumber: 5,
        outcome: 'something_else',
      })
    })

    expect(rebaseEvents).toEqual([])
    expect(toast.success).not.toHaveBeenCalled()
    expect(toast.error).not.toHaveBeenCalled()
    expect(toast.info).not.toHaveBeenCalled()
    // The switch arm's default invalidations still run (they are not the
    // outcome handler's job):
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['issues', 5, TEST_PROJECT.id] })

    offRebase()
  })
  it('exposes the events reconnect version through LiveTaskState', () => {
    reconnectVersion = 3
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const stateProbe: { current: { rebaseConflict: unknown; eventsReconnectVersion: number } | null } = { current: null }

    mountWith(queryClient, stateProbe)

    expect(stateProbe.current?.eventsReconnectVersion).toBe(3)
  })
})

/**
 * Pin the currently-untested branches of the in-file `notifyRunLifecycleToast`
 * (pause / error / suppression by currently-viewed-issue /
 * suppression when canonical issue context is absent) BEFORE the refactor in this
 * issue moves the helper. Characterization tests against the current
 * implementation; see design.md#D2.
 */
describe('LiveTaskProvider notifyRunLifecycleToast (D2 test-first)', () => {
  function makeIssue(id: string, number: number) {
    return makeBaseIssue(id, number)
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
              {
                children: createElement('div', null, 'child'),
                eventsConnectionHook,
                viewedIssueHook,
                pathnameReader: () => pathname,
              },
            ),
          },
        ),
      ),
    )
    return eventsConnectionCalls[0][1]
  }

  it('fires toast.info("Issue #N needs approval") on WorkflowRunPaused when the issue is not currently viewed', () => {
    pathname = '/test-project/issues/other-issue'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-pause', 42)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused, {
        issueNumber: 42,
      })
    })

    expect(toast.info).toHaveBeenCalledTimes(1)
    expect(toast.info).toHaveBeenCalledWith('Issue #42 needs approval')
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('fires toast.error("Issue #N encountered an error") on WorkflowRunFailed when the issue is not currently viewed', () => {
    pathname = '/test-project/issues/other-issue'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-err', 51)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed, {
        issueNumber: 51,
      })
    })

    expect(toast.error).toHaveBeenCalledTimes(1)
    expect(toast.error).toHaveBeenCalledWith('Issue #51 encountered an error')
    expect(toast.info).not.toHaveBeenCalled()
  })

  it('suppresses the lifecycle toast when the event\'s issue is the currently-viewed issue', () => {
    pathname = '/test-project/issues/77'
    viewedIssueRef.current = 77
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    queryClient.setQueryData(['issues'], [makeIssue('iss-self-pause', 77)])
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused, {
        issueNumber: 77,
      })
    })

    expect(toast.info).not.toHaveBeenCalled()
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('suppresses the lifecycle toast when the event omits canonical issue context', () => {
    pathname = '/test-project/issues/some-page'
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const handleEvent = mountWith(queryClient)

    act(() => {
      handleEvent(REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused, {
      })
    })

    expect(toast.info).not.toHaveBeenCalled()
    expect(toast.error).not.toHaveBeenCalled()
  })
})
