import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import type { LinkedIssue } from '../../../entities/epic'
import { issues, linkedIssue, renderPage } from './_epicDetailPageTestUtils'
import { useMswServer } from '../../../../tests/support/msw'

/**
 * Tests for the inline `LinkedIssueRow` rendered inside <EpicDetailPage/>.
 * Covers the inline-Start affordance and the vertical task-line layout contract.
 * The row component itself lives inside EpicDetailPage.tsx (not exported), so
 * every test mounts the full page via renderPage() and scopes assertions to the
 * `linked-issue-*` testids.
 */

let _epicData: unknown = null
let _issuesData: unknown[] = []
const _removeIssueHandler = vi.fn()
const _startIssueHandler = vi.fn()
let _blockStart = false

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
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:number/start', ({ params }) => {
    _startIssueHandler(Number(params.number))
    if (_blockStart) return new Promise(() => {})
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

describe('EpicDetailPage LinkedIssueRow inline Start', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicData = makeEpic()
    _issuesData = issues
    _blockStart = false
  })

  afterEach(() => {
    cleanup()
  })

  it('renders a Start button on a startable backlog row while keeping Remove and navigation', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    const startButton = screen.getByTestId('linked-issue-start')
    expect(startButton).toBeTruthy()
    expect(startButton.textContent).toBe('Start')
    expect(startButton).not.toBeDisabled()

    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
    const navLink = screen.getByTestId('linked-issue-nav-link')
    expect(navLink).toBeTruthy()
    expect(navLink.getAttribute('href')).toContain('/issues/3')
  })

  it('hides the Start button on an in_progress linked issue row', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 2, title: 'Blocked issue', status: 'in_progress' as LinkedIssue['status'], stage: 'build' as LinkedIssue['stage'], priority: 'p1', health: 'blocked' as LinkedIssue['health'], canStart: false }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button on a blocked linked issue row even when canStart is true', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 2, title: 'Blocked issue', priority: 'p1', health: 'blocked' as LinkedIssue['health'], startBlocker: { kind: 'waiting-for', issue: { number: 1, title: 'Done issue' } } }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button on a done linked issue row', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Done issue', status: 'done' as LinkedIssue['status'], stage: 'done' as LinkedIssue['stage'], health: 'done' as LinkedIssue['health'], canStart: false }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button on a cancelled linked issue row', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 4, title: 'Cancelled issue', status: 'cancelled' as LinkedIssue['status'], stage: 'done' as LinkedIssue['stage'], health: 'cancelled' as LinkedIssue['health'], canStart: false }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
    expect(screen.getByRole('button', { name: 'Remove' })).toBeTruthy()
  })

  it('hides the Start button when canStart is false even with backlog status', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Draft issue', canStart: false, startBlocker: { kind: 'draft' } }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-start')).toBeNull()
  })

  it('invokes the start mutation with the issue number when Start is clicked', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-start')

    fireEvent.click(screen.getByTestId('linked-issue-start'))

    await waitFor(() => expect(_startIssueHandler).toHaveBeenCalledWith(3))
    expect(_startIssueHandler).toHaveBeenCalledTimes(1)
  })

  it('disables the Start button for the clicked issue while the start mutation is pending', async () => {
    _blockStart = true
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    const startButton = await screen.findByTestId('linked-issue-start')

    fireEvent.click(startButton)

    expect(startButton).toBeDisabled()
    expect(startButton.textContent).toBe('Starting...')
  })

  it('hides the Start button on all backlog issues when any sibling is in_progress', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Running', status: 'in_progress' as LinkedIssue['status'], stage: 'build' as LinkedIssue['stage'], health: 'active' as LinkedIssue['health'] }),
        linkedIssue({ number: 2, title: 'Next candidate' }),
      ],
    })

    renderPage()
    await screen.findAllByTestId('linked-issue-row')

    expect(screen.queryAllByTestId('linked-issue-start')).toHaveLength(0)
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(2)
  })
})

