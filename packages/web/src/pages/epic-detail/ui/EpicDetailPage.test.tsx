// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicStatus } from '../../../entities/epic'
import type { LinkedIssue } from '../../../entities/epic'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import { EpicDetailPage } from './EpicDetailPage'
import { ApiError } from '../../../shared/api/client'

function linkedIssue(overrides: Pick<LinkedIssue, 'id' | 'number'> & Partial<Omit<LinkedIssue, 'id' | 'number'>>): LinkedIssue {
  return {
    title: 'Issue one',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: true,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

function issue(overrides: Record<string, unknown>) {
  return {
    isDraft: false,
    canStart: true,
    blocker: null,
    status: 'backlog',
    health: 'active',
    ...overrides,
  }
}

const mockUseNavigate = vi.hoisted(() => vi.fn())

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

const widgetBehavior = vi.hoisted(() => ({
  mode: 'default' as 'default' | 'empty' | 'error',
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

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockUseNavigate,
  }
})

vi.mock('../../../widgets/epic-dependency-graph', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/epic-dependency-graph')>()
  const { useEffect: useEffectMock, createElement: createElementMock } = await import('react')
  return {
    ...actual,
    DependencyGraphWidget: (props: {
      linkedIssues: Parameters<typeof actual.DependencyGraphWidget>[0]['linkedIssues']
      onRenderabilityChange?: (state: { renderable: boolean; reason: 'renderable' | 'cyclic' | 'empty' | null }) => void
    }) => {
      const mode = widgetBehavior.mode
      useEffectMock(() => {
        if (mode === 'empty') {
          props.onRenderabilityChange?.({ renderable: false, reason: 'empty' })
        }
      }, [mode])
      if (mode === 'default') {
        return createElementMock(actual.DependencyGraphWidget, props)
      }
      if (mode === 'error') {
        throw new Error('Simulated render error from DependencyGraphWidget')
      }
      return null
    },
  }
})
const epic = {
  id: 'epic-12345678',
  title: 'Epic title',
  description: 'Epic description',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
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
}

const issues = [
  issue({ id: 'issue-1', number: 1, title: 'Done issue', canStart: false, status: 'done', health: 'done' }),
  issue({ id: 'issue-2', number: 2, title: 'Blocked issue', canStart: false, status: 'in_progress', health: 'blocked' }),
  issue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
]

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const ui = () => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/epic/epic-12345678']}>
          <Routes>
            <Route path="/epic/:id" element={<EpicDetailPage />} />
            <Route path="/epics" element={<div>Epics</div>} />
            <Route path="/issues/:number" element={<div>Issue</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
  const result = render(ui())
  return { ...result, rerenderPage: () => result.rerender(ui()) }
}

describe('EpicDetailPage', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: epic, isLoading: false })
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

  it('renders epic progress and linked issues', () => {
    renderPage()

    expect(screen.getByText('Epic title')).toBeTruthy()
    expect(screen.getByText('Epic description')).toBeTruthy()
    expect(screen.getByText(/1 \/ 2/)).toBeTruthy()
    expect(screen.getByText(/#2 Blocked issue/)).toBeTruthy()
    expect(screen.getByText('Done issue')).toBeTruthy()
  })

  it('adds an available issue from the detail page', async () => {
    renderPage()

    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    await waitFor(() => expect(screen.getByTestId('epic-issue-search')).toBeTruthy())
    const option = screen.getByTestId('epic-issue-option')
    fireEvent.click(option)
    fireEvent.click(screen.getByRole('button', { name: 'Add Issue' }))

    expect(addMutate).toHaveBeenCalledWith(
      { epicId: 'epic-12345678', issueId: 'issue-3' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })

  it('renders structured duplicate membership errors from the API', () => {
    mocks.useAddEpicIssue.mockReturnValue({
      mutate: addMutate,
      isPending: false,
      isError: true,
      error: new ApiError(
        'Issue already belongs to Epic "Runtime model"',
        409,
        undefined,
        'DUPLICATE_EPIC_MEMBERSHIP',
        { existingEpicId: 'epic-runtime', existingEpicTitle: 'Runtime model' },
      ),
    })

    renderPage()

    expect(screen.getByText('Issue already belongs to Epic #epic-run Runtime model.')).toBeTruthy()
  })

  it('removes a linked issue from the detail page', () => {
    renderPage()

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0])

    expect(removeMutate).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))

    expect(removeMutate).toHaveBeenCalledWith({ epicId: 'epic-12345678', issueId: 'issue-1' })
  })
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

const numberedEpic = {
  id: 'epic-uuid-aaaa-bbbb-cccccccccccc',
  number: 12,
  title: 'Numbered Epic',
  description: 'Has a number',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
  linkedIssues: [
    linkedIssue({ id: 'issue-1', number: 1, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
  ],
}

describe('EpicDetailPage numbered display', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: numberedEpic, isLoading: false })
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

  it('renders #N as the primary epic identifier when number is present', () => {
    renderPage()

    const label = screen.getByTestId('epic-number')
    expect(label).toHaveTextContent('#12')
  })

  it('does not display a truncated UUID as the primary epic identifier when number is present', () => {
    renderPage()

    const label = screen.getByTestId('epic-number')
    const text = label.textContent ?? ''
    expect(text).not.toContain('epic-uuid-')
    expect(text).not.toContain('aaaa-bbbb')
    expect(text).not.toContain('cccccccccccc')
  })

  it('falls back to the truncated UUID when epic number is null', () => {
    mocks.useEpic.mockReturnValue({ data: { ...epic, number: null }, isLoading: false })
    renderPage()

    const label = screen.getByTestId('epic-number')
    expect(label).toHaveTextContent('#epic-123')
  })
})

const searchEpic = {
  ...epic,
  linkedIssues: [
    linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
  ],
}

const searchIssues = [
  { id: 'issue-1', number: 1, title: 'Done issue', status: 'done' as const, isDraft: false, canStart: false, blocker: null },
  {
    id: 'issue-archived',
    number: 4,
    title: 'Archived candidate',
    status: 'backlog' as const,
    archivedAt: '2026-01-15T00:00:00Z',
  },
  {
    id: 'issue-closed',
    number: 5,
    title: 'Closed candidate',
    status: 'done' as const,
  },
  {
    id: 'issue-blocked',
    number: 6,
    title: 'Blocked candidate',
    status: 'backlog' as const,
    isDraft: false,
    canStart: false,
    blocker: { kind: 'waiting-for', issue: { number: 1, title: 'Done issue' } },
  },
  { id: 'issue-2', number: 2, title: 'Blocked issue', status: 'in_progress' as const, isDraft: false, canStart: false, blocker: null },
  { id: 'issue-3', number: 3, title: 'Candidate issue', status: 'in_progress' as const, isDraft: false, canStart: true, blocker: null },
]

