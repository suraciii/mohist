import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { EpicStatus } from '../../../entities/epic'
import { useMswServer } from '../../../../tests/support/msw'
import {
  closedEpic,
  doneEpic,
  idleEmptyEpic,
  idleReadyEpic,
  makeEpic,
  readyToStartEpic,
  renderPage,
  runningEpic,
  waitForList,
  waitingBlockedEpic,
} from './_epicListPageTestUtils'

let _epicsData: unknown[] = []
const _epicsRequests: { search?: string; sort?: string; dir?: string }[] = []
const _startIssueHandler = vi.fn()
const _createEpicHandler = vi.fn()
let _blockStartIssue = false
let _startIssueError: { status: number; error: string } | null = null

useMswServer(
  http.get('*/api/projects/:projectId/epics', ({ request }) => {
    const url = new URL(request.url)
    const search = url.searchParams.get('search') || undefined
    const sort = url.searchParams.get('sort') || undefined
    const dir = url.searchParams.get('dir') || undefined
    _epicsRequests.push({ search, sort, dir })
    return HttpResponse.json({ success: true, data: _epicsData })
  }),
  http.post('*/api/projects/:projectId/epics', async ({ request }) => {
    const body = (await request.json()) as Record<string, string>
    _createEpicHandler(body)
    return HttpResponse.json({ success: true, data: { projectId: 'proj-1', number: 999, ...body } })
  }),
  http.post('*/api/projects/:projectId/issues/:issueNumber/start', ({ params }) => {
    _startIssueHandler(Number(params.issueNumber))
    if (_blockStartIssue) return new Promise(() => {})
    if (_startIssueError) {
      return HttpResponse.json({ success: false, error: _startIssueError.error }, { status: _startIssueError.status })
    }
    return HttpResponse.json({
      success: true,
      data: { issue: { number: Number(params.issueNumber) }, message: 'started' },
    })
  }),
)

describe('EpicListPage four-group rendering', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
    _epicsData = [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic, doneEpic, closedEpic]
  })

  afterEach(() => {
    cleanup()
  })

  it('renders active sections in fixed priority order Running -> Ready to start -> Waiting/Blocked -> Idle/Empty', async () => {
    renderPage()
    await waitForList()

    const headings = screen.getAllByRole('heading', { level: 2 })
    const headingTexts = headings.map((h) => h.textContent ?? '')
    const orderOf = (prefix: string) => headingTexts.findIndex((t) => t.startsWith(prefix))

    const runningIdx = orderOf('Running')
    const readyIdx = orderOf('Ready to start')
    const waitingIdx = orderOf('Waiting / Blocked')
    const idleIdx = orderOf('Idle / Empty')

    expect(runningIdx).toBeGreaterThanOrEqual(0)
    expect(readyIdx).toBeGreaterThanOrEqual(0)
    expect(waitingIdx).toBeGreaterThanOrEqual(0)
    expect(idleIdx).toBeGreaterThanOrEqual(0)

    expect(runningIdx).toBeLessThan(readyIdx)
    expect(readyIdx).toBeLessThan(waitingIdx)
    expect(waitingIdx).toBeLessThan(idleIdx)
  })

  it('uses the new test-ids epic-section-running, epic-section-ready, epic-section-waiting, epic-section-idle', async () => {
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-section-running')).toBeTruthy()
    expect(screen.getByTestId('epic-section-ready')).toBeTruthy()
    expect(screen.getByTestId('epic-section-waiting')).toBeTruthy()
    expect(screen.getByTestId('epic-section-idle')).toBeTruthy()
  })

  it('does not render the legacy epic-section-active', async () => {
    renderPage()
    await waitForList()
    expect(screen.queryByTestId('epic-section-active')).toBeNull()
  })

  it('retains epic-section-done and epic-section-closed', async () => {
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-section-done')).toBeTruthy()
    expect(screen.getByTestId('epic-section-closed')).toBeTruthy()
  })

  it('renders a Running epic above a Ready-to-start epic when both groups are present', async () => {
    renderPage()
    await waitForList()

    const running = screen.getByText('Running Epic')
    const ready = screen.getByText('Ready To Start Epic')

    const position = running.compareDocumentPosition(ready)
    expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('expands the four active groups by default and folds Done / Closed', async () => {
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-section-running-toggle')).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('epic-section-ready-toggle')).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('epic-section-waiting-toggle')).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('epic-section-idle-toggle')).toHaveAttribute('aria-expanded', 'true')

    expect(screen.getByTestId('epic-section-done-toggle')).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByTestId('epic-section-closed-toggle')).toHaveAttribute('aria-expanded', 'false')

    expect(screen.queryByText('Done Epic')).toBeNull()
    expect(screen.queryByText('Closed Epic')).toBeNull()
  })

  it('expands the Done section when its toggle is clicked and collapses it again', async () => {
    renderPage()
    await waitForList()

    const toggle = screen.getByTestId('epic-section-done-toggle')
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Done Epic')).toBeTruthy()

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('Done Epic')).toBeNull()
  })

  it('expands the Closed section when its toggle is clicked', async () => {
    renderPage()
    await waitForList()

    const toggle = screen.getByTestId('epic-section-closed-toggle')
    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Closed Epic')).toBeTruthy()
  })

  it('does not change server data when toggling sections', async () => {
    const dataSnapshot = [
      runningEpic,
      readyToStartEpic,
      waitingBlockedEpic,
      idleReadyEpic,
      idleEmptyEpic,
      doneEpic,
      closedEpic,
    ]
    _epicsData = dataSnapshot

    renderPage()
    await waitForList()

    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))
    fireEvent.click(screen.getByTestId('epic-section-closed-toggle'))

    expect(_epicsData).toBe(dataSnapshot)
  })
})