describe('EpicDetailPage LinkedIssueRow vertical task line layout', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicData = makeEpic()
    _issuesData = issues
    _blockStart = false
  })

  afterEach(() => {
    cleanup()
  })

  function getRow(): HTMLElement {
    return screen.getByTestId('linked-issue-row')
  }

  it('uses a vertical flex-col container instead of the old horizontal two-column layout', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    const row = getRow()
    expect(row.classList.contains('flex')).toBe(true)
    expect(row.classList.contains('flex-col')).toBe(true)
    expect(row.classList.contains('justify-between')).toBe(false)
    expect(row.classList.contains('items-center')).toBe(false)
  })

  it('keeps reading-row (#number + title) at the top, then metadata row, then blocker reason, then actions row', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({
          number: 7,
          title: 'Blocked item',
          status: IssueStatus.Backlog,
          canStart: false,
          startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } },
          health: IssueHealth.Active,
        }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

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

  it('uses break-words + [overflow-wrap:anywhere] on the title instead of truncate', async () => {
    const LONG_TITLE =
      'LinkedIssueRowLongEnglishTitleWithAnUnbrokenTokenThatMustWrapInsideTheRowAtThreeHundredTwentyPixels'

    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: LONG_TITLE }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-title')

    const title = screen.getByTestId('linked-issue-title')
    expect(title.textContent).toBe(LONG_TITLE)
    expect(title.classList.contains('break-words')).toBe(true)
    expect(title.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
    expect(title.classList.contains('truncate')).toBe(false)
  })

  it('uses flex-wrap on the metadata row so health/status/priority badges wrap at narrow widths', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-metadata-row')

    const metadataRow = screen.getByTestId('linked-issue-metadata-row')
    expect(metadataRow.classList.contains('flex')).toBe(true)
    expect(metadataRow.classList.contains('flex-wrap')).toBe(true)
  })

  it('places the number link and the title on the same primary reading row', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-reading-row')

    const readingRow = screen.getByTestId('linked-issue-reading-row')
    const navLink = screen.getByTestId('linked-issue-nav-link')
    const title = screen.getByTestId('linked-issue-title')
    expect(readingRow.contains(navLink)).toBe(true)
    expect(readingRow.contains(title)).toBe(true)
  })

  it('does NOT show the blocker reason when the issue is inline-startable', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-row')

    expect(screen.queryByTestId('linked-issue-blocker-reason')).toBeNull()
  })

  it('shows the "Still a draft" blocker reason when the issue has a draft blocker', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({
          number: 3,
          title: 'Draft candidate',
          canStart: false,
          startBlocker: { kind: 'draft' },
        }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-blocker-reason')

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Still a draft')
  })

  it('shows the "Waiting for #N" blocker reason when the issue has a waiting-for blocker', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({
          number: 3,
          title: 'Waiting on upstream',
          canStart: false,
          startBlocker: { kind: 'waiting-for', issue: { number: 42, title: 'Upstream' } },
        }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-blocker-reason')

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Waiting for #42')
  })

  it('shows the "Blocked" reason when health is blocked but no blocker is set', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({
          number: 3,
          title: 'Blocked by upstream issue',
          canStart: false,
          health: IssueHealth.Blocked,
        }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-blocker-reason')

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Blocked')
  })

  it('shows the "Another issue is in progress" reason only on rows blocked by a running sibling', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Running', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Active }),
        linkedIssue({ number: 2, title: 'Next candidate' }),
      ],
    })

    renderPage()
    await screen.findAllByTestId('linked-issue-row')

    const rows = screen.getAllByTestId('linked-issue-row')
    expect(rows[0].textContent).not.toContain('Another issue is in progress')
    expect(rows[1].textContent).toContain('Another issue is in progress')
  })

  it('shows the "Not startable" fallback reason when the issue is not startable for an unrecognized reason', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({
          number: 3,
          title: 'Done-ish issue',
          status: IssueStatus.Done,
          stage: WorkflowStage.Done,
          health: IssueHealth.Done,
          canStart: false,
        }),
      ],
    })

    renderPage()
    await screen.findByTestId('linked-issue-blocker-reason')

    const reason = screen.getByTestId('linked-issue-blocker-reason')
    expect(reason.textContent).toBe('Not startable')
  })

  it('keeps Start button gated by canInlineStartRow: present only when inline-startable', async () => {
    _epicData = makeEpic({
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Startable candidate' }),
        linkedIssue({
          number: 4,
          title: 'Non-startable',
          canStart: false,
          startBlocker: { kind: 'draft' },
        }),
      ],
    })

    renderPage()
    await screen.findAllByTestId('linked-issue-row')

    expect(screen.getAllByTestId('linked-issue-start')).toHaveLength(1)
    const rows = screen.getAllByTestId('linked-issue-row')
    expect(rows).toHaveLength(2)
    expect(screen.getAllByTestId('linked-issue-blocker-reason')).toHaveLength(1)
  })
})