describe('EpicDetailPage searchable Add Issue', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: searchEpic, isLoading: false })
    mocks.useIssues.mockReturnValue({ data: searchIssues })
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

  it('filters candidates by issue number or title when search text is typed', async () => {
    renderPage()

    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    const search = await screen.findByTestId('epic-issue-search')
    expect(screen.getAllByTestId('epic-issue-option').map(node => node.getAttribute('data-issue-id')))
      .toEqual(['issue-archived', 'issue-closed', 'issue-blocked', 'issue-2', 'issue-3'])

    fireEvent.change(search, { target: { value: 'archived' } })
    await waitFor(() => {
      const visible = screen.getAllByTestId('epic-issue-option').map(node => node.getAttribute('data-issue-id'))
      expect(visible).toEqual(['issue-archived'])
    })

    fireEvent.change(search, { target: { value: '#6' } })
    await waitFor(() => {
      const visible = screen.getAllByTestId('epic-issue-option').map(node => node.getAttribute('data-issue-id'))
      expect(visible).toEqual(['issue-blocked'])
    })

    fireEvent.change(search, { target: { value: 'no-match-query' } })
    expect(screen.queryByTestId('epic-issue-option')).toBeNull()
  })

  it('disables closed, archived, and non-startable candidates with inline reasons', async () => {
    renderPage()

    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    await screen.findByTestId('epic-issue-search')

    const options = screen.getAllByTestId('epic-issue-option')
    const findOption = (issueId: string) =>
      options.find(node => node.getAttribute('data-issue-id') === issueId) as HTMLElement
    const unavailable = options
      .filter(node => node.getAttribute('data-unavailable') === 'true')
      .map(node => node.getAttribute('data-issue-id'))
    expect(unavailable).toEqual(['issue-archived', 'issue-closed', 'issue-blocked'])

    const archived = findOption('issue-archived')
    const closed = findOption('issue-closed')
    const blocked = findOption('issue-blocked')

    expect(archived.hasAttribute('disabled')).toBe(true)
    expect(closed.hasAttribute('disabled')).toBe(true)
    expect(blocked.hasAttribute('disabled')).toBe(true)

    expect(screen.getByText('Archived')).toBeTruthy()
    expect(screen.getByText('Closed')).toBeTruthy()
    expect(screen.getByText('Waiting for #1')).toBeTruthy()

    fireEvent.click(archived)
    fireEvent.click(blocked)
    expect(addMutate).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Add Issue' })).toBeDisabled()
  })

  it('disables the submit button when no candidate is selected', () => {
    renderPage()

    expect(screen.getByRole('button', { name: 'Add Issue' })).toBeDisabled()
  })

  it('disables the trigger and submit when no selectable candidate exists', () => {
    const blockedEpic = {
      ...searchEpic,
      linkedIssues: [],
    }
    mocks.useEpic.mockReturnValue({ data: blockedEpic, isLoading: false })
    mocks.useIssues.mockReturnValue({
      data: [
        { id: 'issue-archived', number: 4, title: 'Archived candidate', status: 'backlog' as const, archivedAt: '2026-01-15T00:00:00Z' },
        { id: 'issue-closed', number: 5, title: 'Closed candidate', status: 'done' as const },
      ],
    })

    renderPage()

    const trigger = screen.getByTestId('epic-issue-selector-trigger')
    expect(trigger).toBeDisabled()
    expect(trigger).toHaveTextContent('No selectable issues')
    expect(screen.getByRole('button', { name: 'Add Issue' })).toBeDisabled()
  })
})

describe('EpicDetailPage edit flow', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  function defaultEpic() {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: 'active',
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [{ id: 'issue-1', number: 1, title: 'Active issue', health: 'active' }],
    nextIssue: { id: 'issue-1', number: 1, title: 'Active issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Member issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p2' }),
      ],
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: defaultEpic(), isLoading: false })
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

  it('opens the edit dialog prefilled with current epic metadata', () => {
    renderPage()

    fireEvent.click(screen.getByTestId('edit-epic-button'))

    const titleInput = screen.getByLabelText('Title') as HTMLInputElement
    const descriptionInput = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(titleInput.value).toBe('Epic title')
    expect(descriptionInput.value).toBe('Epic description')
  })

  it('saves the edit through the PATCH API and refreshes displayed metadata', async () => {
    const refreshedEpic = {
      ...defaultEpic(),
      title: 'Renamed Epic',
      description: 'Updated description',
      priority: 'p0',
      updatedAt: '2026-01-02T00:00:00Z',
    }

    mocks.useEpic
      .mockReturnValueOnce({ data: defaultEpic(), isLoading: false })
      .mockReturnValue({ data: refreshedEpic, isLoading: false })

    renderPage()

    fireEvent.click(screen.getByTestId('edit-epic-button'))
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Renamed Epic' } })
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Updated description' } })
    fireEvent.click(screen.getByRole('combobox', { name: 'Priority' }))

    const highOption = await screen.findByText('P0 - Critical')
    const optionEl = highOption.closest('[data-slot="select-item"]') as HTMLElement
    fireEvent.pointerDown(optionEl)
    fireEvent.pointerUp(optionEl)
    fireEvent.click(optionEl)

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(updateMutate).toHaveBeenCalledTimes(1)
    const [args] = updateMutate.mock.calls[0]
    expect(args).toEqual({
      id: 'epic-12345678',
      data: {
        title: 'Renamed Epic',
        description: 'Updated description',
        priority: 'p0',
      },
    })
    expect(updateMutate.mock.calls[0][1]).toEqual(
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })

  it('reflects updated title, description, and priority when useEpic returns refreshed data', () => {
    const refreshedEpic = {
      ...defaultEpic(),
      title: 'Renamed Epic',
      description: 'Updated description',
      priority: 'p0',
      updatedAt: '2026-01-02T00:00:00Z',
    }

    mocks.useEpic.mockReturnValue({ data: refreshedEpic, isLoading: false })

    renderPage()

    expect(screen.getByRole('heading', { name: 'Renamed Epic' })).toBeTruthy()
    expect(screen.getByText('Updated description')).toBeTruthy()
    const updatedBadges = screen.getAllByText('P0')
    expect(updatedBadges.length).toBeGreaterThan(0)
  })

  it('does not change linked issue membership or lifecycle status in the UI during the edit', () => {
    renderPage()

    expect(screen.getByText('Member issue')).toBeTruthy()
    expect(screen.getByTestId('linked-issues-list-region')).toHaveTextContent('active')

    fireEvent.click(screen.getByTestId('edit-epic-button'))
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Renamed' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(screen.getByText('Member issue')).toBeTruthy()
    expect(screen.getByTestId('linked-issues-list-region')).toHaveTextContent('active')
  })

  it('disables the save button while the update is pending', () => {
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: true, isError: false })

    renderPage()

    fireEvent.click(screen.getByTestId('edit-epic-button'))

    const saveButton = screen.getByRole('button', { name: 'Saving...' })
    expect(saveButton).toBeDisabled()
  })

  it('shows update errors from the API in the dialog', () => {
    mocks.useUpdateEpic.mockReturnValue({
      mutate: updateMutate,
      isPending: false,
      isError: true,
      error: new ApiError('Update failed: invalid priority', 400),
    })

    renderPage()

    fireEvent.click(screen.getByTestId('edit-epic-button'))

    expect(screen.getByText('Update failed: invalid priority')).toBeTruthy()
  })
})

describe('EpicDetailPage markdown description', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  const markdownDescription = [
    '## Goal',
    '',
    'Ship the epic board fix with:',
    '',
    '- priority ordering',
    '- **accurate** progress',
    '- and *next* issue',
    '',
    'See [the design](./design.md).',
  ].join('\n')

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: markdownDescription,
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

  it('renders headings, lists, and emphasis as formatted content via MarkdownReader', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

    renderPage()

    const container = screen.getByTestId('epic-description')
    expect(container.querySelector('.markdown-reader')).toBeTruthy()

    const heading = screen.getByRole('heading', { name: 'Goal' })
    expect(heading).toBeTruthy()
    expect(heading.tagName).toBe('H4')
    expect(container.textContent).not.toContain('## Goal')
    expect(container.textContent).not.toContain('- priority ordering')

    const listItems = container.querySelectorAll('li')
    expect(listItems.length).toBe(3)

    const boldNodes = container.querySelectorAll('strong')
    const emphasisNodes = container.querySelectorAll('em')
    expect(boldNodes.length).toBeGreaterThan(0)
    expect(emphasisNodes.length).toBeGreaterThan(0)
    expect(container.textContent).not.toContain('**accurate**')
  })

  it('renders a plain description readably through MarkdownReader without spurious formatting', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ description: 'Just a plain description with no markdown.' }),
      isLoading: false,
    })

    renderPage()

    const container = screen.getByTestId('epic-description')
    expect(container.querySelector('.markdown-reader')).toBeTruthy()
    expect(container.textContent).toContain('Just a plain description with no markdown.')
    expect(container.querySelectorAll('h1, h2, h3, h4, h5, h6').length).toBe(0)
  })
})

