import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import type { EpicDetail } from '../../../entities/epic'

import { epic, linkedIssue, renderPage } from './_epicDetailPageTestUtils'
import { useMswServer } from '../../../../tests/support/msw'

let _epicData: unknown = null
let _issuesData: unknown[] = []
const _addIssueHandler = vi.fn()

useMswServer(
  http.get('*/api/projects/:projectId/epics/:epicNumber/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.post('*/api/projects/:projectId/epics/:epicNumber/issues', async ({ request }) => {
    const body = (await request.json()) as { issueNumber: number }
    _addIssueHandler(body)
    return HttpResponse.json({ success: true, data: { epicNumber: 123, issueNumber: body.issueNumber } })
  }),
)

const searchEpic = {
  ...epic,
  linkedIssues: [
    linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
  ],
}

const searchIssues = [
  { number: 1, title: 'Done issue', status: 'done' as const, isDraft: false, canStart: false, blocker: null },
  {
    number: 4,
    title: 'Archived candidate',
    status: 'backlog' as const,
    archivedAt: '2026-01-15T00:00:00Z',
  },
  {
    number: 5,
    title: 'Closed candidate',
    status: 'done' as const,
  },
  {
    number: 6,
    title: 'Blocked candidate',
    status: 'backlog' as const,
    isDraft: false,
    canStart: false,
    blocker: { kind: 'waiting-for', issue: { number: 1, title: 'Done issue' } },
  },
  { number: 2, title: 'Blocked issue', status: 'in_progress' as const, isDraft: false, canStart: false, blocker: null },
  { number: 3, title: 'Candidate issue', status: 'in_progress' as const, isDraft: false, canStart: true, blocker: null },
]

function renderSearchPage() {
  return renderPage({ epic: _epicData as EpicDetail, issues: _issuesData })
}

describe('EpicDetailPage searchable Add Issue', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _epicData = searchEpic
    _issuesData = searchIssues
  })

  afterEach(() => {
    cleanup()
  })

  it('filters candidates by issue number or title when search text is typed', async () => {
    renderSearchPage()

    await screen.findByTestId('epic-issue-selector-trigger')
    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    const search = await screen.findByTestId('epic-issue-search')
    expect(screen.getAllByTestId('epic-issue-option').map(node => node.getAttribute('data-issue-number')))
      .toEqual(['4', '5', '6', '2', '3'])

    fireEvent.change(search, { target: { value: 'archived' } })
    await waitFor(() => {
      const visible = screen.getAllByTestId('epic-issue-option').map(node => node.getAttribute('data-issue-number'))
      expect(visible).toEqual(['4'])
    })

    fireEvent.change(search, { target: { value: '#6' } })
    await waitFor(() => {
      const visible = screen.getAllByTestId('epic-issue-option').map(node => node.getAttribute('data-issue-number'))
      expect(visible).toEqual(['6'])
    })

    fireEvent.change(search, { target: { value: 'no-match-query' } })
    expect(screen.queryByTestId('epic-issue-option')).toBeNull()
  })

  it('disables closed, archived, and non-startable candidates with inline reasons', async () => {
    renderSearchPage()

    await screen.findByTestId('epic-issue-selector-trigger')
    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    await screen.findByTestId('epic-issue-search')

    const options = screen.getAllByTestId('epic-issue-option')
    const findOption = (issueNumber: number) =>
      options.find(node => node.getAttribute('data-issue-number') === String(issueNumber)) as HTMLElement
    const unavailable = options
      .filter(node => node.getAttribute('data-unavailable') === 'true')
      .map(node => node.getAttribute('data-issue-number'))
    expect(unavailable).toEqual(['4', '5', '6'])

    const archived = findOption(4)
    const closed = findOption(5)
    const blocked = findOption(6)

    expect(archived.hasAttribute('disabled')).toBe(true)
    expect(closed.hasAttribute('disabled')).toBe(true)
    expect(blocked.hasAttribute('disabled')).toBe(true)

    expect(screen.getByText('Archived')).toBeTruthy()
    expect(screen.getByText('Closed')).toBeTruthy()
    expect(screen.getByText('Waiting for #1')).toBeTruthy()

    fireEvent.click(archived)
    fireEvent.click(blocked)
    expect(_addIssueHandler).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Add Issue' })).toBeDisabled()
  })

  it('disables the submit button when no candidate is selected', async () => {
    renderSearchPage()

    await screen.findByTestId('epic-issue-selector-trigger')
    expect(screen.getByRole('button', { name: 'Add Issue' })).toBeDisabled()
  })

  it('disables the trigger and submit when no selectable candidate exists', async () => {
    _epicData = {
      ...searchEpic,
      linkedIssues: [],
    }
    _issuesData = [
      { number: 4, title: 'Archived candidate', status: 'backlog' as const, archivedAt: '2026-01-15T00:00:00Z' },
      { number: 5, title: 'Closed candidate', status: 'done' as const },
    ]

    renderSearchPage()

    const trigger = await screen.findByTestId('epic-issue-selector-trigger')
    expect(trigger).toBeDisabled()
    expect(trigger).toHaveTextContent('No selectable issues')
    expect(screen.getByRole('button', { name: 'Add Issue' })).toBeDisabled()
  })
})
