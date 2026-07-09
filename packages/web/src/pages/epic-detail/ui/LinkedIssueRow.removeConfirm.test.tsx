// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen } from '@testing-library/react'
import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'

/**
 * Tests for the `LinkedIssueRow` Remove confirmation flow rendered inside
 * <EpicDetailPage/>. The row component lives inside EpicDetailPage.tsx (not
 * exported), so each test mounts the full page via renderPage() and scopes
 * assertions to the `linked-issue-remove-*` testids.
 */

// --- per-file hoisted mocks (Vitest hoists vi.mock per-file; cannot be shared) ---
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