describe('EpicDetailPage current activity listing', () => {
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
        totalIssueCount: 2,
        blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
        activeIssues: [{ id: 'issue-1', number: 1, title: 'Active issue', health: 'active' }],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p2' }),
      ],
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

  it('lists concrete in-flight issues with number, title, and health coloring, and offers navigation', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

    renderPage()

    const list = screen.getByTestId('current-activity-list')
    expect(list.getAttribute('data-active-count')).toBe('1')
    expect(list.getAttribute('data-blocked-count')).toBe('1')

    const entries = screen.getAllByTestId('current-activity-entry')
    expect(entries.length).toBe(2)

    const blocked = entries.find(entry => entry.getAttribute('data-health') === 'blocked')
    const active = entries.find(entry => entry.getAttribute('data-health') === 'active')
    expect(blocked).toBeTruthy()
    expect(active).toBeTruthy()

    expect(blocked?.textContent).toContain('#2')
    expect(blocked?.textContent).toContain('Blocked issue')
    expect(blocked?.getAttribute('href')).toContain('/issues/2')

    expect(active?.textContent).toContain('#1')
    expect(active?.textContent).toContain('Active issue')
    expect(active?.getAttribute('href')).toContain('/issues/1')

    expect(screen.queryByText(/0 blocked, 0 active/i)).toBeNull()
  })

  it('reflects real activity instead of a constant zero for both active and blocked counts', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 0,
          totalIssueCount: 3,
          blockedIssues: [
            { id: 'issue-3', number: 3, title: 'Stuck issue', health: 'blocked' },
          ],
          activeIssues: [
            { id: 'issue-1', number: 1, title: 'Active issue', health: 'active' },
            { id: 'issue-2', number: 2, title: 'Another active issue', health: 'active' },
          ],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      }),
      isLoading: false,
    })

    renderPage()

    const list = screen.getByTestId('current-activity-list')
    expect(list.getAttribute('data-active-count')).toBe('2')
    expect(list.getAttribute('data-blocked-count')).toBe('1')

    const entries = screen.getAllByTestId('current-activity-entry')
    expect(entries.length).toBe(3)

    expect(screen.queryByText(/0 blocked, 0 active/i)).toBeNull()
  })

  it('shows an empty-state message when no active or blocked issues are in flight', () => {
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

    const empty = screen.getByTestId('current-activity-empty')
    expect(empty.textContent).toMatch(/no current activity/i)
    expect(screen.queryByTestId('current-activity-list')).toBeNull()
    expect(screen.queryByTestId('current-activity-entry')).toBeNull()
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

  it('renders no lifecycle action when the epic is done', () => {
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

    expect(screen.queryByTestId('start-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('pause-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('resume-epic-trigger')).toBeNull()
    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
  })

  it('renders no lifecycle action when the epic is closed', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Closed }), isLoading: false })

    renderPage()

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

describe('EpicDetailPage next issue reason display', () => {
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
        totalIssueCount: 1,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: 'Waiting on #5',
        readyToMarkDone: false,
      },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Pending issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
      ],
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

  it('shows the advancement-state copy (waiting-for-in-progress with nav link) when nextIssue is null', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

    renderPage()

    const copy = screen.getByTestId('advancement-copy')
    expect(copy.textContent).toContain('Waiting for #1 to finish')
    const link = screen.getByTestId('advancement-link')
    expect(link.getAttribute('href')).toContain('/issues/1')
    expect(screen.queryByTestId('mark-epic-done')).toBeTruthy()
  })

  it('shows the next issue link without a Start button when a next issue exists', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 0,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: { id: 'issue-3', number: 3, title: 'Candidate issue' },
          nextIssueReason: null,
          readyToMarkDone: false,
        },
        linkedIssues: [linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' })],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByRole('link', { name: '#3 Candidate issue' }).getAttribute('href')).toContain('/issues/3')
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    expect(startMutate).not.toHaveBeenCalled()
  })

  it('does not show a Next Issue Start action for reason ready or empty states', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })
    renderPage()
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    cleanup()

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
      }),
      isLoading: false,
    })
    renderPage()
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    cleanup()

    mocks.useEpic.mockReturnValue({
      data: makeEpic({
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
      isLoading: false,
    })
    renderPage()
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
  })
})

describe('EpicDetailPage LinkedIssueRow inline Start', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: 'active',
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
  })

  afterEach(() => {
    cleanup()
  })

  it('renders a Start button on a startable backlog row while keeping Remove and navigation', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const startButton = screen.getByTestId('linked-issue-start')
    expect(startButton).toBeTruthy()
    expect(startButton.textContent).toBe('Start')
    expect(startButton).not.toBeDisabled()

    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
    const navLink = screen.getByTestId('linked-issue-nav-link')
    expect(navLink).toBeTruthy()
    expect(navLink.getAttribute('href')).toContain('/issues/3')
  })

  it('hides the Start button on an in_progress linked issue row', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', status: 'in_progress' as LinkedIssue['status'], stage: 'build' as LinkedIssue['stage'], priority: 'p1', health: 'blocked' as LinkedIssue['health'], canStart: false }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button on a blocked linked issue row even when canStart is true', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', priority: 'p1', health: 'blocked' as LinkedIssue['health'], startBlocker: { kind: 'waiting-for', issue: { number: 1, title: 'Done issue' } } }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button on a done linked issue row', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: 'done' as LinkedIssue['status'], stage: 'done' as LinkedIssue['stage'], health: 'done' as LinkedIssue['health'], canStart: false }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button on a cancelled linked issue row', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-4', number: 4, title: 'Cancelled issue', status: 'cancelled' as LinkedIssue['status'], stage: 'done' as LinkedIssue['stage'], health: 'cancelled' as LinkedIssue['health'], canStart: false }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button when canStart is false even with backlog status', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Draft issue', canStart: false, startBlocker: { kind: 'draft' } }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
  })

  it('invokes the start mutation with the issue number when Start is clicked', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-start'))

    expect(startMutate).toHaveBeenCalledWith(3, expect.objectContaining({ onSettled: expect.any(Function) }))
    expect(startMutate).toHaveBeenCalledTimes(1)
  })

  it('disables the Start button for the clicked issue while the start mutation is pending', () => {
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const startButton = screen.getByTestId('linked-issue-start')
    fireEvent.click(startButton)

    expect(startButton).toBeDisabled()
    expect(startButton.textContent).toBe('Starting...')
  })

  it('hides the Start button on all backlog issues when any sibling is in_progress', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Running', status: 'in_progress' as LinkedIssue['status'], stage: 'build' as LinkedIssue['stage'], health: 'active' as LinkedIssue['health'] }),
          linkedIssue({ id: 'issue-2', number: 2, title: 'Next candidate' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryAllByTestId('linked-issue-start')).toHaveLength(0)
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(2)
  })
})

