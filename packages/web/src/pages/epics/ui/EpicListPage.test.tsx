import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { toast } from 'sonner'
import { ProjectProvider } from '../../../entities/project'
import { EpicStatus } from '../../../entities/epic'
import { EpicListPage } from './EpicListPage'
import { useMswServer } from '../../../../tests/support/msw'

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

let _epicsData: unknown[] = []
const _epicsRequests: { search?: string; sort?: string; dir?: string }[] = []
const _startIssueHandler = vi.fn()
const _createEpicHandler = vi.fn()
let _blockStartIssue = false
let _startIssueError: { status: number; error: string } | null = null
let nextEpicNumber = 100

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
    const body = await request.json() as Record<string, string>
    _createEpicHandler(body)
    return HttpResponse.json({ success: true, data: { projectId: 'proj-1', number: 999, ...body } })
  }),
  http.post('*/api/projects/:projectId/issues/:issueNumber/start', ({ params }) => {
    _startIssueHandler(Number(params.issueNumber))
    if (_blockStartIssue) return new Promise(() => {})
    if (_startIssueError) {
      return HttpResponse.json(
        { success: false, error: _startIssueError.error },
        { status: _startIssueError.status },
      )
    }
    return HttpResponse.json({ success: true, data: { issue: { number: Number(params.issueNumber) }, message: 'started' } })
  }),
)

function makeEpic(overrides: Record<string, unknown>) {
  const number = typeof overrides.number === 'number' ? overrides.number : nextEpicNumber++
  return {
    projectId: 'proj-1',
    number,
    title: 'Epic',
    description: 'desc',
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
    ...overrides,
  }
}

const runningEpic = makeEpic({
  number: 1,
  title: 'Running Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [{ number: 2, title: 'Continue work', health: 'active' }],
    nextIssue: { number: 3, title: 'Queued next' },
    nextIssueReason: 'Waiting for #2 to complete',
    readyToMarkDone: false,
  },
})

