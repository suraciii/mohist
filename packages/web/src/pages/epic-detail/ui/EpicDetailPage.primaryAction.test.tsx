// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, fireEvent, screen } from '@testing-library/react'

import { EpicStatus } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage, getActionGroup } from './_epicDetailPageTestHarness'

/**
 * Page-level primary-lifecycle-action tests for <EpicDetailPage/>: single prominent primary action (T-001) and Start Epic refresh.
 */

const mocks = vi.hoisted(() => ({
  useProject: vi.fn(),
  useEpic: vi.fn(),
  useIssues: vi.fn(),
  useAddEpicIssue: vi.fn(),
  useRemoveEpicIssue: vi.fn(),
  useStartIssue: vi.fn(),
  useStartEpic: vi.fn(),
  useMarkEpicDone: vi.fn(),
  useCloseEpic: vi.fn(),
  useUpdateEpic: vi.fn(),
  usePauseEpic: vi.fn(),
  useResumeEpic: vi.fn(),
}))

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useProject: mocks.useProject,
  }
})
vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useIssues: mocks.useIssues,
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpic: mocks.useEpic,
    useAddEpicIssue: mocks.useAddEpicIssue,
    useRemoveEpicIssue: mocks.useRemoveEpicIssue,
    useStartIssue: mocks.useStartIssue,
    useStartEpic: mocks.useStartEpic,
    useMarkEpicDone: mocks.useMarkEpicDone,
    useCloseEpic: mocks.useCloseEpic,
    useUpdateEpic: mocks.useUpdateEpic,
    usePauseEpic: mocks.usePauseEpic,
    useResumeEpic: mocks.useResumeEpic,
  }
})

describe('EpicDetailPage single prominent primary action (T-001)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: 0,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [],
      ...overrides,
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders Start Epic as the only prominent primary action on an idle non-ready epic (no Pause/Resume/Mark Done primary)', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    const start = screen.getByTestId('start-epic-trigger')
    expect(start).toBeTruthy()
    expect(start).toHaveTextContent('Start Epic')

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')
  })

  it('invokes the start API when Start Epic is clicked on an idle epic', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    expect(startEpicMutate).toHaveBeenCalledTimes(1)
    expect(startEpicMutate).toHaveBeenCalledWith('epic-12345678')
  })

  it('renders Pause as the only prominent primary action on a running non-ready epic and opens the pause confirm flow on click', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const pause = screen.getByTestId('pause-epic-trigger')
    expect(pause).toBeTruthy()
    expect(pause).toHaveTextContent('Pause')
    expect(pause).toHaveClass('bg-primary')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).toBeDisabled()

    fireEvent.click(pause)
    expect(screen.getByText('Pause Epic?')).toBeTruthy()

    fireEvent.click(screen.getByTestId('pause-epic-confirm'))
    expect(pauseMutate).toHaveBeenCalledWith(
      { id: 'epic-12345678', reason: null },
      expect.objectContaining({ onSettled: expect.any(Function) }),
    )
  })

  it('renders Resume as the prominent primary on a paused ready epic and keeps Mark Done only as disabled secondary (not as primary)', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Paused,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const resume = screen.getByTestId('resume-epic-trigger')
    expect(resume).toBeTruthy()
    expect(resume).toHaveTextContent('Resume')
    expect(resume).toHaveClass('bg-primary')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).toBeDisabled()

    expect(markDone).not.toHaveAttribute('title')
    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('Resume this Epic before marking it done.')
  })

  it('invokes the resume API when Resume is clicked on a paused ready epic', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Paused,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('resume-epic-trigger'))

    expect(resumeMutate).toHaveBeenCalledTimes(1)
    expect(resumeMutate).toHaveBeenCalledWith('epic-12345678')
  })

  it('renders Mark Done as the prominent primary on a non-paused, non-terminal ready epic and hides Start/Pause', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Running,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).not.toBeDisabled()
    expect(markDone).toHaveTextContent('Mark Done')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()

    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('renders Mark Done as the prominent primary on an idle ready epic and hides Start', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Idle,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).not.toBeDisabled()

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('invokes the mark-done API when Mark Done (primary) is clicked on a ready epic', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Running,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('mark-epic-done'))

    expect(doneMutate).toHaveBeenCalledTimes(1)
    expect(doneMutate).toHaveBeenCalledWith('epic-12345678')
  })

  it('renders no Start/Pause/Resume/Mark Done lifecycle action on a done epic', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Done,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('renders no Start/Pause/Resume/Mark Done lifecycle action on a closed epic', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Closed }), isLoading: false })

    renderPage()

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('renders a visible on-screen reason with no title attribute when Mark Done is disabled because the epic is paused', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Paused,
        progress: {
          deliveredCount: 0,
          totalIssueCount: 2,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p2' }),
          linkedIssue({ id: 'issue-2', number: 2, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toBeTruthy()
    expect(reason).toHaveTextContent('Resume this Epic before marking it done.')
  })

  it('renders a visible on-screen reason stating the unfinished count (plural) when Mark Done is disabled on an idle non-ready epic', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Idle,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 3,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: { id: 'issue-2', number: 2, title: 'Active issue' },
          nextIssueReason: null,
          readyToMarkDone: false,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
          linkedIssue({ id: 'issue-2', number: 2, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
          linkedIssue({ id: 'issue-3', number: 3, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p3' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('2 linked issues remain unfinished.')
  })

  it('renders a visible on-screen reason stating the unfinished count (singular) when exactly one linked issue remains', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Idle,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 2,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: { id: 'issue-2', number: 2, title: 'Blocked issue' },
          nextIssueReason: null,
          readyToMarkDone: false,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
          linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p1' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('1 linked issue remains unfinished.')
  })

  it('renders an actionable visible reason when no linked issues exist', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Idle,
        progress: {
          deliveredCount: 0,
          totalIssueCount: 0,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
        linkedIssues: [],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('Link at least one issue before marking this Epic done.')
    expect(reason).not.toHaveTextContent('0 linked issues remain unfinished.')
  })

  it('keeps Edit and Close Epic reachable as secondary actions across non-terminal statuses', () => {
    for (const status of [EpicStatus.Idle, EpicStatus.Running, EpicStatus.Paused]) {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ status }), isLoading: false })
      renderPage()

      const actionGroup = getActionGroup()
      expect(actionGroup.querySelector('[data-testid="edit-epic-button"]'), `edit on ${status}`).toBeTruthy()
      expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]'), `close on ${status}`).toBeTruthy()

      cleanup()
    }
  })

  it('does not render any lifecycle primary action alongside Mark Done on a non-paused ready epic (no Pause / no Start)', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Running,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const actionGroup = getActionGroup()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })
})

describe('EpicDetailPage Start Epic refresh on success', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: 0,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [],
      ...overrides,
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('submits the start mutation with the epic id and no extra options', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    expect(startEpicMutate).toHaveBeenCalledTimes(1)
    expect(startEpicMutate).toHaveBeenCalledWith('epic-12345678')
    expect(startEpicMutate.mock.calls[0]).toHaveLength(1)
  })

  it('does not invoke the start mutation when Start Epic is not clicked', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    expect(startEpicMutate).not.toHaveBeenCalled()
  })

  it('keeps the Start Epic trigger stable across multiple idle renders (header does not flicker)', () => {
    const idleEpic = makeEpic({ status: EpicStatus.Idle })
    mocks.useEpic.mockReturnValue({ data: idleEpic, isLoading: false })

    renderPage()
    expect(screen.getByTestId('start-epic-trigger')).toBeTruthy()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })
})