describe('EpicDetailPage LinkedIssueRow vertical task line layout (T-001)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: 'active',
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
  })

  afterEach(() => {
    cleanup()
  })

  function getRow(): HTMLElement {
    return screen.getByTestId('linked-issue-row')
  }

  it('uses a vertical flex-col container instead of the old horizontal two-column layout', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const row = getRow()
    expect(row.classList.contains('flex')).toBe(true)
    expect(row.classList.contains('flex-col')).toBe(true)
    expect(row.classList.contains('justify-between')).toBe(false)
    expect(row.classList.contains('items-center')).toBe(false)
  })

  it('keeps reading-row (#number + title) at the top, then metadata row, then blocker reason, then actions row', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({
            id: 'issue-7',
            number: 7,
            title: 'Blocked item',
            status: IssueStatus.Backlog,
            canStart: false,
            startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } },
            health: IssueHealth.Active,
          }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const row = getRow()
    const readingRow = screen.getByTestId('linked-issue-reading-row')
    const metadataRow = screen.getByTestId('linked-issue-metadata-row')
    const blockerReason = screen.getByTestId('linked-issue-blocker-reason')
    const actionsRow = screen.getByTestId('linked-issue-actions-row')

    expect(row.children[0]).toBe(readingRow)
    expect(row.children[1]).toBe(metadataRow)
    expect(row.children[2]).toBe(blockerReason)
    expect(row.children[3]).toBe(actionsRow)
  })

  it('uses break-words + [overflow-wrap:anywhere] on the title instead of truncate', () => {
    const LONG_TITLE =
      'LinkedIssueRowLongEnglishTitleWithAnUnbrokenTokenThatMustWrapInsideTheRowAtThreeHundredTwentyPixels'

    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: LONG_TITLE }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const title = screen.getByTestId('linked-issue-title')
    expect(title.textContent).toBe(LONG_TITLE)
    expect(title.classList.contains('break-words')).toBe(true)
    expect(title.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
    expect(title.classList.contains('truncate')).toBe(false)
  })

  it('uses flex-wrap on the metadata row so health/status/priority badges wrap at narrow widths', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const metadataRow = screen.getByTestId('linked-issue-metadata-row')
    expect(metadataRow.classList.contains('flex')).toBe(true)
    expect(metadataRow.classList.contains('flex-wrap')).toBe(true)
  })

  it('places the number link and the title on the same primary reading row', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const readingRow = screen.getByTestId('linked-issue-reading-row')
    const navLink = screen.getByTestId('linked-issue-nav-link')
    const title = screen.getByTestId('linked-issue-title')
    expect(readingRow.contains(navLink)).toBe(true)
    expect(readingRow.contains(title)).toBe(true)
  })

  it('does NOT show the blocker reason when the issue is inline-startable', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-blocker-reason')).toBeNull()
  })

  it('shows the "Still a draft" blocker reason when the issue has a draft blocker', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({
            id: 'issue-3',
            number: 3,
            title: 'Draft candidate',
            canStart: false,
            startBlocker: { kind: 'draft' },
          }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Still a draft')
  })

  it('shows the "Waiting for #N" blocker reason when the issue has a waiting-for blocker', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({
            id: 'issue-3',
            number: 3,
            title: 'Waiting on upstream',
            canStart: false,
            startBlocker: { kind: 'waiting-for', issue: { number: 42, title: 'Upstream' } },
          }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Waiting for #42')
  })

  it('shows the "Blocked" reason when health is blocked but no blocker is set', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({
            id: 'issue-3',
            number: 3,
            title: 'Blocked by upstream issue',
            canStart: false,
            health: IssueHealth.Blocked,
          }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Blocked')
  })

  it('shows the "Another issue is in progress" reason only on rows blocked by a running sibling', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-1', number: 1, title: 'Running', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Active }),
          linkedIssue({ id: 'issue-2', number: 2, title: 'Next candidate' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const rows = screen.getAllByTestId('linked-issue-row')
    expect(rows[0].textContent).not.toContain('Another issue is in progress')
    expect(rows[1].textContent).toContain('Another issue is in progress')
  })

  it('shows the "Not startable" fallback reason when the issue is not startable for an unrecognized reason', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({
            id: 'issue-3',
            number: 3,
            title: 'Done-ish issue',
            status: IssueStatus.Done,
            stage: WorkflowStage.Done,
            health: IssueHealth.Done,
            canStart: false,
          }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Not startable')
  })

  it('keeps Start button gated by canInlineStartRow: present only when inline-startable', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Startable candidate' }),
          linkedIssue({
            id: 'issue-4',
            number: 4,
            title: 'Non-startable',
            canStart: false,
            startBlocker: { kind: 'draft' },
          }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.getAllByTestId('linked-issue-start')).toHaveLength(1)
    const rows = screen.getAllByTestId('linked-issue-row')
    expect(rows).toHaveLength(2)
    expect(screen.getAllByTestId('linked-issue-blocker-reason')).toHaveLength(1)
  })
})

describe('EpicDetailPage LinkedIssueRow Remove confirmation flow (T-002)', () => {
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
      status: 'active',
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

  it('places the Remove button in the actions row, not the primary reading row', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const readingRow = screen.getByTestId('linked-issue-reading-row')
    const actionsRow = screen.getByTestId('linked-issue-actions-row')
    const removeButton = screen.getByTestId('linked-issue-remove')

    expect(readingRow.contains(removeButton)).toBe(false)
    expect(actionsRow.contains(removeButton)).toBe(true)
  })

  it('renders the Remove button with the ghost variant for a secondary de-emphasized affordance', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const removeButton = screen.getByTestId('linked-issue-remove')
    expect(removeButton.className).toContain('hover:bg-muted')
    expect(removeButton.className).not.toContain('border-border')
  })

  it('a single click on Remove does NOT call removeEpicIssue.mutate', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    expect(removeMutate).not.toHaveBeenCalled()
  })

  it('clicking Remove opens a confirmation Dialog that shows the issue number and an explanation', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
          linkedIssue({ id: 'issue-7', number: 7, title: 'Other issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const removeButtons = screen.getAllByTestId('linked-issue-remove')
    fireEvent.click(removeButtons[0])

    const dialog = screen.getByTestId('linked-issue-remove-confirm-dialog')
    expect(dialog).toBeTruthy()
    expect(dialog.textContent).toMatch(/remove #3 from this epic\?/i)
    expect(dialog.textContent).toMatch(/workflow state/i)
  })

  it('does not render the remove confirm dialog in the DOM before Remove is clicked', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
    expect(screen.queryByTestId('linked-issue-remove-confirm')).toBeNull()
    expect(screen.queryByTestId('linked-issue-remove-cancel')).toBeNull()
  })

  it('clicking Cancel keeps the link intact and closes the dialog without calling mutate', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-remove'))
    fireEvent.click(screen.getByTestId('linked-issue-remove-cancel'))

    expect(removeMutate).not.toHaveBeenCalled()
    expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
  })

  it('clicking Confirm (destructive) calls removeEpicIssue.mutate with the correct epicId and issueId', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-remove'))
    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))

    expect(removeMutate).toHaveBeenCalledTimes(1)
    expect(removeMutate).toHaveBeenCalledWith({ epicId: 'epic-12345678', issueId: 'issue-3' })
  })

  it('the Confirm button uses the destructive variant', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    const confirm = screen.getByTestId('linked-issue-remove-confirm')
    expect(confirm.className).toContain('text-destructive')
  })

  it('the Cancel button uses the outline variant', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    const cancel = screen.getByTestId('linked-issue-remove-cancel')
    expect(cancel.className).toContain('border-border')
  })

  it('Cancel and Confirm are not disabled while removeEpicIssue is not pending', () => {
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    const confirm = screen.getByTestId('linked-issue-remove-confirm')
    const cancel = screen.getByTestId('linked-issue-remove-cancel')
    expect(confirm).not.toBeDisabled()
    expect(cancel).not.toBeDisabled()
  })

  it('gates the Remove affordance on removeEpicIssue.isPending so the dialog cannot be opened mid-mutation', () => {
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: true, isError: false })
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const removeButton = screen.getByTestId('linked-issue-remove')
    expect(removeButton).toBeDisabled()

    fireEvent.click(removeButton)
    expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
    expect(removeMutate).not.toHaveBeenCalled()
  })

  it('each row owns its own remove-confirm open state — clicking one row does not open another row dialog', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'First issue' }),
          linkedIssue({ id: 'issue-7', number: 7, title: 'Second issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const removeButtons = screen.getAllByTestId('linked-issue-remove')
    fireEvent.click(removeButtons[0])

    expect(screen.getByTestId('linked-issue-remove-confirm-dialog')).toBeTruthy()
    expect(screen.getAllByTestId('linked-issue-remove-confirm')).toHaveLength(1)
    expect(screen.getAllByTestId('linked-issue-remove-cancel')).toHaveLength(1)
  })
})

