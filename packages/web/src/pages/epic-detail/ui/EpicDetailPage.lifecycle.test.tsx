import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'

import { EpicStatus } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage } from './_epicDetailPageTestUtils'
import { useMswServer } from '../../../../tests/support/msw'

let _epicData: unknown = null
let _issuesData: unknown[] = []
const _startEpicHandler = vi.fn()
const _markDoneHandler = vi.fn()
const _closeEpicHandler = vi.fn()
const _pauseEpicHandler = vi.fn()
const _resumeEpicHandler = vi.fn()
const _reopenEpicHandler = vi.fn()
let _blockReopen = false
let _blockStart = false

useMswServer(
  http.get('*/api/projects/:projectId/epics/:epicNumber', () =>
    HttpResponse.json({ success: true, data: _epicData }),
  ),
  http.get('*/api/projects/:projectId/epics/:epicNumber/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issuesData }),
  ),
  http.post('*/api/projects/:projectId/epics/:epicNumber/start', ({ params }) => {
    _startEpicHandler(Number(params.epicNumber))
    if (_blockStart) return new Promise(() => {})
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/done', ({ params }) => {
    _markDoneHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/close', ({ params }) => {
    _closeEpicHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/pause', async ({ request, params }) => {
    let reason: string | null = null
    try { const body = await request.json() as any; reason = body.reason ?? null } catch { /* empty body */ }
    _pauseEpicHandler({ number: Number(params.epicNumber), reason })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/resume', ({ params }) => {
    _resumeEpicHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/reopen', ({ params }) => {
    _reopenEpicHandler(Number(params.epicNumber))
    if (_blockReopen) return new Promise(() => {})
    return HttpResponse.json({ success: true, data: {} })
  }),
)

