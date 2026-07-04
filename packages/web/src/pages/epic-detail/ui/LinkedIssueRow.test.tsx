// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen } from '@testing-library/react'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import type { LinkedIssue } from '../../../entities/epic'
import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'

/**
 * Tests for the inline `LinkedIssueRow` rendered inside <EpicDetailPage/>.
 * Covers the inline-Start affordance and the vertical task-line layout contract.
 * The row component itself lives inside EpicDetailPage.tsx (not exported), so
 * every test mounts the full page via renderPage() and scopes assertions to the
 * `linked-issue-*` testids.
 */

// --- per-file hoisted mocks (Vitest hoists vi.mock per-file; cannot be shared) ---
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
    useNavigate: () => vi.fn(),
  }
})

vi.mock('../../../widgets/epic-dependency-graph', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../widgets/epic-dependency-graph')>()
  return { ...actual }
})

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

describe('EpicDetailPage LinkedIssueRow inline Start', () => {
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
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })
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
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })
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