describe('EpicDetailPage linked issues view toggle', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()

  const makeLinkedIssue = linkedIssue

  function makeEpicWithLinkedIssues(linkedIssues: unknown[]) {
    return {
      id: 'epic-12345678',
      number: 7,
      title: 'Graph epic',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: linkedIssues.length,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues,
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
  })

  afterEach(() => {
    cleanup()
  })

  it('defaults to the list view and shows the toggle when the epic has 2+ linked issues', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        makeLinkedIssue({ id: 'issue-1', number: 1 }),
        makeLinkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    const toggle = screen.getByTestId('linked-issues-view-toggle')
    expect(toggle).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
  })

  it('switches to the graph view when the Graph tab is clicked and does not mutate data', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        makeLinkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        makeLinkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'true')
      expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()
    })

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-region')).toBeInTheDocument()
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()

    expect(addMutate).not.toHaveBeenCalled()
    expect(removeMutate).not.toHaveBeenCalled()
    expect(updateMutate).not.toHaveBeenCalled()
  })

  it('returns to the list view when the List tab is clicked after the graph is shown', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        makeLinkedIssue({ id: 'issue-1', number: 1 }),
        makeLinkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('linked-issues-view-list'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
  })

  it('keeps the Graph tab reachable with an empty-data explanation when the epic has zero linked issues', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([]),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-toggle')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const graphRegion = screen.getByTestId('linked-issues-graph-region')
    expect(graphRegion).toHaveAttribute('data-renderability', 'empty')
    const banner = screen.getByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toHaveAttribute('data-reason', 'empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
    expect(screen.getByTestId('linked-issues-list-region')).toHaveAttribute('data-fallback-for', 'empty')
    expect(screen.getByText('No linked issues yet.')).toBeInTheDocument()
  })

  it('keeps the Graph tab reachable with an empty-data explanation when the epic has exactly one linked issue', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        makeLinkedIssue({ id: 'issue-1', number: 1, title: 'Lone issue' }),
      ]),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-toggle')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const graphRegion = screen.getByTestId('linked-issues-graph-region')
    expect(graphRegion).toHaveAttribute('data-renderability', 'empty')
    const banner = screen.getByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toHaveAttribute('data-reason', 'empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
    expect(screen.getByTestId('linked-issues-list-region')).toHaveAttribute('data-fallback-for', 'empty')
    expect(screen.getByText('Lone issue')).toBeInTheDocument()
    expect(screen.getByTestId('linked-issue-row')).toBeInTheDocument()
  })

  it('falls back to the list view when the graph reports a cycle', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        makeLinkedIssue({ id: 'issue-1', number: 1, prerequisiteNumbers: [2] }),
        makeLinkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      const region = screen.queryByTestId('linked-issues-graph-region')
      if (region) {
        expect(region.getAttribute('data-renderability')).toBe('cyclic')
      }
      expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    })

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(banner.textContent).toMatch(/cycle/i)
    expect(banner.textContent).toMatch(/use the list below/i)

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
  })

  it('keeps the Linked Issues list and add-issue selector fully functional when the graph is the default tab', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        makeLinkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
        makeLinkedIssue({ id: 'issue-2', number: 2, title: 'L-2' }),
      ]),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.getByTestId('epic-issue-selector-trigger')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(2)

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0])
    expect(removeMutate).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))
    expect(removeMutate).toHaveBeenCalledWith({ epicId: 'epic-12345678', issueId: 'issue-1' })
  })
})

describe('EpicDetailPage Graph mobile degradation (T-003)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  function makeEpicWithLinkedIssues(linkedIssues: unknown[]) {
    return {
      id: 'epic-12345678',
      number: 7,
      title: 'Graph epic',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: linkedIssues.length,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues,
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

  it('defaults to the List view when 2+ linked issues are present (List is always the initial state)', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
  })

  it('keeps both List and Graph tabs visible and clickable when graphAvailable is true (data-testids unchanged)', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    const listTab = screen.getByTestId('linked-issues-view-list')
    const graphTab = screen.getByTestId('linked-issues-view-graph')

    expect(listTab).toBeInTheDocument()
    expect(graphTab).toBeInTheDocument()
    expect(listTab).toBeInstanceOf(HTMLButtonElement)
    expect(graphTab).toBeInstanceOf(HTMLButtonElement)
    expect((listTab as HTMLButtonElement).disabled).toBe(false)
    expect((graphTab as HTMLButtonElement).disabled).toBe(false)

    fireEvent.click(graphTab)
    fireEvent.click(listTab)
    expect(addMutate).not.toHaveBeenCalled()
    expect(removeMutate).not.toHaveBeenCalled()
    expect(updateMutate).not.toHaveBeenCalled()
  })

  it('wraps the graph canvas in an overflow-x-auto container with md:overflow-visible (no scrollbar on desktop)', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })

    const canvas = screen.getByTestId('epic-dep-graph-canvas')
    const scrollContainer = canvas.parentElement
    expect(scrollContainer).toBeTruthy()
    expect(scrollContainer!.getAttribute('data-testid')).toBe('linked-issues-graph-scroll-container')
    expect(scrollContainer!.classList.contains('overflow-x-auto')).toBe(true)
    expect(scrollContainer!.classList.contains('md:overflow-visible')).toBe(true)
  })

  it('gives the inner graph canvas a min-width class so it horizontally scrolls on narrow viewports', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })

    const canvas = screen.getByTestId('epic-dep-graph-canvas')
    const classes = Array.from(canvas.classList)
    const hasMinWidth = classes.some(cls => /^min-w-\[/.test(cls))
    expect(hasMinWidth).toBe(true)
  })

  it('renders a narrow-screen hint with md:hidden above the graph canvas', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-region')).toBeInTheDocument()
    })

    const hint = screen.getByTestId('linked-issues-graph-narrow-hint')
    expect(hint).toBeInTheDocument()
    expect(hint.classList.contains('md:hidden')).toBe(true)
    expect(hint.textContent).toMatch(/Graph works best on wider screens/i)
    expect(hint.textContent).toMatch(/swipe/i)
  })

  it('places the narrow-screen hint above the scroll container in DOM order', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-scroll-container')).toBeInTheDocument()
    })

    const region = screen.getByTestId('linked-issues-graph-region')
    const hint = screen.getByTestId('linked-issues-graph-narrow-hint')
    const scrollContainer = screen.getByTestId('linked-issues-graph-scroll-container')

    const hintIndex = Array.from(region.children).indexOf(hint)
    const scrollIndex = Array.from(region.children).indexOf(scrollContainer)
    expect(hintIndex).toBeGreaterThanOrEqual(0)
    expect(scrollIndex).toBeGreaterThanOrEqual(0)
    expect(hintIndex).toBeLessThan(scrollIndex)
  })

  it('keeps the narrow-screen hint hidden when the list view is the default (only renders inside the graph region)', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-narrow-hint')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('switches between List and Graph tabs without error and keeps the overflow-x-auto wrapper on every Graph render', async () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'Root' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Dep', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-scroll-container')).toBeInTheDocument()
    })
    expect(screen.getByTestId('linked-issues-graph-narrow-hint')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('linked-issues-view-list'))
    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-list-region')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('linked-issues-graph-scroll-container')).toBeInTheDocument()
    })
    const canvasAfterReturn = screen.getByTestId('epic-dep-graph-canvas')
    const containerAfterReturn = canvasAfterReturn.parentElement
    expect(containerAfterReturn!.classList.contains('overflow-x-auto')).toBe(true)
    expect(containerAfterReturn!.classList.contains('md:overflow-visible')).toBe(true)
  })
})