describe('EpicListPage per-group card content', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
  })

  afterEach(() => {
    cleanup()
  })

  it('shows the in-progress issue number and title on a Running card', async () => {
    _epicsData = [runningEpic]
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('In progress: #2')
    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('Continue work')
  })

  it('does not render a Start next issue control on a Running card even when a queued next exists', async () => {
    _epicsData = [runningEpic]
    renderPage()
    await waitForList()
    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('shows the next issue number and title on a Ready-to-start card', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Next: #3')
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Start me')
  })

  it('renders the manual start control labelled exactly "Start next issue" on a Ready-to-start card', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    const startButton = screen.getByTestId('epic-card-start')
    expect(startButton).toBeTruthy()
    expect(startButton.textContent).toBe('Start next issue')
    expect(startButton).not.toBeDisabled()
  })

  it('does not render a Start next issue control on a Waiting/Blocked card', async () => {
    _epicsData = [waitingBlockedEpic]
    renderPage()
    await waitForList()
    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('shows the nextIssueReason text on a Waiting/Blocked card', async () => {
    _epicsData = [waitingBlockedEpic]
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Draft blocked on review')
  })

  it('shows "Ready to mark done" on an Idle/Empty card with progress.readyToMarkDone=true', async () => {
    _epicsData = [idleReadyEpic]
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-card-ready')).toHaveTextContent('Ready to mark done')
    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('shows "No linked issues" on an Idle/Empty card with no linked work', async () => {
    _epicsData = [idleEmptyEpic]
    renderPage()
    await waitForList()

    expect(screen.getByTestId('epic-card-empty')).toHaveTextContent('No linked issues')
    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('renders a Done completion phrase on Done cards', async () => {
    _epicsData = [doneEpic]
    renderPage()
    await waitForList()
    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))

    expect(screen.getByText('Done Epic')).toBeTruthy()
    const doneCard = screen.getByText('Done Epic').closest('.cursor-pointer') as HTMLElement
    expect(doneCard.textContent).toContain('Completed')
    expect(doneCard.textContent).not.toContain('Ready to mark done')
  })

  it('renders a Closed phrase on Closed cards', async () => {
    _epicsData = [closedEpic]
    renderPage()
    await waitForList()
    fireEvent.click(screen.getByTestId('epic-section-closed-toggle'))

    expect(screen.getByText('Closed Epic')).toBeTruthy()
    const closedCard = screen.getByText('Closed Epic').closest('.cursor-pointer') as HTMLElement
    expect(closedCard.textContent).toContain('Closed')
    expect(closedCard.textContent).not.toContain('Ready to mark done')
  })
})

describe('EpicListPage numbered display', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
  })

  afterEach(() => {
    cleanup()
  })

  it('renders #N as the primary epic identifier when number is present', async () => {
    const numbered = [
      makeEpic({
        number: 7,
        title: 'Numbered Ready Epic',
        progress: readyToStartEpic.progress,
      }),
      makeEpic({
        number: 8,
        title: 'Numbered Done Epic',
        status: EpicStatus.Done,
        progress: doneEpic.progress,
      }),
    ]
    _epicsData = numbered
    renderPage()
    await waitForList()

    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))

    const numbers = screen.getAllByTestId('epic-number')
    expect(numbers.length).toBeGreaterThanOrEqual(2)
    expect(numbers.some((n) => n.textContent === '#7')).toBe(true)
    expect(numbers.some((n) => n.textContent === '#8')).toBe(true)
  })

  it('displays the canonical epic number', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    const numbers = screen.getAllByTestId('epic-number')
    expect(numbers[0]).toHaveTextContent('#2')
  })
})

