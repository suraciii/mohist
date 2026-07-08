// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, fireEvent, screen } from '@testing-library/react'

import { EpicStatus } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'

/**
 * Page-level lifecycle orchestration tests for <EpicDetailPage/>: mark-done/close guards, pause/resume, and header lifecycle actions.
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
  useReopenEpic: vi.fn(),
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
    useReopenEpic: mocks.useReopenEpic,
  }
})

describe('EpicDetailPage lifecycle guards', () => {
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

  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const reopenMutate = vi.fn()
  const startEpicMutate = vi.fn()

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
    mocks.useReopenEpic.mockReturnValue({ mutate: reopenMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('disables Mark Done while progress is not ready and explains unfinished issue count', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 1,
          totalIssueCount: 3,
          blockedIssues: [],
          activeIssues: [
            { id: 'issue-2', number: 2, title: 'Active issue', health: 'active' },
            { id: 'issue-3', number: 3, title: 'Backlog issue', health: 'active' },
          ],
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
    expect(screen.getByTestId('start-epic-trigger')).toBeTruthy()
    expect(doneMutate).not.toHaveBeenCalled()
  })

  it('explains singular unfinished count when exactly one linked issue remains', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 1,
          totalIssueCount: 2,
          blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
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

  it('enables Mark Done and runs the mark-done mutation when progress is ready', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
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
    expect(markDone).not.toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    fireEvent.click(markDone)

    expect(doneMutate).toHaveBeenCalledWith('epic-12345678')
  })

  it('opens a close confirmation dialog that lists the linked issue count before submitting', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
          linkedIssue({ id: 'issue-2', number: 2, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
          linkedIssue({ id: 'issue-3', number: 3, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p3' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('close-epic-trigger'))

    expect(closeMutate).not.toHaveBeenCalled()
    expect(screen.getByText(/unlink 3 associated issues/i)).toBeTruthy()
    expect(screen.getByText(/issue workflow state will not change/i)).toBeTruthy()

    fireEvent.click(screen.getByTestId('close-epic-confirm'))

    expect(closeMutate).toHaveBeenCalledWith('epic-12345678', expect.objectContaining({
      onSettled: expect.any(Function),
    }))
  })

  it('shows a singular linked issue message when only one issue is associated', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('close-epic-trigger'))

    expect(screen.getByText(/unlink 1 associated issue\b/i)).toBeTruthy()
  })

  it('explains that closing is safe when no issues are linked', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ linkedIssues: [] }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('close-epic-trigger'))

    expect(screen.getByText(/This Epic has no linked issues/i)).toBeTruthy()
  })

  it('does not run the close mutation when the confirmation is cancelled', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('close-epic-trigger'))
    fireEvent.click(screen.getByTestId('close-epic-cancel'))

    expect(closeMutate).not.toHaveBeenCalled()
  })

  it('hides Mark Done and Close Epic for done epics and shows the terminal status', () => {
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

    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
    expect(screen.getByTestId('epic-number').parentElement).toHaveTextContent('done')
  })

  it('hides Mark Done and Close Epic for closed epics', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Closed,
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

    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
    expect(screen.getByText('closed')).toBeTruthy()
  })
})

describe('EpicDetailPage pause/resume actions', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const startEpicMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const reopenMutate = vi.fn()

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
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
    mocks.useReopenEpic.mockReturnValue({ mutate: reopenMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows a Pause button on a running Epic that opens a confirm dialog with a reason input', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running }),
      isLoading: false,
    })

    renderPage()

    const pauseTrigger = screen.getByTestId('pause-epic-trigger')
    expect(pauseTrigger).toBeTruthy()
    expect(pauseTrigger).toHaveTextContent('Pause')

    fireEvent.click(pauseTrigger)

    expect(screen.getByText('Pause Epic?')).toBeTruthy()
    expect(screen.getByText(/keep all linked issues connected/i)).toBeTruthy()
    expect(screen.getByTestId('pause-reason-input')).toBeTruthy()
    expect(screen.getByTestId('pause-epic-confirm')).toBeTruthy()
  })

  it('submits the pause mutation with an optional reason', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('pause-epic-trigger'))

    const reasonInput = screen.getByTestId('pause-reason-input') as HTMLInputElement
    fireEvent.change(reasonInput, { target: { value: 'Waiting for design review' } })

    fireEvent.click(screen.getByTestId('pause-epic-confirm'))

    expect(pauseMutate).toHaveBeenCalledWith(
      { id: 'epic-12345678', reason: 'Waiting for design review' },
      expect.objectContaining({ onSettled: expect.any(Function) }),
    )
  })

  it('submits the pause mutation with null reason when the input is left empty', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('pause-epic-trigger'))
    fireEvent.click(screen.getByTestId('pause-epic-confirm'))

    expect(pauseMutate).toHaveBeenCalledWith(
      { id: 'epic-12345678', reason: null },
      expect.objectContaining({ onSettled: expect.any(Function) }),
    )
  })

  it('cancels the pause dialog without calling the mutation', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('pause-epic-trigger'))
    fireEvent.click(screen.getByTestId('pause-epic-cancel'))

    expect(pauseMutate).not.toHaveBeenCalled()
    expect(screen.queryByText('Pause Epic?')).toBeNull()
  })

  it('shows a Resume button on a paused Epic that calls the resume mutation', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Paused }),
      isLoading: false,
    })

    renderPage()

    const resumeTrigger = screen.getByTestId('resume-epic-trigger')
    expect(resumeTrigger).toBeTruthy()
    expect(resumeTrigger).toHaveTextContent('Resume')

    fireEvent.click(resumeTrigger)

    expect(resumeMutate).toHaveBeenCalledWith('epic-12345678')
  })

  it('disables Mark Done when the Epic is paused and shows the resume-first hint', () => {
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')
    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('Resume this Epic before marking it done.')
  })

  it('displays the persisted pause reason near the status badge when present', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Paused,
        pauseReason: 'Waiting for design review',
      }),
      isLoading: false,
    })

    renderPage()

    const reasonBadge = screen.getByTestId('pause-reason')
    expect(reasonBadge).toHaveTextContent('Waiting for design review')
  })

  it('does not show a pause reason element when the epic has no reason', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Paused }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('pause-reason')).toBeNull()
  })

  it('hides the Pause button and shows Resume when epic is paused', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Paused }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.getByTestId('resume-epic-trigger')).toBeTruthy()
  })

  it('hides the Pause button for done epics', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Done }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  it('hides the Pause button for closed epics', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Closed }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  describe('reopen control for terminal epics', () => {
    it('shows a Reopen button on a done epic', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Done }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('reopen-epic-trigger')).toBeTruthy()
      expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
      expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
      expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
      expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    })

    it('shows a Reopen button on a closed epic', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Closed }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('reopen-epic-trigger')).toBeTruthy()
    })

    it('does not show a Reopen button on a non-terminal epic', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Idle }),
        isLoading: false,
      })

      renderPage()

      expect(screen.queryByTestId('reopen-epic-trigger')).toBeNull()
    })

    it('invokes the reopen mutation with the epic id on click', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Done }),
        isLoading: false,
      })

      renderPage()

      fireEvent.click(screen.getByTestId('reopen-epic-trigger'))

      expect(reopenMutate).toHaveBeenCalledWith('epic-12345678')
    })

    it('disables the Reopen button while the mutation is in flight', () => {
      mocks.useReopenEpic.mockReturnValue({ mutate: reopenMutate, isPending: true })
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Closed }),
        isLoading: false,
      })

      renderPage()

      const reopenButton = screen.getByTestId('reopen-epic-trigger')
      expect(reopenButton).toBeDisabled()
      expect(reopenButton).toHaveTextContent(/Reopening/)
    })
  })
})

describe('EpicDetailPage lifecycle header actions', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const reopenMutate = vi.fn()
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
    mocks.useReopenEpic.mockReturnValue({ mutate: reopenMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders Start Epic as the only lifecycle action when the epic is idle', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    const start = screen.getByTestId('start-epic-trigger')
    expect(start).toBeTruthy()
    expect(start).toHaveTextContent('Start Epic')

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  it('invokes the start mutation with the epic id when Start Epic is clicked', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    expect(startEpicMutate).toHaveBeenCalledTimes(1)
    expect(startEpicMutate).toHaveBeenCalledWith('epic-12345678')
  })

  it('renders Pause as the only lifecycle action when the epic is running', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const pause = screen.getByTestId('pause-epic-trigger')
    expect(pause).toBeTruthy()
    expect(pause).toHaveTextContent('Pause')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  it('renders Resume as the only lifecycle action when the epic is paused', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Paused }), isLoading: false })

    renderPage()

    const resume = screen.getByTestId('resume-epic-trigger')
    expect(resume).toBeTruthy()
    expect(resume).toHaveTextContent('Resume')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
  })

  it('renders Reopen as the lifecycle action when the epic is done', () => {
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
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('reopen-epic-trigger')).toHaveTextContent('Reopen')
    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
  })

  it('renders Reopen as the lifecycle action when the epic is closed', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Closed }), isLoading: false })

    renderPage()

    expect(screen.getByTestId('reopen-epic-trigger')).toHaveTextContent('Reopen')
    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
  })

  it('shows the Start Epic label with Starting... and disables the trigger while pending', () => {
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: true })
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    const start = screen.getByTestId('start-epic-trigger')
    expect(start).toHaveTextContent('Starting...')
    expect(start).toBeDisabled()
  })
})