describe('EpicDetailPage Graph unrenderable banner + Error Boundary (T-004)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  function makeEpicWithLinkedIssues(linkedIssues: unknown[]) {
    return {
      id: 'epic-12345678',
      number: 7,
      title: 'Graph epic',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: linkedIssues.length,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues,
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    widgetBehavior.mode = 'default'
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
    widgetBehavior.mode = 'default'
  })

  it('renders the cyclic banner explaining the dependency cycle when the graph reports cyclic', async () => {
    widgetBehavior.mode = 'default'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, prerequisiteNumbers: [2] }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(banner.textContent).toMatch(/cycle/i)
    expect(banner.textContent).toMatch(/use the list below/i)
  })

  it('renders the empty banner explaining there is not enough data when the graph reports empty', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('empty')
    expect(banner.textContent).toMatch(/not enough/i)
    expect(banner.textContent).toMatch(/use the list below/i)
  })

  it('renders the fallback "Graph is unavailable" banner when the Error Boundary catches a render exception', async () => {
    widgetBehavior.mode = 'error'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner).toBeInTheDocument()
    expect(banner.getAttribute('data-reason')).toBe('error')
    expect(banner.textContent).toMatch(/graph is unavailable/i)
    expect(banner.textContent).toMatch(/use the list below/i)

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('keeps the List view rendered as fallback for the empty unrenderable scenario', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('empty')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
  })

  it('keeps the List view rendered as fallback when the Error Boundary catches a render exception', async () => {
    widgetBehavior.mode = 'error'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('error')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
  })

  it('keeps the List view rendered as fallback for the cyclic unrenderable scenario (existing behavior)', async () => {
    widgetBehavior.mode = 'default'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1, title: 'L-1', prerequisiteNumbers: [2] }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const listRegion = screen.getByTestId('linked-issues-list-region')
    expect(listRegion).toBeInTheDocument()
    expect(listRegion.getAttribute('data-fallback-for')).toBe('cyclic')

    const listRows = screen.getAllByTestId('linked-issue-row')
    expect(listRows).toHaveLength(2)
  })

  it('does not show the graph canvas or scroll container when the banner is displayed (any unrenderable state)', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()
    expect(screen.queryByTestId('linked-issues-graph-scroll-container')).toBeNull()
  })

  it('shows the narrow-screen hint above the unavailability banner in DOM order', async () => {
    widgetBehavior.mode = 'empty'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

    await screen.findByTestId('linked-issues-graph-unavailable-banner')

    const region = screen.getByTestId('linked-issues-graph-region')
    const hint = screen.getByTestId('linked-issues-graph-narrow-hint')
    const banner = screen.getByTestId('linked-issues-graph-unavailable-banner')

    const children = Array.from(region.children)
    const hintIndex = children.indexOf(hint)
    const bannerIndex = children.indexOf(banner)
    expect(hintIndex).toBeGreaterThanOrEqual(0)
    expect(bannerIndex).toBeGreaterThanOrEqual(0)
    expect(hintIndex).toBeLessThan(bannerIndex)
  })

  it('clears the error fallback when the user switches back to the List tab and re-selects Graph with a healthy widget', async () => {
    widgetBehavior.mode = 'error'
    mocks.useEpic.mockReturnValue({
      data: makeEpicWithLinkedIssues([
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ]),
      isLoading: false,
    })

    renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(screen.getByTestId('linked-issues-graph-unavailable-banner').getAttribute('data-reason')).toBe('error')

    fireEvent.click(screen.getByTestId('linked-issues-view-list'))
    await waitFor(() => {
      expect(screen.queryByTestId('linked-issues-graph-region')).toBeNull()
    })

    widgetBehavior.mode = 'default'
    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-unavailable-banner')).toBeNull()
  })

  it('re-probes graph renderability when linked issue data changes while Graph remains selected', async () => {
    let currentEpic = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'L-1', prerequisiteNumbers: [2] }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])
    mocks.useEpic.mockImplementation(() => ({ data: currentEpic, isLoading: false }))

    const { rerenderPage } = renderPage()

    fireEvent.click(screen.getByTestId('linked-issues-view-graph'))
    const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
    expect(banner.getAttribute('data-reason')).toBe('cyclic')
    expect(screen.queryByTestId('epic-dep-graph-canvas')).toBeNull()

    currentEpic = makeEpicWithLinkedIssues([
      linkedIssue({ id: 'issue-1', number: 1, title: 'L-1' }),
      linkedIssue({ id: 'issue-2', number: 2, title: 'L-2', prerequisiteNumbers: [1] }),
    ])
    rerenderPage()

    await waitFor(() => {
      expect(screen.getByTestId('epic-dep-graph-canvas')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('linked-issues-graph-unavailable-banner')).toBeNull()
    expect(screen.queryByTestId('linked-issues-list-region')).toBeNull()
  })

  it('every unrenderable banner message directs the user to the list below (spec contract)', async () => {
    const scenarios = [
      { reason: 'cyclic' as const, expectedKeyword: /cycle/i, linkedIssues: () => [
        linkedIssue({ id: 'issue-1', number: 1, prerequisiteNumbers: [2] }),
        linkedIssue({ id: 'issue-2', number: 2, prerequisiteNumbers: [1] }),
      ] },
      { reason: 'empty' as const, expectedKeyword: /not enough/i, linkedIssues: () => [
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2 }),
      ] },
      { reason: 'error' as const, expectedKeyword: /graph is unavailable/i, linkedIssues: () => [
        linkedIssue({ id: 'issue-1', number: 1 }),
        linkedIssue({ id: 'issue-2', number: 2 }),
      ] },
    ]

    for (const scenario of scenarios) {
      widgetBehavior.mode = scenario.reason === 'cyclic' ? 'default' : scenario.reason
      mocks.useEpic.mockReturnValue({
        data: makeEpicWithLinkedIssues(scenario.linkedIssues()),
        isLoading: false,
      })

      const { unmount } = renderPage()
      fireEvent.click(screen.getByTestId('linked-issues-view-graph'))

      const banner = await screen.findByTestId('linked-issues-graph-unavailable-banner')
      expect(banner.textContent).toMatch(scenario.expectedKeyword)
      expect(banner.textContent).toMatch(/use the list below/i)
      unmount()
      widgetBehavior.mode = 'default'
    }
  })
})

function getMobileHeaderContainer(): HTMLElement {
  const epicNumber = screen.getByTestId('epic-number')
  const container = epicNumber.closest('.flex.flex-col.gap-4')
  if (!container) throw new Error('Epic detail mobile header container not found')
  return container as HTMLElement
}

function getEpicDetailPageContainer(): HTMLElement {
  const epicNumber = screen.getByTestId('epic-number')
  const container = epicNumber.closest('.mx-auto')
  if (!container) throw new Error('Epic detail page container not found')
  return container as HTMLElement
}

function getTitleBlock(): HTMLElement {
  const epicNumber = screen.getByTestId('epic-number')
  const titleBlock = epicNumber.closest('.min-w-0.flex-1')
  if (!titleBlock) throw new Error('Epic title block not found')
  return titleBlock as HTMLElement
}

function getActionGroup(): HTMLElement {
  const editButton = screen.getByTestId('edit-epic-button')
  const actionGroup = editButton.parentElement
  if (!actionGroup) throw new Error('Epic action group not found')
  return actionGroup as HTMLElement
}

describe('EpicDetailPage mobile layout structural contract', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  const LONG_CHINESE_TITLE =
    '史诗详情页移动端布局修复：消除横向溢出与标题压缩，让标题和描述在窄屏下独占可读宽度，操作按钮按主次分级可见'
  const LONG_ENGLISH_TITLE =
    'EpicDetailPageMobileHeaderTitleWithAnUnbrokenEnglishTokenThatMustWrapInsideTheReadableColumnAtThreeHundredTwentyPixels'
  const LONG_ENGLISH_DESCRIPTION =
    'EpicDetailPageMobileHeaderDescriptionWithAnUnbrokenEnglishTokenThatMustWrapInsideTheDescriptionColumnAtThreeHundredTwentyPixels'

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: 7,
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

  it('uses a flex-col mobile layout and md:flex-row desktop layout in the header container', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const header = getMobileHeaderContainer()
    expect(header.classList.contains('flex')).toBe(true)
    expect(header.classList.contains('flex-col')).toBe(true)
    expect(header.classList.contains('gap-4')).toBe(true)
    expect(header.classList.contains('md:flex-row')).toBe(true)
    expect(header.classList.contains('md:flex-wrap')).toBe(true)
    expect(header.classList.contains('md:items-start')).toBe(true)
    expect(header.classList.contains('md:justify-between')).toBe(true)
  })

  it('lets the page wrapper shrink inside the app shell at mobile widths', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const container = getEpicDetailPageContainer()
    expect(container.classList.contains('w-full')).toBe(true)
    expect(container.classList.contains('min-w-0')).toBe(true)
    expect(container.classList.contains('max-w-4xl')).toBe(true)
  })

  it('places the title block before the action button group in DOM order on a running epic so it stacks above on mobile', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running, title: LONG_CHINESE_TITLE }),
      isLoading: false,
    })

    renderPage()

    const header = getMobileHeaderContainer()
    const titleBlock = getTitleBlock()
    const actionGroup = getActionGroup()

    const titleIndex = Array.from(header.children).indexOf(titleBlock)
    const actionIndex = Array.from(header.children).indexOf(actionGroup)
    expect(titleIndex).toBeGreaterThanOrEqual(0)
    expect(actionIndex).toBeGreaterThanOrEqual(0)
    expect(titleIndex).toBeLessThan(actionIndex)
  })

  it('places the title block before the action button group in DOM order on an idle epic', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Idle, title: LONG_ENGLISH_TITLE }),
      isLoading: false,
    })

    renderPage()

    const header = getMobileHeaderContainer()
    const titleBlock = getTitleBlock()
    const actionGroup = getActionGroup()

    const titleIndex = Array.from(header.children).indexOf(titleBlock)
    const actionIndex = Array.from(header.children).indexOf(actionGroup)
    expect(titleIndex).toBeLessThan(actionIndex)
  })

  it('keeps the title block class contract (min-w-0 + flex-1) so it can shrink/wrap on mobile', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running, title: LONG_CHINESE_TITLE }),
      isLoading: false,
    })

    renderPage()

    const titleBlock = getTitleBlock()
    expect(titleBlock.classList.contains('min-w-0')).toBe(true)
    expect(titleBlock.classList.contains('flex-1')).toBe(true)
  })

  it('adds an explicit break rule to an unbroken English title so it cannot force horizontal overflow', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Idle, title: LONG_ENGLISH_TITLE }),
      isLoading: false,
    })

    renderPage()

    const heading = screen.getByRole('heading', { name: LONG_ENGLISH_TITLE })
    expect(heading.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
  })

  it('adds an explicit break rule to plain description content with an unbroken English token', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running, description: LONG_ENGLISH_DESCRIPTION }),
      isLoading: false,
    })

    renderPage()

    const description = screen.getByTestId('epic-description')
    expect(description.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
    expect(description).toHaveTextContent(LONG_ENGLISH_DESCRIPTION)
  })

  it('uses flex-wrap on the action button group so secondary actions stay reachable on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()
    expect(actionGroup.classList.contains('flex')).toBe(true)
    expect(actionGroup.classList.contains('flex-wrap')).toBe(true)
    expect(actionGroup.classList.contains('justify-start')).toBe(true)
    expect(actionGroup.classList.contains('md:justify-end')).toBe(true)
  })

  it('renders the running lifecycle action (Pause) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })

  it('renders the idle lifecycle action (Start Epic) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })

  it('renders the paused lifecycle action (Resume) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Paused }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
  })

  it('omits Start/Pause/Resume lifecycle actions for a done epic on mobile', () => {
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

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeNull()
  })

  it('omits Start/Pause/Resume lifecycle actions for a closed epic on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Closed }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeNull()
  })

  it('uses flex-wrap on the LinkedIssueRow action container so Start/Remove can wrap at 320px', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Running,
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const linkedStartButton = screen.getByTestId('linked-issue-start')
    const actionContainer = linkedStartButton.parentElement as HTMLElement
    expect(actionContainer).toBeTruthy()
    expect(actionContainer.getAttribute('data-testid')).toBe('linked-issue-actions-row')
    expect(actionContainer.classList.contains('flex')).toBe(true)
    expect(actionContainer.classList.contains('flex-wrap')).toBe(true)
    expect(actionContainer.classList.contains('gap-2')).toBe(true)
  })

  it('keeps the desktop flex-row + justify-between classes on the header container for >=md layout', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const header = getMobileHeaderContainer()
    expect(header.classList.contains('md:flex-row')).toBe(true)
    expect(header.classList.contains('md:justify-between')).toBe(true)
    expect(header.classList.contains('md:items-start')).toBe(true)
    expect(header.classList.contains('md:flex-wrap')).toBe(true)
  })
})

