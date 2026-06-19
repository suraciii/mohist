// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { EpicStatus } from '../../../entities/epic'
import { EpicListPage } from './EpicListPage'

const mockNavigate = vi.fn()

const mocks = vi.hoisted(() => ({
  useEpics: vi.fn(),
  useCreateEpic: vi.fn(),
}))

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  }
})

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpics: mocks.useEpics,
    useCreateEpic: mocks.useCreateEpic,
  }
})

function makeEpic(overrides: Record<string, unknown>) {
  return {
    id: 'epic-id',
    number: null,
    title: 'Epic',
    description: 'desc',
    priority: 'p1',
    status: EpicStatus.Active,
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

const activeWithBoth = makeEpic({
  id: 'epic-active',
  title: 'Active Epic',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [],
    activeIssues: [{ id: 'issue-2', number: 2, title: 'Continue work', health: 'active' }],
    nextIssue: { id: 'issue-3', number: 3, title: 'Next thing' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
})

const activeWithOnlyNext = makeEpic({
  id: 'epic-next-only',
  title: 'Next only',
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

const activeWithOnlyInProgress = makeEpic({
  id: 'epic-in-progress-only',
  title: 'In progress only',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 3,
    blockedIssues: [{ id: 'issue-7', number: 7, title: 'Blocked work', health: 'blocked' }],
    activeIssues: [{ id: 'issue-2', number: 2, title: 'Continue work', health: 'active' }],
    nextIssue: null,
    nextIssueReason: 'Waiting on #1',
    readyToMarkDone: false,
  },
})

const activeReady = makeEpic({
  id: 'epic-ready',
  title: 'Active Ready',
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

const activeNoLinks = makeEpic({
  id: 'epic-empty',
  title: 'Empty Active',
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

describe('EpicListPage group collapse', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useEpics.mockReturnValue({
      data: [activeWithBoth, activeReady, doneEpic, closedEpic],
      isLoading: false,
    })
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('expands the Active section by default and collapses Done and Closed', () => {
    renderPage()

    expect(screen.getByTestId('epic-section-active')).toBeTruthy()
    expect(screen.getByTestId('epic-section-done-toggle')).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByTestId('epic-section-closed-toggle')).toHaveAttribute('aria-expanded', 'false')

    expect(screen.getByText('Active Epic')).toBeTruthy()
    expect(screen.getByText('Active Ready')).toBeTruthy()

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
    const dataSnapshot = [activeWithBoth, activeReady, doneEpic, closedEpic]
    mocks.useEpics.mockReturnValue({ data: dataSnapshot, isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))
    fireEvent.click(screen.getByTestId('epic-section-closed-toggle'))

    expect(mocks.useEpics).toHaveBeenCalledTimes(1)
    expect(mocks.useEpics.mock.results[0].value.data).toBe(dataSnapshot)
  })
})

describe('EpicListPage status-conditional card text', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useEpics.mockReturnValue({
      data: [activeReady, doneEpic, closedEpic],
      isLoading: false,
    })
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows Ready to mark done only for Active epics, not for Done or Closed', () => {
    renderPage()

    expect(screen.getByTestId('epic-card-ready')).toHaveTextContent('Ready to mark done')

    fireEvent.click(screen.getByTestId('epic-section-done-toggle'))
    fireEvent.click(screen.getByTestId('epic-section-closed-toggle'))

    const doneCard = screen.getByText('Done Epic').closest('[data-testid],div') as HTMLElement
    const closedCard = screen.getByText('Closed Epic').closest('[data-testid],div') as HTMLElement
    expect(doneCard.textContent).not.toContain('Ready to mark done')
    expect(closedCard.textContent).not.toContain('Ready to mark done')

    expect(screen.queryByTestId('epic-card-ready')).not.toBeNull()
    expect(screen.getAllByText('Completed').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Closed').some(node => node.textContent === 'Closed')).toBe(true)
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

describe('EpicListPage in-progress and next display', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows both in-progress and next lines when both are present on an Active epic', () => {
    mocks.useEpics.mockReturnValue({ data: [activeWithBoth], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('In progress: #2')
    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('Continue work')
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Next: #3')
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Next thing')
  })

  it('shows only the next line when there is no in-flight issue', () => {
    mocks.useEpics.mockReturnValue({ data: [activeWithOnlyNext], isLoading: false })

    renderPage()

    expect(screen.queryByTestId('epic-card-in-progress')).toBeNull()
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Next: #3')
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Start me')
  })

  it('shows only the in-progress line and falls back to nextIssueReason when no startable next exists', () => {
    mocks.useEpics.mockReturnValue({ data: [activeWithOnlyInProgress], isLoading: false })

    renderPage()

    expect(screen.getByTestId('epic-card-in-progress')).toHaveTextContent('In progress: #2')
    expect(screen.getByTestId('epic-card-next')).toHaveTextContent('Waiting on #1')
  })

  it('shows Ready to mark done for an Active epic with no in-flight and no next', () => {
    mocks.useEpics.mockReturnValue({ data: [activeReady], isLoading: false })

    renderPage()

    expect(screen.queryByTestId('epic-card-in-progress')).toBeNull()
    expect(screen.queryByTestId('epic-card-next')).toBeNull()
    expect(screen.getByTestId('epic-card-ready')).toHaveTextContent('Ready to mark done')
  })

  it('shows No linked issues when an Active epic has no linked work at all', () => {
    mocks.useEpics.mockReturnValue({ data: [activeNoLinks], isLoading: false })

    renderPage()

    expect(screen.queryByTestId('epic-card-in-progress')).toBeNull()
    expect(screen.queryByTestId('epic-card-next')).toBeNull()
    expect(screen.queryByTestId('epic-card-ready')).toBeNull()
    expect(screen.getByText('No linked issues')).toBeTruthy()
  })
})

describe('EpicListPage basic actions', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useEpics.mockReturnValue({ data: [activeWithBoth], isLoading: false })
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders grouped sections with their counts', () => {
    mocks.useEpics.mockReturnValue({
      data: [activeWithBoth, activeReady, doneEpic, closedEpic],
      isLoading: false,
    })

    renderPage()

    expect(screen.getByRole('heading', { name: /Active \(\d+\)/ })).toBeTruthy()
    expect(screen.getByText('Active Epic')).toBeTruthy()
    expect(screen.getByText('Active Ready')).toBeTruthy()
    expect(screen.getByText('1 / 3 completed')).toBeTruthy()
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
})

describe('EpicListPage numbered display', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders #N as the primary epic identifier when number is present', () => {
    const numbered = [
      makeEpic({
        id: 'epic-uuid-1-aaaa-bbbb-cccccccccccc',
        number: 7,
        title: 'Numbered Active Epic',
        progress: activeWithBoth.progress,
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
    mocks.useEpics.mockReturnValue({ data: [activeWithBoth], isLoading: false })

    renderPage()

    const numbers = screen.getAllByTestId('epic-number')
    expect(numbers[0]).toHaveTextContent('#epic-ac')
  })
})