function makeEpic(overrides: Record<string, unknown> = {}) {
  return {
    projectId: 'proj-1',
    number: 123,
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

describe('EpicDetailPage lifecycle guards', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _issuesData = issues
    _blockReopen = false
    _blockStart = false
  })

  afterEach(() => {
    cleanup()
  })

  it('disables Mark Done while progress is not ready and explains unfinished issue count', async () => {
    _epicData = makeEpic({
      progress: {
        deliveredCount: 1,
        totalIssueCount: 3,
        blockedIssues: [],
        activeIssues: [
          { number: 2, title: 'Active issue', health: 'active' },
          { number: 3, title: 'Backlog issue', health: 'active' },
        ],
        nextIssue: { number: 2, title: 'Active issue' },
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        linkedIssue({ number: 2, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
        linkedIssue({ number: 3, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p3' }),
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')
    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('2 linked issues remain unfinished.')
    expect(screen.getByTestId('start-epic-trigger')).toBeTruthy()
    expect(_markDoneHandler).not.toHaveBeenCalled()
  })

  it('explains singular unfinished count when exactly one linked issue remains', async () => {
    _epicData = makeEpic({
      progress: {
        deliveredCount: 1,
        totalIssueCount: 2,
        blockedIssues: [{ number: 2, title: 'Blocked issue', health: 'blocked' }],
        activeIssues: [],
        nextIssue: { number: 2, title: 'Blocked issue' },
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        linkedIssue({ number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p1' }),
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')
    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('1 linked issue remains unfinished.')
  })

  it('enables Mark Done and runs the mark-done mutation when progress is ready', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).not.toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    fireEvent.click(markDone)

    await waitFor(() => expect(_markDoneHandler).toHaveBeenCalledWith(123))
  })

  it('opens a close confirmation dialog that lists the linked issue count before submitting', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        linkedIssue({ number: 2, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
        linkedIssue({ number: 3, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p3' }),
      ],
    })

    renderPage()

    await screen.findByTestId('close-epic-trigger')
    fireEvent.click(screen.getByTestId('close-epic-trigger'))

    expect(_closeEpicHandler).not.toHaveBeenCalled()
    expect(screen.getByText(/unlink 3 associated issues/i)).toBeTruthy()
    expect(screen.getByText(/issue workflow state will not change/i)).toBeTruthy()

    fireEvent.click(screen.getByTestId('close-epic-confirm'))

    await waitFor(() => expect(_closeEpicHandler).toHaveBeenCalledWith(123))
  })

  it('shows a singular linked issue message when only one issue is associated', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('close-epic-trigger')
    fireEvent.click(screen.getByTestId('close-epic-trigger'))

    expect(screen.getByText(/unlink 1 associated issue\b/i)).toBeTruthy()
  })

  it('explains that closing is safe when no issues are linked', async () => {
    _epicData = makeEpic({ linkedIssues: [] })

    renderPage()

    await screen.findByTestId('close-epic-trigger')
    fireEvent.click(screen.getByTestId('close-epic-trigger'))

    expect(screen.getByText(/This Epic has no linked issues/i)).toBeTruthy()
  })

  it('does not run the close mutation when the confirmation is cancelled', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('close-epic-trigger')
    fireEvent.click(screen.getByTestId('close-epic-trigger'))
    fireEvent.click(screen.getByTestId('close-epic-cancel'))

    expect(_closeEpicHandler).not.toHaveBeenCalled()
  })

  it('hides Mark Done and Close Epic for done epics and shows the terminal status', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
    expect(screen.getByTestId('epic-number').parentElement).toHaveTextContent('done')
  })

  it('hides Mark Done and Close Epic for closed epics', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
    expect(screen.getByText('closed')).toBeTruthy()
  })
})

describe('EpicDetailPage pause/resume actions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _issuesData = issues
    _blockReopen = false
    _blockStart = false
  })

  afterEach(() => {
    cleanup()
  })

  it('shows a Pause button on a running Epic that opens a confirm dialog with a reason input', async () => {
    _epicData = makeEpic({ status: EpicStatus.Running })

    renderPage()

    const pauseTrigger = await screen.findByTestId('pause-epic-trigger')
    expect(pauseTrigger).toBeTruthy()
    expect(pauseTrigger).toHaveTextContent('Pause')

    fireEvent.click(pauseTrigger)

    expect(screen.getByText('Pause Epic?')).toBeTruthy()
    expect(screen.getByText(/keep all linked issues connected/i)).toBeTruthy()
    expect(screen.getByTestId('pause-reason-input')).toBeTruthy()
    expect(screen.getByTestId('pause-epic-confirm')).toBeTruthy()
  })

  it('submits the pause mutation with an optional reason', async () => {
    _epicData = makeEpic({ status: EpicStatus.Running })

    renderPage()

    await screen.findByTestId('pause-epic-trigger')
    fireEvent.click(screen.getByTestId('pause-epic-trigger'))

    const reasonInput = screen.getByTestId('pause-reason-input') as HTMLInputElement
    fireEvent.change(reasonInput, { target: { value: 'Waiting for design review' } })

    fireEvent.click(screen.getByTestId('pause-epic-confirm'))

    await waitFor(() => expect(_pauseEpicHandler).toHaveBeenCalledWith(
      { number: 123, reason: 'Waiting for design review' },
    ))
  })

  it('submits the pause mutation with null reason when the input is left empty', async () => {
    _epicData = makeEpic({ status: EpicStatus.Running })

    renderPage()

    await screen.findByTestId('pause-epic-trigger')
    fireEvent.click(screen.getByTestId('pause-epic-trigger'))
    fireEvent.click(screen.getByTestId('pause-epic-confirm'))

    await waitFor(() => expect(_pauseEpicHandler).toHaveBeenCalledWith(
      { number: 123, reason: null },
    ))
  })

  it('cancels the pause dialog without calling the mutation', async () => {
    _epicData = makeEpic({ status: EpicStatus.Running })

    renderPage()

    await screen.findByTestId('pause-epic-trigger')
    fireEvent.click(screen.getByTestId('pause-epic-trigger'))
    fireEvent.click(screen.getByTestId('pause-epic-cancel'))

    expect(_pauseEpicHandler).not.toHaveBeenCalled()
    expect(screen.queryByText('Pause Epic?')).toBeNull()
  })

  it('shows a Resume button on a paused Epic that calls the resume mutation', async () => {
    _epicData = makeEpic({ status: EpicStatus.Paused })

    renderPage()

    const resumeTrigger = await screen.findByTestId('resume-epic-trigger')
    expect(resumeTrigger).toBeTruthy()
    expect(resumeTrigger).toHaveTextContent('Resume')

    fireEvent.click(resumeTrigger)

    await waitFor(() => expect(_resumeEpicHandler).toHaveBeenCalledWith(123))
  })

  it('disables Mark Done when the Epic is paused and shows the resume-first hint', async () => {
    _epicData = makeEpic({
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
        { number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')
    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('Resume this Epic before marking it done.')
  })

  it('displays the persisted pause reason near the status badge when present', async () => {
    _epicData = makeEpic({
      status: EpicStatus.Paused,
      pauseReason: 'Waiting for design review',
    })

    renderPage()

    const reasonBadge = await screen.findByTestId('pause-reason')
    expect(reasonBadge).toHaveTextContent('Waiting for design review')
  })

  it('does not show a pause reason element when the epic has no reason', async () => {
    _epicData = makeEpic({ status: EpicStatus.Paused })

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.queryByTestId('pause-reason')).toBeNull()
  })

  it('hides the Pause button and shows Resume when epic is paused', async () => {
    _epicData = makeEpic({ status: EpicStatus.Paused })

    renderPage()

    await screen.findByTestId('resume-epic-trigger')
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.getByTestId('resume-epic-trigger')).toBeTruthy()
  })

  it('hides the Pause button for done epics', async () => {
    _epicData = makeEpic({ status: EpicStatus.Done })

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  it('hides the Pause button for closed epics', async () => {
    _epicData = makeEpic({ status: EpicStatus.Closed })

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  describe('reopen control for terminal epics', () => {
    it('shows a Reopen button on a done epic', async () => {
      _epicData = makeEpic({ status: EpicStatus.Done })

      renderPage()

      await screen.findByTestId('reopen-epic-trigger')
      expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
      expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
      expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
      expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    })

    it('shows a Reopen button on a closed epic', async () => {
      _epicData = makeEpic({ status: EpicStatus.Closed })

      renderPage()

      await screen.findByTestId('reopen-epic-trigger')
    })

    it('does not show a Reopen button on a non-terminal epic', async () => {
      _epicData = makeEpic({ status: EpicStatus.Idle })

      renderPage()

      await screen.findByTestId('epic-number')
      expect(screen.queryByTestId('reopen-epic-trigger')).toBeNull()
    })

    it('invokes the reopen mutation with the epic id on click', async () => {
      _epicData = makeEpic({ status: EpicStatus.Done })

      renderPage()

      await screen.findByTestId('reopen-epic-trigger')
      fireEvent.click(screen.getByTestId('reopen-epic-trigger'))

      await waitFor(() => expect(_reopenEpicHandler).toHaveBeenCalledWith(123))
    })

    it('disables the Reopen button while the mutation is in flight', async () => {
      _blockReopen = true
      _epicData = makeEpic({ status: EpicStatus.Closed })

      renderPage()
      await screen.findByTestId('reopen-epic-trigger')

      fireEvent.click(screen.getByTestId('reopen-epic-trigger'))

      await waitFor(() => {
        const reopenButton = screen.getByTestId('reopen-epic-trigger')
        expect(reopenButton).toBeDisabled()
        expect(reopenButton).toHaveTextContent(/Reopening/)
      })
    })
  })
})

describe('EpicDetailPage lifecycle header actions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _issuesData = issues
    _blockReopen = false
    _blockStart = false
  })

  afterEach(() => {
    cleanup()
  })

  it('renders Start Epic as the only lifecycle action when the epic is idle', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    const start = await screen.findByTestId('start-epic-trigger')
    expect(start).toBeTruthy()
    expect(start).toHaveTextContent('Start Epic')

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  it('invokes the start mutation with the epic id when Start Epic is clicked', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    await screen.findByTestId('start-epic-trigger')
    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    await waitFor(() => {
      expect(_startEpicHandler).toHaveBeenCalledTimes(1)
      expect(_startEpicHandler).toHaveBeenCalledWith(123)
    })
  })

  it('renders Pause as the only lifecycle action when the epic is running', async () => {
    _epicData = makeEpic({ status: EpicStatus.Running })

    renderPage()

    const pause = await screen.findByTestId('pause-epic-trigger')
    expect(pause).toBeTruthy()
    expect(pause).toHaveTextContent('Pause')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })

  it('renders Resume as the only lifecycle action when the epic is paused', async () => {
    _epicData = makeEpic({ status: EpicStatus.Paused })

    renderPage()

    const resume = await screen.findByTestId('resume-epic-trigger')
    expect(resume).toBeTruthy()
    expect(resume).toHaveTextContent('Resume')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
  })

  it('renders Reopen as the lifecycle action when the epic is done', async () => {
    _epicData = makeEpic({
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
    })

    renderPage()

    await screen.findByTestId('reopen-epic-trigger')
    expect(screen.getByTestId('reopen-epic-trigger')).toHaveTextContent('Reopen')
    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
  })

  it('renders Reopen as the lifecycle action when the epic is closed', async () => {
    _epicData = makeEpic({ status: EpicStatus.Closed })

    renderPage()

    await screen.findByTestId('reopen-epic-trigger')
    expect(screen.getByTestId('reopen-epic-trigger')).toHaveTextContent('Reopen')
    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
  })

  it('shows the Start Epic label with Starting... and disables the trigger while pending', async () => {
    _blockStart = true
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()
    await screen.findByTestId('start-epic-trigger')

    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    await waitFor(() => {
      const start = screen.getByTestId('start-epic-trigger')
      expect(start).toHaveTextContent('Starting...')
      expect(start).toBeDisabled()
    })
  })
})
