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
import { LocationProbe } from './_epicDetailPageTestHarness'
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

const mocks = vi.hoisted(() => ({
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

vi.mock('../../../widgets/epic-dependency-graph', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/epic-dependency-graph')>()
  const { useEffect: useEffectMock, createElement } = await import('react')
  function hasCycle(issues: { number: number; prerequisiteNumbers: number[] }[]): boolean {
    const adj = new Map<number, number[]>()
    for (const i of issues) adj.set(i.number, i.prerequisiteNumbers ?? [])
    const visited = new Set<number>()
    const stack = new Set<number>()
    function dfs(n: number): boolean {
      if (stack.has(n)) return true
      if (visited.has(n)) return false
      visited.add(n); stack.add(n)
      for (const d of adj.get(n) ?? []) if (dfs(d)) return true
      stack.delete(n)
      return false
    }
    for (const n of adj.keys()) if (dfs(n)) return true
    return false
  }
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
        } else if (mode === 'default') {
          if (hasCycle(props.linkedIssues as { number: number; prerequisiteNumbers: number[] }[])) {
            props.onRenderabilityChange?.({ renderable: false, reason: 'cyclic' })
          } else {
            props.onRenderabilityChange?.({ renderable: true, reason: null })
          }
        }
      }, [mode])
      if (mode === 'error') {
        throw new Error('Simulated render error from DependencyGraphWidget')
      }
      if (mode === 'default') {
        if (hasCycle(props.linkedIssues as { number: number; prerequisiteNumbers: number[] }[])) return null
        return createElement('div', {
          'data-testid': 'epic-dep-graph-canvas',
          className: 'h-[560px] w-full min-w-[640px] rounded-lg border bg-background',
        })
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
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/epic/epic-12345678']}>
          <LocationProbe />
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

    expect(screen.getByTestId('current-path').textContent).toContain('/agent-sessions/new?epic=')
  })

  it('includes the epic id in the navigation URL', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ id: 'epic-abc-def' }), isLoading: false })

    renderPage()

    const button = screen.getByTestId('ask-agent-epic')
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toContain('epic=epic-abc-def')
  })
})