describe('EpicDetailPage summary-first information architecture (T-002)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  const LONG_DESCRIPTION = [
    '## Background',
    '',
    'This is the long descriptive prose that previously appeared in the header card before the summary grid.',
    '',
    'It pushed the status facts below the first fold on narrow viewports.',
    '',
    Array.from({ length: 12 }, (_, i) => `Paragraph ${i + 1} with additional context and details.`).join('\n\n'),
  ].join('\n\n')

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: 26,
      title: 'Epic title',
      description: LONG_DESCRIPTION,
      priority: 'p1',
      status: EpicStatus.Running,
      pauseReason: null,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 1,
        totalIssueCount: 3,
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

  function getSummaryGrid(): HTMLElement {
    const summary = screen.getByTestId('summary-grid')
    return summary
  }

  function getOverviewCard(): HTMLElement {
    return screen.getByTestId('overview-card')
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

  describe('summary-before-description DOM order', () => {
    it('renders the summary grid before the Overview card on desktop', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const summary = getSummaryGrid()
      const overview = getOverviewCard()
      expect(summary.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('places the summary grid before the Overview card in DOM order on mobile (390px viewport)', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const summary = getSummaryGrid()
      const overview = getOverviewCard()
      expect(summary.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('keeps the summary grid inside the header card while the Overview card sits below it', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const summary = getSummaryGrid()
      const headerCard = summary.closest('[data-slot="card"]') as HTMLElement
      expect(headerCard).toBeTruthy()
      expect(headerCard.querySelector('[data-testid="overview-card"]')).toBeNull()
    })
  })

  describe('no Overview card when description is empty', () => {
    it('omits the Overview card entirely when epic.description is the empty string', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ description: '' }), isLoading: false })

      renderPage()

      expect(screen.queryByTestId('overview-card')).toBeNull()
      expect(screen.queryByTestId('epic-description')).toBeNull()
    })

    it('still renders the summary grid when description is empty', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ description: '' }), isLoading: false })

      renderPage()

      expect(screen.getByTestId('summary-grid')).toBeTruthy()
      expect(screen.getByText('1 / 3')).toBeTruthy()
    })
  })

  describe('Overview/Description region is collapsible via MarkdownReader', () => {
    it('renders the MarkdownReader in collapsible mode inside the Overview card', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const description = screen.getByTestId('epic-description')
      const reader = description.querySelector('[data-testid="markdown-reader"]') as HTMLElement
      expect(reader).toBeTruthy()
      expect(reader.getAttribute('data-mode')).toBe('collapsible')
    })

    it('exposes the expand/collapse test hooks from MarkdownReader inside the Overview card', () => {
      const originalScrollHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight')
      Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
        configurable: true,
        get() {
          return 5000
        },
      })
      try {
        mocks.useEpic.mockReturnValue({
          data: makeEpic({
            description: Array.from({ length: 80 }, (_, i) => `Line ${i + 1} content that exceeds the collapsed height.`).join('\n\n'),
          }),
          isLoading: false,
        })

        renderPage()

        const description = screen.getByTestId('epic-description')
        const expandControl = description.querySelector('[data-testid="markdown-expand-control"]') as HTMLElement
        expect(expandControl).toBeTruthy()
      } finally {
        if (originalScrollHeight) {
          Object.defineProperty(HTMLElement.prototype, 'scrollHeight', originalScrollHeight)
        } else {
          delete (HTMLElement.prototype as unknown as Record<string, unknown>).scrollHeight
        }
      }
    })
  })

  describe('progress summary', () => {
    it('shows delivered / total counts', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ progress: { deliveredCount: 2, totalIssueCount: 5, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false } }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByText('2 / 5')).toBeTruthy()
      expect(screen.queryByTestId('progress-ready-to-mark-done')).toBeNull()
    })

    it('surfaces a ready-to-mark-done indication when readyToMarkDone is true', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ progress: { deliveredCount: 3, totalIssueCount: 3, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: true } }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByText('3 / 3')).toBeTruthy()
      const indicator = screen.getByTestId('progress-ready-to-mark-done')
      expect(indicator).toBeTruthy()
      expect(indicator.textContent).toMatch(/ready to mark done/i)
    })

    it('omits the ready-to-mark-done indicator for terminal epics (done/closed)', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Done,
          progress: { deliveredCount: 1, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: true },
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.queryByTestId('progress-ready-to-mark-done')).toBeNull()
    })
  })

  describe('advancement copy kinds', () => {
    it('renders waiting-for-in-progress copy with nav link to the in-progress issue', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          progress: { deliveredCount: 0, totalIssueCount: 2, blockedIssues: [], activeIssues: [{ id: 'issue-2', number: 2, title: 'Active', health: 'active' }], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Backlog', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: true, startBlocker: null }),
            linkedIssue({ id: 'issue-2', number: 2, title: 'Active', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1', canStart: false }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('Waiting for #2 to finish')
      const link = screen.getByTestId('advancement-link')
      expect(link.getAttribute('href')).toContain('/issues/2')
    })

    it('renders draft-blocker copy with nav link to the draft candidate', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-8', number: 8, title: 'Draft candidate', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'draft' } }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('still a draft')
      expect(copy.textContent).toContain('#8')
      const link = screen.getByTestId('advancement-link')
      expect(link.getAttribute('href')).toContain('/issues/8')
    })

    it('renders external-prerequisite-blocker copy with nav links to the prerequisites', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({
              id: 'issue-9',
              number: 9,
              title: 'Blocked by externals',
              status: IssueStatus.Backlog,
              stage: WorkflowStage.Plan,
              canStart: false,
              startBlocker: null,
              externalPrerequisites: [
                { number: 100, title: 'Upstream A', stage: 'plan', status: 'backlog' },
                { number: 200, title: 'Upstream B', stage: 'plan', status: 'backlog' },
              ],
            }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('external issues')
      expect(copy.textContent).toContain('#100')
      expect(copy.textContent).toContain('#200')
      const links = screen.getAllByTestId('advancement-link')
      expect(links.length).toBe(2)
      const hrefs = links.map(l => l.getAttribute('href')).sort()
      expect(hrefs).toContain('/issues/100')
      expect(hrefs).toContain('/issues/200')
    })

    it('renders running-but-idle copy without nav links for a running epic with no startable next', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Running,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Waiting on', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('Running')
      expect(copy.textContent).not.toContain('Idle')
      expect(screen.queryByTestId('advancement-link')).toBeNull()
    })

    it('renders has-next nav link without additional advancement copy when a server-provided next issue exists', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: { id: 'issue-3', number: 3, title: 'Candidate' }, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate' }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const next = screen.getByTestId('next-issue')
      expect(next.getAttribute('href')).toContain('/issues/3')
      // When nextIssue is present and state is has-next, no extra advancement copy is rendered
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
    })

    it('does not render external-blocker copy below a startable next issue with prerequisite metadata', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: { id: 'issue-3', number: 3, title: 'Candidate' }, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({
              id: 'issue-3',
              number: 3,
              title: 'Candidate',
              status: IssueStatus.Backlog,
              stage: WorkflowStage.Plan,
              canStart: true,
              startBlocker: null,
              externalPrerequisites: [{ number: 77, title: 'Historical prerequisite', stage: 'done', status: 'done' }],
            }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const next = screen.getByTestId('next-issue')
      expect(next).toHaveTextContent('#3 Candidate')
      expect(next.getAttribute('href')).toContain('/issues/3')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
      expect(screen.queryByText(/external issue/i)).toBeNull()
    })

    it('does not show a lower-priority draft blocker under a server-provided next issue', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 2, blockedIssues: [], activeIssues: [], nextIssue: { id: 'issue-9', number: 9, title: 'Priority candidate' }, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-4', number: 4, title: 'Older draft', priority: 'p3', canStart: false, startBlocker: { kind: 'draft' } }),
            linkedIssue({ id: 'issue-9', number: 9, title: 'Priority candidate', priority: 'p0', canStart: true, startBlocker: null }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const next = screen.getByTestId('next-issue')
      expect(next).toHaveTextContent('#9 Priority candidate')
      expect(next.getAttribute('href')).toContain('/issues/9')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
      const advancementArea = screen.getByTestId('next-issue-region')
      expect(advancementArea.textContent ?? '').not.toMatch(/still a draft/i)
    })

    it('renders idle-no-next reason copy when an idle epic has no startable candidate and no specific blocker', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Waiting on', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('Running')
    })

    it('renders "No linked issues yet" when there are no linked issues and no next issue', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 0, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [],
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByText('No linked issues yet')).toBeTruthy()
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
    })

    it('uses neutral copy for an all-cancelled epic instead of delivered wording', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-7', number: 7, title: 'Cancelled issue', status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(screen.getByText('0 / 1')).toBeTruthy()
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('All linked issues are delivered')
    })

    it('uses neutral copy for mixed done and cancelled issues instead of delivered wording', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 1, totalIssueCount: 2, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
            linkedIssue({ id: 'issue-7', number: 7, title: 'Cancelled issue', status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(screen.getByText('1 / 2')).toBeTruthy()
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('All linked issues are delivered')
    })

    it('distinguishes advancement copy kinds without collapsing them into one message', () => {
      // Sanity check: build three epics with different shapes and confirm distinct copy.
      const cases = [
        {
          label: 'running-but-idle',
          epic: makeEpic({
            status: EpicStatus.Running,
            linkedIssues: [linkedIssue({ id: 'i1', number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } })],
          }),
        },
        {
          label: 'draft-blocker',
          epic: makeEpic({
            status: EpicStatus.Idle,
            linkedIssues: [linkedIssue({ id: 'i1', number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } })],
          }),
        },
        {
          label: 'external-prerequisite-blocker',
          epic: makeEpic({
            status: EpicStatus.Idle,
            linkedIssues: [linkedIssue({ id: 'i1', number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: null, externalPrerequisites: [{ number: 99, title: 'X', stage: 'plan', status: 'backlog' }] })],
          }),
        },
      ]
      const seen = new Set<string>()
      for (const c of cases) {
        mocks.useEpic.mockReturnValue({ data: c.epic, isLoading: false })
        renderPage()
        const copy = screen.getByTestId('advancement-copy')
        seen.add(copy.textContent ?? '')
        cleanup()
      }
      expect(seen.size).toBe(3)
    })
  })

  describe('paused epic resume hint', () => {
    it('renders the paused epic pause reason chip in the header', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Paused, pauseReason: 'Waiting for design review' }),
        isLoading: false,
      })

      renderPage()

      const reasonBadge = screen.getByTestId('pause-reason')
      expect(reasonBadge).toHaveTextContent('Waiting for design review')
    })

    it('renders the resume re-evaluation hint inside the Next Issue column when paused', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Paused, pauseReason: 'Waiting for design review' }),
        isLoading: false,
      })

      renderPage()

      const hint = screen.getByTestId('resume-re-evaluation-hint')
      expect(hint.textContent).toMatch(/resuming/i)
      expect(hint.textContent).toMatch(/re-evaluate/i)
    })

    it('does not render the resume hint on a non-paused epic', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

      renderPage()

      expect(screen.queryByTestId('resume-re-evaluation-hint')).toBeNull()
    })
  })

  describe('no regression of linked-issue / edit / add capabilities', () => {
    it('keeps the Linked Issues listing reachable with linked-issue nav links and Remove buttons', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
            linkedIssue({ id: 'issue-2', number: 2, title: 'Backlog', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: true, startBlocker: null }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const list = screen.getByTestId('linked-issues-list-region')
      expect(list).toBeTruthy()
      const navLinks = screen.getAllByTestId('linked-issue-nav-link')
      expect(navLinks.length).toBe(2)
      expect(navLinks[0].getAttribute('href')).toContain('/issues/1')
      expect(navLinks[1].getAttribute('href')).toContain('/issues/2')
      expect(screen.getAllByRole('button', { name: 'Remove' }).length).toBe(2)
    })

    it('keeps the add-issue selector reachable and functional after the summary restructure', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('epic-issue-selector-trigger')).toBeTruthy()
      expect(screen.getByTestId('add-issue-submit')).toBeTruthy()

      fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
      const option = screen.getAllByTestId('epic-issue-option')[0]
      fireEvent.click(option)
      expect(option).toBeTruthy()
    })

    it('keeps the Edit and Close Epic buttons reachable as secondary actions on a non-terminal epic', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

      renderPage()

      const actionGroup = getActionGroup()
      expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
      expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()
    })

    it('keeps the list/graph toggle reachable when there are 2+ linked issues', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'A' }),
            linkedIssue({ id: 'issue-2', number: 2, title: 'B', prerequisiteNumbers: [1] }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('linked-issues-view-toggle')).toBeTruthy()
      expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
      expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
    })
  })
})

