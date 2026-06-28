// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { EpicStatus } from '../../../entities/epic'
import { EpicListPage } from './EpicListPage'

const mockNavigate = vi.fn()

const toastMocks = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  warning: vi.fn(),
  info: vi.fn(),
}))

const startIssueMock = vi.hoisted(() => vi.fn())

const mocks = vi.hoisted(() => ({
  useEpics: vi.fn(),
  useCreateEpic: vi.fn(),
  useStartIssue: vi.fn(),
}))

const realEpicModule = await vi.importActual<typeof import('../../../entities/epic')>('../../../entities/epic')

function passthroughStartIssue() {
  mocks.useStartIssue.mockImplementation(((...args: unknown[]) =>
    realEpicModule.useStartIssue(...(args as Parameters<typeof realEpicModule.useStartIssue>))) as ReturnType<
    typeof mocks.useStartIssue
  >)
}

vi.mock('sonner', () => ({
  toast: toastMocks,
}))

const toastError = toastMocks.error

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  }
})

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    startIssue: startIssueMock,
  }
})

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpics: mocks.useEpics,
    useCreateEpic: mocks.useCreateEpic,
    useStartIssue: mocks.useStartIssue,
  }
})

function makeEpic(overrides: Record<string, unknown>) {
  return {
    id: 'epic-id',
    number: null,
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
  id: 'epic-running',
  title: 'Running Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [{ id: 'issue-2', number: 2, title: 'Continue work', health: 'active' }],
    nextIssue: { id: 'issue-3', number: 3, title: 'Queued next' },
    nextIssueReason: 'Waiting for #2 to complete',
    readyToMarkDone: false,
  },
})