const readyToStartEpic = makeEpic({
  number: 2,
  title: 'Ready To Start Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { number: 3, title: 'Start me' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

const waitingBlockedEpic = makeEpic({
  number: 3,
  title: 'Waiting Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: 'Draft blocked on review',
    readyToMarkDone: false,
  },
})

const idleReadyEpic = makeEpic({
  number: 4,
  title: 'Idle Ready Epic',
  progress: {
    deliveredCount: 3,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: true,
  },
})

const idleEmptyEpic = makeEpic({
  number: 5,
  title: 'Empty Epic',
  progress: {
    deliveredCount: 0,
    totalIssueCount: 0,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

const doneEpic = makeEpic({
  number: 6,
  title: 'Done Epic',
  status: EpicStatus.Done,
  progress: {
    deliveredCount: 2,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: true,
  },
})

const closedEpic = makeEpic({
  number: 7,
  title: 'Closed Epic',
  status: EpicStatus.Closed,
  progress: {
    deliveredCount: 2,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: null,
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/epics']}>
          <LocationProbe />
          <Routes>
            <Route path="/epics" element={<EpicListPage />} />
            <Route path="/epics/:number" element={<div>Epic Detail</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

async function waitForList() {
  const sections = ['epic-section-running', 'epic-section-ready', 'epic-section-waiting', 'epic-section-idle', 'epic-section-done', 'epic-section-closed', 'epic-section-paused']
  await Promise.any(sections.map(id => screen.findByTestId(id, {}, { timeout: 5000 })))
}

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
    const headingTexts = headings.map(h => h.textContent ?? '')
    const orderOf = (prefix: string) => headingTexts.findIndex(t => t.startsWith(prefix))

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
    const dataSnapshot = [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic, doneEpic, closedEpic]
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

describe('EpicListPage Start next issue action', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
  })

  afterEach(() => {
    cleanup()
  })

  it('invokes startIssue(next.number) on the Ready-to-start card and does not navigate to the epic detail', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    const startButton = screen.getByTestId('epic-card-start')
    expect(startButton.textContent).toBe('Start next issue')
    fireEvent.click(startButton)

    await vi.waitFor(() => {
      expect(_startIssueHandler).toHaveBeenCalledWith(3)
    })
    expect(_startIssueHandler).toHaveBeenCalledTimes(1)
    expect(screen.getByTestId('current-path').textContent).toBe('/epics')
  })

  it('calls stopPropagation on the click event so the card does not navigate', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    const startButton = screen.getByTestId('epic-card-start')
    const stopPropagationSpy = vi.fn()
    const clickEvent = new MouseEvent('click', { bubbles: true, cancelable: true })
    Object.defineProperty(clickEvent, 'stopPropagation', { value: stopPropagationSpy, configurable: true })
    startButton.dispatchEvent(clickEvent)

    expect(stopPropagationSpy).toHaveBeenCalled()
  })

  it('disables the Start next issue button and shows "Starting..." while pending', async () => {
    _blockStartIssue = true
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    const startButton = screen.getByTestId('epic-card-start')
    fireEvent.click(startButton)

    await vi.waitFor(() => {
      expect(startButton).toBeDisabled()
      expect(startButton.textContent).toBe('Starting...')
    })
  })

  it('surfaces an error toast when the underlying startIssue call rejects', async () => {
    _startIssueError = { status: 400, error: 'Issue is still a draft' }
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()

    fireEvent.click(screen.getByTestId('epic-card-start'))

    await vi.waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Issue is still a draft')
    })
  })

  it('never offers a Start next issue control on Running, Waiting/Blocked or Idle/Empty cards', async () => {
    _epicsData = [runningEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic]
    renderPage()
    await waitForList()

    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })
})

describe('EpicListPage basic actions', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
    _epicsData = [readyToStartEpic]
  })

  afterEach(() => {
    cleanup()
  })

  it('renders grouped sections with their counts', async () => {
    _epicsData = [runningEpic, readyToStartEpic, doneEpic]
    renderPage()
    await waitForList()

    expect(screen.getByRole('heading', { name: /Running \(1\)/ })).toBeTruthy()
    expect(screen.getByRole('heading', { name: /Ready to start \(1\)/ })).toBeTruthy()
    expect(screen.getByText('Running Epic')).toBeTruthy()
    expect(screen.getByText('Ready To Start Epic')).toBeTruthy()
    expect(screen.getAllByText('1 / 3 completed').length).toBeGreaterThanOrEqual(1)
  })

  it('renders without a Paused section when there are zero paused epics', async () => {
    renderPage()
    await waitForList()

    expect(screen.queryByRole('heading', { name: 'Paused' })).toBeNull()
  })

  it('renders a Paused section between the active groups and Done with amber badge and de-emphasized cards', async () => {
    _epicsData = [
      readyToStartEpic,
      {
        projectId: 'proj-1',
        number: 8,
        title: 'Paused Epic',
        description: 'On hold',
        priority: 'p2',
        status: EpicStatus.Paused,
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        progress: {
          deliveredCount: 0,
          totalIssueCount: 2,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: { number: 11, title: 'Resume-ready paused work' },
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      },
      doneEpic,
    ]

    renderPage()
    await waitForList()

    const sections = screen.getAllByRole('heading', { level: 2 })
    const sectionTexts = sections.map(h => h.textContent)
    const readyIdx = sectionTexts.findIndex(t => t?.startsWith('Ready to start'))
    const pausedIdx = sectionTexts.findIndex(t => t?.startsWith('Paused'))
    const doneIdx = sectionTexts.findIndex(t => t?.startsWith('Done'))

    expect(readyIdx).not.toBe(-1)
    expect(pausedIdx).not.toBe(-1)
    expect(doneIdx).not.toBe(-1)
    expect(readyIdx).toBeLessThan(pausedIdx)
    expect(pausedIdx).toBeLessThan(doneIdx)

    const pausedCard = screen.getByText('Paused Epic').closest('[data-slot="card"]')
    expect(pausedCard).toBeTruthy()
    expect(pausedCard!.className).toContain('opacity-60')

    const badges = screen.getAllByText('Paused')
    expect(badges.length).toBeGreaterThan(0)
    const pausedBadge = badges.find(b => b.tagName !== 'H2') as HTMLElement
    expect(pausedBadge).toBeTruthy()
    expect(pausedCard!.textContent).toContain('Next: #11')
    expect(pausedCard!.textContent).toContain('Resume-ready paused work')

    const pausedStartButton = pausedCard!.querySelector('[data-testid="epic-card-start"]') as HTMLElement
    expect(pausedStartButton).toBeTruthy()
    expect(pausedStartButton.textContent).toBe('Start next issue')

    fireEvent.click(pausedStartButton)

    await vi.waitFor(() => {
      expect(_startIssueHandler).toHaveBeenCalledWith(11)
    })
    expect(screen.getByTestId('current-path').textContent).toBe('/epics')
  })

  it('keeps the legacy paused progress fallback for waiting, ready-to-mark-done, and empty paused cards', async () => {
    _epicsData = [
      makeEpic({
        title: 'Paused Waiting Epic',
        status: EpicStatus.Paused,
        progress: {
          deliveredCount: 0,
          totalIssueCount: 2,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: 'Paused until external dependency lands',
          readyToMarkDone: false,
        },
      }),
      makeEpic({
        title: 'Paused Ready Epic',
        status: EpicStatus.Paused,
        progress: {
          deliveredCount: 2,
          totalIssueCount: 2,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
      }),
      makeEpic({
        title: 'Paused Empty Epic',
        status: EpicStatus.Paused,
        progress: {
          deliveredCount: 0,
          totalIssueCount: 0,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      }),
    ]

    renderPage()
    await waitForList()

    const waitingCard = screen.getByText('Paused Waiting Epic').closest('[data-slot="card"]')
    const readyCard = screen.getByText('Paused Ready Epic').closest('[data-slot="card"]')
    const emptyCard = screen.getByText('Paused Empty Epic').closest('[data-slot="card"]')
    expect(waitingCard!.textContent).toContain('Paused until external dependency lands')
    expect(readyCard!.textContent).toContain('Ready to mark done')
    expect(emptyCard!.textContent).toContain('No linked issues')
  })

  it('opens create dialog and submits title description and priority', async () => {
    renderPage()
    await waitForList()

    fireEvent.click(screen.getByRole('button', { name: 'New Epic' }))
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'New Goal' } })
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Ship the goal' } })
    fireEvent.click(screen.getByRole('combobox', { name: 'Priority' }))
    await waitFor(() => expect(screen.getByText('P1 - High')).toBeTruthy())
    const option = screen.getByText('P1 - High').closest('[data-slot="select-item"]') as HTMLElement
    fireEvent.pointerDown(option)
    fireEvent.pointerUp(option)
    fireEvent.click(option)
    fireEvent.click(screen.getByRole('button', { name: 'Create Epic' }))

    await vi.waitFor(() => {
      expect(_createEpicHandler).toHaveBeenCalledWith(
        { title: 'New Goal', description: 'Ship the goal', priority: 'p1' },
      )
    })
  })

  it('navigates to epic detail from a list card', async () => {
    renderPage()
    await waitForList()

    fireEvent.click(screen.getByText('Ready To Start Epic'))

    expect(screen.getByTestId('current-path').textContent).toBe('/epics/2')
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
    expect(numbers.some(n => n.textContent === '#7')).toBe(true)
    expect(numbers.some(n => n.textContent === '#8')).toBe(true)
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
        title: 'A very long epic title that should wrap across multiple lines on narrow viewports without clipping the badge',
        progress: {
          deliveredCount: 0,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [{ number: 12345, title: 'A current issue with a very long descriptive title that may otherwise be truncated', health: 'active' }],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      }),
      makeEpic({
        title: 'Another long ready-to-start epic title used to confirm that wrapping also keeps the next issue number visible',
        progress: {
          deliveredCount: 0,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: { number: 67890, title: 'A queued next issue with a long descriptive title that may otherwise be truncated' },
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

    const heading = screen.getByText('A very long epic title that should wrap across multiple lines on narrow viewports without clipping the badge')
    expect(heading.className).toContain('break-words')
  })
})