describe('EpicDetailPage Ask Agent entry (T-005)', () => {
  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: 7,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: 'active',
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 1,
        totalIssueCount: 2,
        blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
        activeIssues: [],
        nextIssue: { id: 'issue-2', number: 2, title: 'Blocked issue' },
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
    mocks.useAddEpicIssue.mockReturnValue({ mutate: vi.fn(), isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: vi.fn(), isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: vi.fn(), isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: vi.fn(), isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: vi.fn(), isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders an Ask Agent button in the action group', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

    renderPage()

    const button = screen.getByTestId('ask-agent-epic')
    expect(button).toBeTruthy()
    expect(button.textContent).toContain('Ask Agent')
  })

  it('navigates to the composer with ?epic=<id> on click', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ id: 'epic-12345678' }), isLoading: false })

    renderPage()

    const button = screen.getByTestId('ask-agent-epic')
    fireEvent.click(button)

    expect(mockUseNavigate).toHaveBeenCalledWith(
      expect.stringContaining('/agent-sessions/new?epic='),
    )
  })

  it('includes the epic id in the navigation URL', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ id: 'epic-abc-def' }), isLoading: false })

    renderPage()

    const button = screen.getByTestId('ask-agent-epic')
    fireEvent.click(button)

    const callArg = mockUseNavigate.mock.calls[0][0] as string
    expect(callArg).toContain('epic=epic-abc-def')
  })
})
