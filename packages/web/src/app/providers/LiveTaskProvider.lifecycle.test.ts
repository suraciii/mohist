import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { LiveTaskProvider } from './LiveTaskProvider'
import { onRebaseEvent, type RebaseEvent } from '../../entities/issue/model/rebase-events'
import { useLiveTask } from '../../entities/issue/model/live-task'
import { TEST_PROJECT, makeBaseIssue } from './_liveTaskProviderTestUtils'

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

beforeEach(() => {
  vi.clearAllMocks()
  mocks.useAgentStatus.mockReturnValue({
    data: {
      running: false,
      runnerAvailable: true,
    },
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
  function makeIssue(id: string, number: number) {
    return makeBaseIssue(id, number)
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
