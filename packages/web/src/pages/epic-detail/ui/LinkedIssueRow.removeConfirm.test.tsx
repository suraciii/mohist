import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { useMutation } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import type { EpicDetail } from '../../../entities/epic'
import { issues, linkedIssue, renderPage as renderEpicDetailPage } from './_epicDetailPageTestUtils'
import type { RemoveEpicIssueHook } from './EpicDetailPage'
import { useMswServer } from '../../../../tests/support/msw'

/**
 * Tests for the `LinkedIssueRow` Remove confirmation flow rendered inside
 * <EpicDetailPage/>. The row component lives inside EpicDetailPage.tsx (not
 * exported), so each test mounts the full page via renderPage() and scopes
 * assertions to the `linked-issue-remove-*` testids.
 */

let _epicData: unknown = null
let _issuesData: unknown[] = []
const _removeIssueHandler = vi.fn()
let _blockRemove = false

const removeEpicIssueHook: RemoveEpicIssueHook = () =>
  useMutation<{ epicNumber: number; issueNumber: number }, Error, { epicNumber: number; issueNumber: number }>({
    mutationFn: async ({ epicNumber, issueNumber }) => {
      _removeIssueHandler(issueNumber)
      if (_blockRemove) return new Promise(() => {})
      return { epicNumber, issueNumber }
    },
  })

function renderPage() {
  return renderEpicDetailPage({
    epic: _epicData as EpicDetail,
    dependencies: { removeEpicIssueHook },
  })
}

useMswServer(
  http.get('*/api/projects/:projectId/epics/:epicNumber', () =>
    HttpResponse.json({ success: true, data: _epicData }),
  ),
  http.get('*/api/projects/:projectId/epics/:epicNumber/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issuesData }),
  ),
  http.delete('*/api/projects/:projectId/epics/:epicNumber/issues/:issueNumber', ({ params }) => {
    _removeIssueHandler(Number(params.issueNumber))
    if (_blockRemove) return new Promise(() => {})
    return HttpResponse.json({ success: true, data: {} })
  }),
)

function makeEpic(overrides: Record<string, unknown> = {}) {
  return {
    projectId: 'proj-1',
    number: 123,
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

describe('EpicDetailPage LinkedIssueRow Remove confirmation flow', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicData = makeEpic()
    _issuesData = issues
    _blockRemove = false
  })

  afterEach(() => {
    cleanup()
  })

  it('places the Remove button in the actions row, not the primary reading row', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    const readingRow = screen.getByTestId('linked-issue-reading-row')
    const actionsRow = screen.getByTestId('linked-issue-actions-row')
    const removeButton = screen.getByTestId('linked-issue-remove')

    expect(readingRow.contains(removeButton)).toBe(false)
    expect(actionsRow.contains(removeButton)).toBe(true)
  })

  it('renders the Remove button with the ghost variant for a secondary de-emphasized affordance', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    const removeButton = screen.getByTestId('linked-issue-remove')
    expect(removeButton.className).toContain('hover:bg-muted')
    expect(removeButton.className).not.toContain('border-border')
  })

  it('a single click on Remove does NOT call removeEpicIssue.mutate', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    expect(_removeIssueHandler).not.toHaveBeenCalled()
  })

  it('clicking Remove opens a confirmation Dialog that shows the issue number and an explanation', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
        linkedIssue({ number: 7, title: 'Other issue' }),
      ],
    })

    renderPage()
    await screen.findAllByTestId('linked-issue-remove')

    const removeButtons = screen.getAllByTestId('linked-issue-remove')
    fireEvent.click(removeButtons[0])

    const dialog = screen.getByTestId('linked-issue-remove-confirm-dialog')
    expect(dialog).toBeTruthy()
    expect(dialog.textContent).toMatch(/remove #3 from this epic\?/i)
    expect(dialog.textContent).toMatch(/workflow state/i)
  })

  it('does not render the remove confirm dialog in the DOM before Remove is clicked', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
    expect(screen.queryByTestId('linked-issue-remove-confirm')).toBeNull()
    expect(screen.queryByTestId('linked-issue-remove-cancel')).toBeNull()
  })

  it('clicking Cancel keeps the link intact and closes the dialog without calling mutate', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    fireEvent.click(screen.getByTestId('linked-issue-remove'))
    fireEvent.click(screen.getByTestId('linked-issue-remove-cancel'))

    expect(_removeIssueHandler).not.toHaveBeenCalled()
    expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
  })

  it('clicking Confirm (destructive) calls removeEpicIssue.mutate with the correct epic and issue numbers', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    fireEvent.click(screen.getByTestId('linked-issue-remove'))
    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))

    await waitFor(() => {
      expect(_removeIssueHandler).toHaveBeenCalledWith(3)
      expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
    })
    expect(_removeIssueHandler).toHaveBeenCalledTimes(1)
  })

  it('the Confirm button uses the destructive variant', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    const confirm = screen.getByTestId('linked-issue-remove-confirm')
    expect(confirm.className).toContain('text-destructive')
  })

  it('the Cancel button uses the outline variant', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    const cancel = screen.getByTestId('linked-issue-remove-cancel')
    expect(cancel.className).toContain('border-border')
  })

  it('Cancel and Confirm are not disabled while removeEpicIssue is not pending', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    fireEvent.click(screen.getByTestId('linked-issue-remove'))

    const confirm = screen.getByTestId('linked-issue-remove-confirm')
    const cancel = screen.getByTestId('linked-issue-remove-cancel')
    expect(confirm).not.toBeDisabled()
    expect(cancel).not.toBeDisabled()
  })

  it('gates the Remove affordance on removeEpicIssue.isPending so the dialog cannot be opened mid-mutation', async () => {
    _blockRemove = true
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-remove')

    // Trigger remove to enter pending state
    fireEvent.click(screen.getByTestId('linked-issue-remove'))
    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))

    const removeButton = screen.getByTestId('linked-issue-remove')
    await waitFor(() => {
      expect(_removeIssueHandler).toHaveBeenCalledTimes(1)
      expect(removeButton).toBeDisabled()
    })

    fireEvent.click(removeButton)
    expect(screen.queryByTestId('linked-issue-remove-confirm-dialog')).toBeNull()
    expect(_removeIssueHandler).toHaveBeenCalledTimes(1)
  })

  it('each row owns its own remove-confirm open state — clicking one row does not open another row dialog', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'First issue' }),
        linkedIssue({ number: 7, title: 'Second issue' }),
      ],
    })

    renderPage()
    await screen.findAllByTestId('linked-issue-remove')

    const removeButtons = screen.getAllByTestId('linked-issue-remove')
    fireEvent.click(removeButtons[0])

    expect(screen.getByTestId('linked-issue-remove-confirm-dialog')).toBeTruthy()
    expect(screen.getAllByTestId('linked-issue-remove-confirm')).toHaveLength(1)
    expect(screen.getAllByTestId('linked-issue-remove-cancel')).toHaveLength(1)
  })
})