describe('EpicListPage search and sort controls', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicsRequests.length = 0
    _blockStartIssue = false
    _startIssueError = null
  })

  afterEach(() => {
    cleanup()
  })

  it('renders a search input bound to the toolbar', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    const input = screen.getByTestId('epic-search-input') as HTMLInputElement
    expect(input).toBeTruthy()
    expect(input.type).toBe('search')
    expect(input.value).toBe('')
    expect(input.placeholder).toBe('Filter epics by title')
  })

  it('forwards the typed search term', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    const input = screen.getByTestId('epic-search-input') as HTMLInputElement
    fireEvent.change(input, { target: { value: 'Auth' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]?.search).toBe('Auth')
    })
  })

  it('trims whitespace before forwarding the search param', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    const input = screen.getByTestId('epic-search-input') as HTMLInputElement
    fireEvent.change(input, { target: { value: '  Auth  ' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]?.search).toBe('Auth')
    })
  })

  it('clearing the search input omits the search param', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    const input = screen.getByTestId('epic-search-input') as HTMLInputElement
    fireEvent.change(input, { target: { value: 'Auth' } })
    fireEvent.change(input, { target: { value: '' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]?.search).toBeUndefined()
    })
  })

  it('renders a no-results state instead of project-empty copy for empty filtered results', async () => {
    _epicsData = []
    renderPage()
    await screen.findByTestId('epic-list-toolbar')
    fireEvent.change(screen.getByTestId('epic-search-input'), { target: { value: 'missing' } })
    await vi.waitFor(() => {
      expect(screen.getByText('No epics match this view')).toBeTruthy()
    })
    expect(screen.queryByText('No epics yet')).toBeNull()
    expect(screen.queryByText('Create your first Epic')).toBeNull()
  })

  it('renders sort field and direction selectors with default = no override', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    const sortField = screen.getByTestId('epic-sort-field') as HTMLSelectElement
    const sortDir = screen.getByTestId('epic-sort-dir') as HTMLSelectElement
    expect(sortField).toBeTruthy()
    expect(sortDir).toBeTruthy()
    expect(sortField.value).toBe('')
    expect(sortDir.value).toBe('')
    expect(screen.queryByRole('option', { name: 'Created' })).toBeNull()
  })

  it('forwards a default ascending direction when only the sort field changes', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-sort-field'), { target: { value: 'updated' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: undefined, sort: 'updated', dir: 'asc' })
    })
  })

  it('forwards priority sorting when only the direction changes', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-sort-dir'), { target: { value: 'desc' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: undefined, sort: 'priority', dir: 'desc' })
    })
  })

  it('forwards sort=priority and dir=asc to useEpics when selected', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-sort-field'), { target: { value: 'priority' } })
    fireEvent.change(screen.getByTestId('epic-sort-dir'), { target: { value: 'asc' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: undefined, sort: 'priority', dir: 'asc' })
    })
  })

  it('forwards sort=updated and dir=desc to useEpics when selected', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-sort-field'), { target: { value: 'updated' } })
    fireEvent.change(screen.getByTestId('epic-sort-dir'), { target: { value: 'desc' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: undefined, sort: 'updated', dir: 'desc' })
    })
  })

  it('rejects unknown sort field values by falling back to default ordering', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-sort-dir'), { target: { value: 'desc' } })
    fireEvent.change(screen.getByTestId('epic-sort-field'), { target: { value: 'garbage-payload' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: undefined, sort: undefined, dir: undefined })
    })
  })

  it('rejects unknown dir values by falling back to default ordering', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-sort-dir'), { target: { value: 'sideways' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: undefined, sort: undefined, dir: undefined })
    })
  })

  it('combines search with sort and direction into a single call', async () => {
    _epicsData = [readyToStartEpic]
    renderPage()
    await waitForList()
    fireEvent.change(screen.getByTestId('epic-search-input'), { target: { value: 'auth' } })
    fireEvent.change(screen.getByTestId('epic-sort-field'), { target: { value: 'priority' } })
    fireEvent.change(screen.getByTestId('epic-sort-dir'), { target: { value: 'desc' } })
    await vi.waitFor(() => {
      expect(_epicsRequests[_epicsRequests.length - 1]).toEqual({ search: 'auth', sort: 'priority', dir: 'desc' })
    })
  })

  it('groups the data the server returned under the requested sort + filter', async () => {
    const authReady = makeEpic({
      title: 'Auth ready',
      progress: {
        deliveredCount: 1,
        totalIssueCount: 3,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: { number: 9, title: 'Next auth work' },
        nextIssueReason: null,
        readyToMarkDone: false,
      },
    })
    const authRunning = makeEpic({
      title: 'Auth running',
      progress: {
        deliveredCount: 1,
        totalIssueCount: 3,
        blockedIssues: [],
        activeIssues: [{ number: 4, title: 'Active auth work', health: 'active' }],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
    })
    _epicsData = [authReady, authRunning]
    renderPage()
    await waitForList()

    const running = screen.getByText('Auth running')
    const ready = screen.getByText('Auth ready')
    expect(running.compareDocumentPosition(ready) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
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
