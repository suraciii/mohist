import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'

import { EpicStatus } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage, getActionGroup } from './_epicDetailPageTestUtils'
import { useMswServer } from '../../../../tests/support/msw'

let _epicData: unknown = null
let _issuesData: unknown[] = []
const _startEpicHandler = vi.fn()
const _markDoneHandler = vi.fn()
const _pauseEpicHandler = vi.fn()
const _resumeEpicHandler = vi.fn()

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
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/done', ({ params }) => {
    _markDoneHandler(Number(params.epicNumber))
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

describe('EpicDetailPage single prominent primary action', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('renders Start Epic as the only prominent primary action on an idle non-ready epic (no Pause/Resume/Mark Done primary)', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    const start = await screen.findByTestId('start-epic-trigger')
    expect(start).toBeTruthy()
    expect(start).toHaveTextContent('Start Epic')

    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')
  })

  it('invokes the start API when Start Epic is clicked on an idle epic', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    await screen.findByTestId('start-epic-trigger')
    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    await waitFor(() => {
      expect(_startEpicHandler).toHaveBeenCalledTimes(1)
      expect(_startEpicHandler).toHaveBeenCalledWith(123)
    })
  })

  it('renders Pause as the only prominent primary action on a running non-ready epic and opens the pause confirm flow on click', async () => {
    _epicData = makeEpic({ status: EpicStatus.Running })

    renderPage()

    const pause = await screen.findByTestId('pause-epic-trigger')
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
    await waitFor(() => expect(_pauseEpicHandler).toHaveBeenCalledWith(
      { number: 123, reason: null },
    ))
  })

  it('renders Resume as the prominent primary on a paused ready epic and keeps Mark Done only as disabled secondary (not as primary)', async () => {
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    const resume = await screen.findByTestId('resume-epic-trigger')
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

  it('invokes the resume API when Resume is clicked on a paused ready epic', async () => {
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('resume-epic-trigger')
    fireEvent.click(screen.getByTestId('resume-epic-trigger'))

    await waitFor(() => {
      expect(_resumeEpicHandler).toHaveBeenCalledTimes(1)
      expect(_resumeEpicHandler).toHaveBeenCalledWith(123)
    })
  })

  it('renders Mark Done as the prominent primary on a non-paused, non-terminal ready epic and hides Start/Pause', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).not.toBeDisabled()
    expect(markDone).toHaveTextContent('Mark Done')

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()

    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('renders Mark Done as the prominent primary on an idle ready epic and hides Start', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeTruthy()
    expect(markDone).not.toBeDisabled()

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('invokes the mark-done API when Mark Done (primary) is clicked on a ready epic', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('mark-epic-done')
    fireEvent.click(screen.getByTestId('mark-epic-done'))

    await waitFor(() => {
      expect(_markDoneHandler).toHaveBeenCalledTimes(1)
      expect(_markDoneHandler).toHaveBeenCalledWith(123)
    })
  })

  it('renders no Start/Pause/Resume/Mark Done lifecycle action on a done epic', async () => {
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
    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('renders no Start/Pause/Resume/Mark Done lifecycle action on a closed epic', async () => {
    _epicData = makeEpic({ status: EpicStatus.Closed })

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('mark-done-disabled-reason')).toBeNull()
  })

  it('renders a visible on-screen reason with no title attribute when Mark Done is disabled because the epic is paused', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p2' }),
        linkedIssue({ number: 2, title: 'Backlog issue', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, priority: 'p2' }),
      ],
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toBeTruthy()
    expect(reason).toHaveTextContent('Resume this Epic before marking it done.')
  })

  it('renders a visible on-screen reason stating the unfinished count (plural) when Mark Done is disabled on an idle non-ready epic', async () => {
    _epicData = makeEpic({
      status: EpicStatus.Idle,
      progress: {
        deliveredCount: 1,
        totalIssueCount: 3,
        blockedIssues: [],
        activeIssues: [],
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
  })

  it('renders a visible on-screen reason stating the unfinished count (singular) when exactly one linked issue remains', async () => {
    _epicData = makeEpic({
      status: EpicStatus.Idle,
      progress: {
        deliveredCount: 1,
        totalIssueCount: 2,
        blockedIssues: [],
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

  it('renders an actionable visible reason when no linked issues exist', async () => {
    _epicData = makeEpic({
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
    })

    renderPage()

    const markDone = await screen.findByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).not.toHaveAttribute('title')

    const reason = screen.getByTestId('mark-done-disabled-reason')
    expect(reason).toHaveTextContent('Link at least one issue before marking this Epic done.')
    expect(reason).not.toHaveTextContent('0 linked issues remain unfinished.')
  })

  it('keeps Edit and Close Epic reachable as secondary actions across non-terminal statuses', async () => {
    for (const status of [EpicStatus.Idle, EpicStatus.Running, EpicStatus.Paused]) {
      _epicData = makeEpic({ status })
      renderPage()

      await screen.findByTestId('edit-epic-button')
      const actionGroup = getActionGroup()
      expect(actionGroup.querySelector('[data-testid="edit-epic-button"]'), `edit on ${status}`).toBeTruthy()
      expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]'), `close on ${status}`).toBeTruthy()

      cleanup()
    }
  })

  it('does not render any lifecycle primary action alongside Mark Done on a non-paused ready epic (no Pause / no Start)', async () => {
    _epicData = makeEpic({
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
        linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    })

    renderPage()

    await screen.findByTestId('edit-epic-button')
    const actionGroup = getActionGroup()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })
})

describe('EpicDetailPage Start Epic refresh on success', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('submits the start mutation with the epic id and no extra options', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    await screen.findByTestId('start-epic-trigger')
    fireEvent.click(screen.getByTestId('start-epic-trigger'))

    await waitFor(() => {
      expect(_startEpicHandler).toHaveBeenCalledTimes(1)
      expect(_startEpicHandler).toHaveBeenCalledWith(123)
    })
  })

  it('does not invoke the start mutation when Start Epic is not clicked', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    await screen.findByTestId('start-epic-trigger')
    expect(_startEpicHandler).not.toHaveBeenCalled()
  })

  it('keeps the Start Epic trigger stable across multiple idle renders (header does not flicker)', async () => {
    _epicData = makeEpic({ status: EpicStatus.Idle })

    renderPage()

    await screen.findByTestId('start-epic-trigger')
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
  })
})
