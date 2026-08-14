import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { toast } from 'sonner'
import { EpicStatus } from '../../../entities/epic'
import { useMswServer } from '../../../../tests/support/msw'
import {
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
    const sectionTexts = sections.map((h) => h.textContent)
    const readyIdx = sectionTexts.findIndex((t) => t?.startsWith('Ready to start'))
    const pausedIdx = sectionTexts.findIndex((t) => t?.startsWith('Paused'))
    const doneIdx = sectionTexts.findIndex((t) => t?.startsWith('Done'))

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
    const pausedBadge = badges.find((b) => b.tagName !== 'H2') as HTMLElement
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
      expect(_createEpicHandler).toHaveBeenCalledWith({
        title: 'New Goal',
        description: 'Ship the goal',
        priority: 'p1',
      })
    })
  })

  it('navigates to epic detail from a list card', async () => {
    renderPage()
    await waitForList()

    fireEvent.click(screen.getByText('Ready To Start Epic'))

    expect(screen.getByTestId('current-path').textContent).toBe('/epics/2')
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