const readyToStartEpic = makeEpic({
  id: 'epic-ready-to-start',
  title: 'Ready To Start Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { id: 'issue-3', number: 3, title: 'Start me' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

const waitingBlockedEpic = makeEpic({
  id: 'epic-waiting',
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
  id: 'epic-idle-ready',
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
  id: 'epic-idle-empty',
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
  id: 'epic-done',
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
  id: 'epic-closed',
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
      <MemoryRouter initialEntries={['/epics']}>
        <Routes>
          <Route path="/epics" element={<EpicListPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('EpicListPage four-group rendering', () => {
  const createMutate = vi.fn()
  const startMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useEpics.mockReturnValue({
      data: [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic, doneEpic, closedEpic],
      isLoading: false,
    })
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders active sections in fixed priority order Running → Ready to start → Waiting/Blocked → Idle/Empty', () => {
    renderPage()

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

  it('uses the new test-ids epic-section-running, epic-section-ready, epic-section-waiting, epic-section-idle', () => {
    renderPage()

    expect(screen.getByTestId('epic-section-running')).toBeTruthy()
    expect(screen.getByTestId('epic-section-ready')).toBeTruthy()
    expect(screen.getByTestId('epic-section-waiting')).toBeTruthy()
    expect(screen.getByTestId('epic-section-idle')).toBeTruthy()
  })

  it('does not render the legacy epic-section-active', () => {
    renderPage()

    expect(screen.queryByTestId('epic-section-active')).toBeNull()
  })

  it('retains epic-section-done and epic-section-closed', () => {
    renderPage()

    expect(screen.getByTestId('epic-section-done')).toBeTruthy()
    expect(screen.getByTestId('epic-section-closed')).toBeTruthy()
  })

  it('renders a Running epic above a Ready-to-start epic when both groups are present', () => {
    renderPage()

    const running = screen.getByText('Running Epic')
    const ready = screen.getByText('Ready To Start Epic')

    const position = running.compareDocumentPosition(ready)
    expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('expands the four active groups by default and folds Done / Closed', () => {
    renderPage()

    expect(screen.getByTestId('epic-section-running-toggle')).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('epic-section-ready-toggle')).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('epic-section-waiting-toggle')).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('epic-section-idle-toggle')).toHaveAttribute('aria-expanded', 'true')

    expect(screen.getByTestId('epic-section-done-toggle')).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByTestId('epic-section-closed-toggle')).toHaveAttribute('aria-expanded', 'false')

    expect(screen.queryByText('Done Epic')).toBeNull()
    expect(screen.queryByText('Closed Epic')).toBeNull()
  })

  it('expands the Done section when its toggle is clicked and collapses it again', () => {
    renderPage()

    const toggle = screen.getByTestId('epic-section-done-toggle')
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Done Epic')).toBeTruthy()

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByText('Done Epic')).toBeNull()
  })

  it('expands the Closed section when its toggle is clicked', () => {
    renderPage()

    const toggle = screen.getByTestId('epic-section-closed-toggle')
    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByText('Closed Epic')).toBeTruthy()
  })

  it('does not change server data when toggling sections', () => {
    const dataSnapshot = [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic, doneEpic, closedEpic]
    mocks.useEpics.mockReturnValue({ data: dataSnapshot, isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))
    fireEvent.click(screen.getByTestId('epic-section-closed-toggle'))

    expect(mocks.useEpics).toHaveBeenCalledTimes(1)
    expect(mocks.useEpics.mock.results[0].value.data).toBe(dataSnapshot)
  })
})

describe('EpicListPage per-group card content', () => {
  const createMutate = vi.fn()
  const startMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows the in-progress issue number and title on a Running card', () => {
    mocks.useEpics.mockReturnValue({ data: [runningEpic], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('In progress: #2')
    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('Continue work')
  })

  it('does not render a Start next issue control on a Running card even when a queued next exists', () => {
    mocks.useEpics.mockReturnValue({ data: [runningEpic], isLoading: false })

    renderPage()

    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('shows the next issue number and title on a Ready-to-start card', () => {
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Next: #3')
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Start me')
  })

  it('renders the manual start control labelled exactly "Start next issue" on a Ready-to-start card', () => {
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    const startButton = screen.getByTestId('epic-card-start')
    expect(startButton).toBeTruthy()
    expect(startButton.textContent).toBe('Start next issue')
    expect(startButton).not.toBeDisabled()
  })

  it('does not render a Start next issue control on a Waiting/Blocked card', () => {
    mocks.useEpics.mockReturnValue({ data: [waitingBlockedEpic], isLoading: false })

    renderPage()

    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('shows the nextIssueReason text on a Waiting/Blocked card', () => {
    mocks.useEpics.mockReturnValue({ data: [waitingBlockedEpic], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Draft blocked on review')
  })

  it('shows "Ready to mark done" on an Idle/Empty card with progress.readyToMarkDone=true', () => {
    mocks.useEpics.mockReturnValue({ data: [idleReadyEpic], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-ready')).toHaveTextContent('Ready to mark done')
    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('shows "No linked issues" on an Idle/Empty card with no linked work', () => {
    mocks.useEpics.mockReturnValue({ data: [idleEmptyEpic], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-empty')).toHaveTextContent('No linked issues')
    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })

  it('renders a Done completion phrase on Done cards', () => {
    mocks.useEpics.mockReturnValue({ data: [doneEpic], isLoading: false })

    renderPage()
    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))

    expect(screen.getByText('Done Epic')).toBeTruthy()
    const doneCard = screen.getByText('Done Epic').closest('.cursor-pointer') as HTMLElement
    expect(doneCard.textContent).toContain('Completed')
    expect(doneCard.textContent).not.toContain('Ready to mark done')
  })

  it('renders a Closed phrase on Closed cards', () => {
    mocks.useEpics.mockReturnValue({ data: [closedEpic], isLoading: false })

    renderPage()
    fireEvent.click(screen.getByTestId('epic-section-closed-toggle'))

    expect(screen.getByText('Closed Epic')).toBeTruthy()
    const closedCard = screen.getByText('Closed Epic').closest('.cursor-pointer') as HTMLElement
    expect(closedCard.textContent).toContain('Closed')
    expect(closedCard.textContent).not.toContain('Ready to mark done')
  })
})

describe('EpicListPage Start next issue action', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
    passthroughStartIssue()
    startIssueMock.mockReset()
    startIssueMock.mockResolvedValue({ issue: { number: 3 }, message: 'started' })
  })

  afterEach(() => {
    cleanup()
  })

  it('invokes startIssue(next.number) on the Ready-to-start card and does not navigate to the epic detail', async () => {
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    const startButton = screen.getByTestId('epic-card-start')
    expect(startButton.textContent).toBe('Start next issue')
    fireEvent.click(startButton)

    await waitFor(() => {
      expect(startIssueMock).toHaveBeenCalledWith(3, null)
    })
    expect(startIssueMock).toHaveBeenCalledTimes(1)
    expect(mockNavigate).not.toHaveBeenCalled()
  })

  it('calls stopPropagation on the click event so the card does not navigate', () => {
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    const startButton = screen.getByTestId('epic-card-start')
    const stopPropagationSpy = vi.fn()
    const clickEvent = new MouseEvent('click', { bubbles: true, cancelable: true })
    Object.defineProperty(clickEvent, 'stopPropagation', { value: stopPropagationSpy, configurable: true })
    startButton.dispatchEvent(clickEvent)

    expect(stopPropagationSpy).toHaveBeenCalled()
  })

  it('disables the Start next issue button and shows "Starting..." while pending', () => {
    const startMutate = vi.fn()
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    const startButton = screen.getByTestId('epic-card-start')
    fireEvent.click(startButton)

    expect(startButton).toBeDisabled()
    expect(startButton.textContent).toBe('Starting...')
  })

  it('surfaces an error toast when the underlying startIssue call rejects', async () => {
    const failure = new Error('Issue is still a draft')
    startIssueMock.mockRejectedValueOnce(failure)
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('epic-card-start'))

    await waitFor(() => {
      expect(toastError).toHaveBeenCalledWith('Issue is still a draft')
    })
  })

  it('never offers a Start next issue control on Running, Waiting/Blocked or Idle/Empty cards', () => {
    mocks.useEpics.mockReturnValue({
      data: [runningEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic],
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('epic-card-start')).toBeNull()
  })
})

describe('EpicListPage basic actions', () => {
  const createMutate = vi.fn()
  const startMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders grouped sections with their counts', () => {
    mocks.useEpics.mockReturnValue({
      data: [runningEpic, readyToStartEpic, doneEpic],
      isLoading: false,
    })

    renderPage()

    expect(screen.getByRole('heading', { name: /Running \(1\)/ })).toBeTruthy()
    expect(screen.getByRole('heading', { name: /Ready to start \(1\)/ })).toBeTruthy()
    expect(screen.getByText('Running Epic')).toBeTruthy()
    expect(screen.getByText('Ready To Start Epic')).toBeTruthy()
    expect(screen.getAllByText('1 / 3 completed').length).toBeGreaterThanOrEqual(1)
  })

  it('renders without a Paused section when there are zero paused epics', () => {
    renderPage()

    expect(screen.queryByRole('heading', { name: 'Paused' })).toBeNull()
  })

  it('renders a Paused section between the active groups and Done with amber badge and de-emphasized cards', () => {
    mocks.useEpics.mockReturnValue({
      data: [
        readyToStartEpic,
        {
          id: 'epic-paused',
          number: null,
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
            nextIssue: null,
            readyToMarkDone: false,
          },
        },
        doneEpic,
      ],
      isLoading: false,
    })

    renderPage()

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
  })

  it('opens create dialog and submits title description and priority', async () => {
    renderPage()

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

    expect(createMutate).toHaveBeenCalledWith(
      { title: 'New Goal', description: 'Ship the goal', priority: 'p1' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })

  it('navigates to epic detail from a list card', () => {
    renderPage()

    fireEvent.click(screen.getByText('Ready To Start Epic'))

    expect(mockNavigate).toHaveBeenCalledWith('/epics/epic-ready-to-start')
  })
})

describe('EpicListPage numbered display', () => {
  const createMutate = vi.fn()
  const startMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders #N as the primary epic identifier when number is present', () => {
    const numbered = [
      makeEpic({
        id: 'epic-uuid-1-aaaa-bbbb-cccccccccccc',
        number: 7,
        title: 'Numbered Ready Epic',
        progress: readyToStartEpic.progress,
      }),
      makeEpic({
        id: 'epic-uuid-2-aaaa-bbbb-dddddddddddd',
        number: 8,
        title: 'Numbered Done Epic',
        status: EpicStatus.Done,
        progress: doneEpic.progress,
      }),
    ]
    mocks.useEpics.mockReturnValue({ data: numbered, isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))

    const numbers = screen.getAllByTestId('epic-number')
    expect(numbers.length).toBeGreaterThanOrEqual(2)
    expect(numbers.some(n => n.textContent === '#7')).toBe(true)
    expect(numbers.some(n => n.textContent === '#8')).toBe(true)
  })

  it('falls back to the truncated UUID when epic number is null', () => {
    mocks.useEpics.mockReturnValue({ data: [readyToStartEpic], isLoading: false })

    renderPage()

    const numbers = screen.getAllByTestId('epic-number')
    expect(numbers[0]).toHaveTextContent('#epic-re')
  })
})

describe('EpicListPage mobile no-overflow invariants', () => {
  const createMutate = vi.fn()
  const startMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  function stubWidth(width: number) {
    Object.defineProperty(document.documentElement, 'scrollWidth', {
      configurable: true,
      get: () => width,
    })
    Object.defineProperty(document.documentElement, 'clientWidth', {
      configurable: true,
      get: () => width,
    })
  }

  function expectNoOverflow(width: number) {
    stubWidth(width)
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(document.documentElement.clientWidth)
  }

  it('renders across all four active groups without horizontal overflow at 320, 390, and 430 px', () => {
    mocks.useEpics.mockReturnValue({
      data: [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic],
      isLoading: false,
    })

    for (const width of [320, 390, 430]) {
      cleanup()
      renderPage()
      expectNoOverflow(width)
    }
  })

  it('forbids fixed-width and min-width on status/priority badges, progress bar, current/next text, and Start next issue control', () => {
    mocks.useEpics.mockReturnValue({
      data: [runningEpic, readyToStartEpic, waitingBlockedEpic, idleReadyEpic, idleEmptyEpic],
      isLoading: false,
    })

    renderPage()

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

  it('keeps status badge and current/next issue number visible (state-bearing strings not truncated)', () => {
    mocks.useEpics.mockReturnValue({
      data: [
        makeEpic({
          id: 'epic-running-long',
          title: 'A very long epic title that should wrap across multiple lines on narrow viewports without clipping the badge',
          progress: {
            deliveredCount: 0,
            totalIssueCount: 1,
            blockedIssues: [],
            activeIssues: [{ id: 'i1', number: 12345, title: 'A current issue with a very long descriptive title that may otherwise be truncated', health: 'active' }],
            nextIssue: null,
            nextIssueReason: null,
            readyToMarkDone: false,
          },
        }),
        makeEpic({
          id: 'epic-ready-long',
          title: 'Another long ready-to-start epic title used to confirm that wrapping also keeps the next issue number visible',
          progress: {
            deliveredCount: 0,
            totalIssueCount: 1,
            blockedIssues: [],
            activeIssues: [],
            nextIssue: { id: 'i1', number: 67890, title: 'A queued next issue with a long descriptive title that may otherwise be truncated' },
            nextIssueReason: null,
            readyToMarkDone: false,
          },
        }),
      ],
      isLoading: false,
    })

    renderPage()

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