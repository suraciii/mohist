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

const epics = [
  {
    id: 'epic-active',
    title: 'Active Epic',
    description: 'Active description',
    priority: 'p1',
    status: EpicStatus.Active,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    progress: {
      completedCount: 1,
      totalIssueCount: 3,
      blockedIssues: [],
      activeIssues: ['issue-2'],
      nextIssue: { id: 'issue-2', number: 2, title: 'Continue work' },
      readyToMarkDone: false,
    },
  },
  {
    id: 'epic-done',
    title: 'Done Epic',
    description: 'Done description',
    priority: 'p2',
    status: EpicStatus.Done,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    progress: {
      completedCount: 2,
      totalIssueCount: 2,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      readyToMarkDone: true,
    },
  },
]

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

describe('EpicListPage', () => {
  const createMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mockNavigate.mockClear()
    mocks.useEpics.mockReturnValue({ data: epics, isLoading: false })
    mocks.useCreateEpic.mockReturnValue({ mutate: createMutate, isPending: false, isError: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders grouped epics with progress and next issue', () => {
    renderPage()

    expect(screen.getByRole('heading', { name: 'Active' })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Done' })).toBeTruthy()
    expect(screen.getByText('Active Epic')).toBeTruthy()
    expect(screen.getByText('1 / 3 completed')).toBeTruthy()
    expect(screen.getByText('#2')).toBeTruthy()
    expect(screen.getByText('Continue work')).toBeTruthy()
    expect(screen.getByText('Ready to mark done')).toBeTruthy()
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

    fireEvent.click(screen.getByText('Active Epic'))

    expect(mockNavigate).toHaveBeenCalledWith('/epic/epic-active')
  })
})