describe('EpicListPage responsive markup', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('forbids fixed-width and min-width on status/priority badges, progress bar, current/next text, and Start next issue control', async () => {
    _epicsData = [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic]
    renderPage()
    await waitForList()

    const forbiddenPattern = /(?:^|\s)(?:w-\d+|min-w-\[|w-\[)/

    const badgeNodes = Array.from(document.querySelectorAll('[data-slot="badge"]'))
    expect(badgeNodes.length).toBeGreaterThan(0)
    for (const node of badgeNodes) {
      expect(forbiddenPattern.test(node.className)).toBe(false)
    }

    const progressBars = screen.getAllByTestId('epic-progress-bar')
    expect(progressBars.length).toBeGreaterThan(0)
    for (const progressBar of progressBars) {
      expect(progressBar.className).toContain('w-full')
      expect(forbiddenPattern.test(progressBar.className)).toBe(false)
    }

    const inProgress = screen.getAllByTestId('epic-card-in-progress')
    for (const node of inProgress) {
      expect(forbiddenPattern.test(node.className)).toBe(false)
      expect(node.className).toContain('break-words')
    }

    const nextText = screen.getAllByTestId('epic-card-next')
    for (const node of nextText) {
      expect(forbiddenPattern.test(node.className)).toBe(false)
      expect(node.className).toContain('break-words')
    }

    const startButton = screen.getByTestId('epic-card-start')
    expect(forbiddenPattern.test(startButton.className)).toBe(false)
  })

  it('keeps status badge and current/next issue number visible (state-bearing strings not truncated)', async () => {
    _epicsData = [
      makeEpic({
        title:
          'A very long epic title that should wrap across multiple lines on narrow viewports without clipping the badge',
        progress: {
          deliveredCount: 0,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [
            {
              number: 12345,
              title: 'A current issue with a very long descriptive title that may otherwise be truncated',
              health: 'active',
            },
          ],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      }),
      makeEpic({
        title:
          'Another long ready-to-start epic title used to confirm that wrapping also keeps the next issue number visible',
        progress: {
          deliveredCount: 0,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: {
            number: 67890,
            title: 'A queued next issue with a long descriptive title that may otherwise be truncated',
          },
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      }),
    ]

    renderPage()
    await waitForList()

    const inProgress = screen.getByTestId('epic-card-in-progress')
    expect(inProgress.textContent).toContain('#12345')
    expect(inProgress.className).not.toContain('truncate')

    const next = screen.getByTestId('epic-card-next')
    expect(next.textContent).toContain('#67890')
    expect(next.className).not.toContain('truncate')

    const heading = screen.getByText(
      'A very long epic title that should wrap across multiple lines on narrow viewports without clipping the badge',
    )
    expect(heading.className).toContain('break-words')
  })
})

describe('EpicListPage empty state', () => {
  it('renders empty project state when there are zero epics', async () => {
    _epicsData = []
    renderPage()
    await screen.findByText('No epics yet')
    expect(screen.getByText('Create your first Epic')).toBeTruthy()
  })
})
