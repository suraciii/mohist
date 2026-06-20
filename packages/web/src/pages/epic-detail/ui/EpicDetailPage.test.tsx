// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicStatus } from '../../../entities/epic'
import { EpicDetailPage } from './EpicDetailPage'
import { ApiError } from '../../../shared/api/client'

const mocks = vi.hoisted(() => ({
  useProject: vi.fn(),
  useEpic: vi.fn(),
  useIssues: vi.fn(),
  useAddEpicIssue: vi.fn(),
  useRemoveEpicIssue: vi.fn(),
  useMarkEpicDone: vi.fn(),
  useCloseEpic: vi.fn(),
  useUpdateEpic: vi.fn(),
  usePauseEpic: vi.fn(),
  useResumeEpic: vi.fn(),
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
    useMarkEpicDone: mocks.useMarkEpicDone,
    useCloseEpic: mocks.useCloseEpic,
    useUpdateEpic: mocks.useUpdateEpic,
    usePauseEpic: mocks.usePauseEpic,
    useResumeEpic: mocks.useResumeEpic,
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
    { id: 'issue-1', number: 1, title: 'Done issue', status: 'completed', stage: 'done', priority: 'p2' },
    { id: 'issue-2', number: 2, title: 'Blocked issue', status: 'blocked', stage: 'build', priority: 'p1' },
  ],
}

const issues = [
  { id: 'issue-1', number: 1, title: 'Done issue', isDraft: false, canStart: false, blocker: null, status: 'done', health: 'done' },
  { id: 'issue-2', number: 2, title: 'Blocked issue', isDraft: false, canStart: false, blocker: null, status: 'in_progress', health: 'blocked' },
  { id: 'issue-3', number: 3, title: 'Candidate issue', isDraft: false, canStart: true, blocker: null, status: 'backlog', health: 'active' },
]

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
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
    </QueryClientProvider>,
  )
}

describe('EpicDetailPage', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: epic, isLoading: false })
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
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
      linkedIssues: [],
      ...overrides,
    }
  }

  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
          { id: 'issue-2', number: 2, title: 'Active issue', status: 'in_progress', stage: 'build', priority: 'p1' },
          { id: 'issue-3', number: 3, title: 'Backlog issue', status: 'backlog', stage: 'plan', priority: 'p3' },
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).toHaveAttribute('title', '2 linked issues remain unfinished')
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
          { id: 'issue-2', number: 2, title: 'Blocked issue', status: 'blocked', stage: 'build', priority: 'p1' },
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const markDone = screen.getByTestId('mark-epic-done')
    expect(markDone).toBeDisabled()
    expect(markDone).toHaveAttribute('title', '1 linked issue remains unfinished')
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
          { id: 'issue-2', number: 2, title: 'Active issue', status: 'in_progress', stage: 'build', priority: 'p1' },
          { id: 'issue-3', number: 3, title: 'Backlog issue', status: 'backlog', stage: 'plan', priority: 'p3' },
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
        ],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.queryByTestId('mark-epic-done')).toBeNull()
    expect(screen.queryByTestId('close-epic-trigger')).toBeNull()
    expect(screen.getByText('done')).toBeTruthy()
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
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
    { id: 'issue-1', number: 1, title: 'Active issue', status: 'in_progress', stage: 'build', priority: 'p1' },
  ],
}

describe('EpicDetailPage numbered display', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: numberedEpic, isLoading: false })
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
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
    { id: 'issue-1', number: 1, title: 'Done issue', status: 'completed', stage: 'done', priority: 'p2' },
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
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: searchEpic, isLoading: false })
    mocks.useIssues.mockReturnValue({ data: searchIssues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
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
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

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
        { id: 'issue-1', number: 1, title: 'Member issue', status: 'in_progress', stage: 'build', priority: 'p2' },
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
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
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
    expect(screen.getByText('active')).toBeTruthy()

    fireEvent.click(screen.getByTestId('edit-epic-button'))
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Renamed' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(screen.getByText('Member issue')).toBeTruthy()
    expect(screen.getByText('active')).toBeTruthy()
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
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

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
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Active,
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
        { id: 'issue-1', number: 1, title: 'Active issue', status: 'in_progress', stage: 'build', priority: 'p1' },
        { id: 'issue-2', number: 2, title: 'Blocked issue', status: 'in_progress', stage: 'build', priority: 'p2' },
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
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
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
          { id: 'issue-1', number: 1, title: 'Done issue', status: 'done', stage: 'done', priority: 'p2' },
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
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
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
  })

  afterEach(() => {
    cleanup()
  })

  it('shows a Pause button on an active Epic that opens a confirm dialog with a reason input', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Active }),
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
      data: makeEpic({ status: EpicStatus.Active }),
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
      data: makeEpic({ status: EpicStatus.Active }),
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
      data: makeEpic({ status: EpicStatus.Active }),
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
    expect(markDone).toHaveAttribute('title', 'Resume this Epic before marking it done')
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

describe('EpicDetailPage next issue reason display', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Active,
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
        { id: 'issue-1', number: 1, title: 'Pending issue', status: 'in_progress', stage: 'build', priority: 'p1' },
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
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows the human-readable reason in the Next Issue magnet when nextIssue is null', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

    renderPage()

    const reason = screen.getByTestId('next-issue-reason')
    expect(reason.textContent).toBe('Waiting on #5')
    expect(screen.queryByTestId('mark-epic-done')).toBeTruthy()
  })
})