import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'
import { makeCompositeParent, renderCompositeParentPage } from './_compositeParentDetailTestSupport'

mountIssueDetail({ issue: makeCompositeParent() })

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage composite parent overview', () => {
  it('renders the composite parent overview with progress, blocked count, and child list for a parent with mixed children', async () => {
    mockIssue(makeCompositeParent())

    renderCompositeParentPage()

    const overview = await waitFor(() => screen.getByTestId('composite-parent-overview'))
    expect(overview).toHaveAttribute('data-child-count', '3')
    expect(overview).toHaveAttribute('data-done-count', '1')
    expect(overview).toHaveAttribute('data-blocked-count', '1')

    expect(screen.getByTestId('composite-parent-progress-label')).toHaveTextContent('1/3 done')

    const blocked = screen.getByTestId('composite-parent-blocked-stat')
    expect(blocked).toHaveAttribute('data-blocked', 'true')
    expect(within(blocked).getByTestId('composite-parent-blocked-label')).toHaveTextContent('1')

    const rows = screen.getAllByTestId('composite-child-row')
    expect(rows.map((row) => row.getAttribute('data-child-number'))).toEqual(['12', '13', '15'])

    const row13 = rows[1]
    expect(within(row13).getByTestId('composite-child-number')).toHaveTextContent('#13')
    expect(within(row13).getByTestId('composite-child-title')).toHaveTextContent('Web portal upgrade')
    expect(within(row13).getByTestId('composite-child-status-pill')).toHaveAttribute('data-status', 'in_progress')
    expect(within(row13).getByTestId('composite-child-repository')).toHaveAttribute('data-repository', 'web')
    expect(within(row13).getByTestId('composite-child-blocked-indicator')).toBeTruthy()
    expect(row13).toHaveAttribute('data-child-blocked', 'true')

    const row12 = rows[0]
    expect(within(row12).getByTestId('composite-child-repository')).toHaveAttribute('data-repository', 'server')
    expect(within(row12).queryByTestId('composite-child-blocked-indicator')).toBeNull()
    expect(row12).toHaveAttribute('data-child-blocked', 'false')

    const row15 = rows[2]
    expect(within(row15).getByTestId('composite-child-status-pill')).toHaveAttribute('data-status', 'cancelled')
    expect(within(row15).queryByTestId('composite-child-blocked-indicator')).toBeNull()
    expect(row15).toHaveAttribute('data-child-blocked', 'false')
  })

  it('displays a zero blocked-child count when no child has blocked health', async () => {
    const parent = makeCompositeParent({
      children: [
        { number: 12, title: 'All good', status: 'done', health: 'done', repositoryName: 'server' },
        { number: 13, title: 'Active work', status: 'in_progress', health: 'active', repositoryName: 'web' },
      ],
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 0,
      },
    })
    mockIssue(parent)

    renderCompositeParentPage()

    const overview = await waitFor(() => screen.getByTestId('composite-parent-overview'))
    expect(overview).toHaveAttribute('data-blocked-count', '0')

    const blocked = screen.getByTestId('composite-parent-blocked-stat')
    expect(blocked).toHaveAttribute('data-blocked', 'false')
    expect(within(blocked).getByTestId('composite-parent-blocked-label')).toHaveTextContent('0')

    const rows = screen.getAllByTestId('composite-child-row')
    for (const row of rows) {
      expect(row).toHaveAttribute('data-child-blocked', 'false')
      expect(within(row).queryByTestId('composite-child-blocked-indicator')).toBeNull()
    }
  })

  it('navigates to the child issue detail page when activating a child row', async () => {
    mockIssue(makeCompositeParent())

    renderCompositeParentPage()

    const row13 = await waitFor(() => screen.getAllByTestId('composite-child-row')[1])
    expect(row13.getAttribute('href')).toBe('/Project%201/issues/13')

    fireEvent.click(row13)

    await waitFor(() =>
      expect(screen.getByTestId('current-path').textContent).toBe('/Project%201/issues/13'),
    )
  })

  it('shows a navigable parent backlink on a child issue and navigates back to the parent', async () => {
    const child = {
      number: 13,
      title: 'Child issue',
      body: '',
      status: 'in_progress',
      health: 'active',
      projectId: 'proj-1',
      labels: {},
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      comments: [],
      isDraft: false,
      canStart: true,
      blocker: null,
      parentIssueRef: { number: 14, title: 'Composite parent issue' },
    }
    mockIssue(child)

    renderCompositeParentPage('/issues/13')

    const backlink = await waitFor(() => screen.getByTestId('parent-issue-backlink'))
    expect(backlink).toHaveTextContent('#14 Composite parent issue')
    expect(backlink).toHaveAttribute('href', '/Project%201/issues/14')

    fireEvent.click(backlink)

    await waitFor(() =>
      expect(screen.getByTestId('current-path').textContent).toBe('/Project%201/issues/14'),
    )
  })

  it('does not display a parent backlink for an ordinary issue without parentIssueRef', async () => {
    const ordinary = {
      number: 14,
      title: 'Ordinary issue',
      body: '',
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      projectId: 'proj-1',
      labels: {},
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      comments: [],
      isDraft: false,
      canStart: true,
      blocker: null,
    }
    mockIssue(ordinary)

    renderCompositeParentPage()

    await waitFor(() => expect(screen.queryByTestId('composite-parent-overview')).toBeNull())
    expect(screen.queryByTestId('parent-issue-metadata-row')).toBeNull()
  })

  it('keeps description, comments, repository metadata, and edit button visible on a parent issue', async () => {
    mockIssue(makeCompositeParent())

    renderCompositeParentPage()

    expect(await waitFor(() => screen.getByTestId('description-section'))).toBeTruthy()
    expect(screen.getByTestId('comments-section')).toBeTruthy()
    expect(screen.getByTestId('edit-issue-button')).toBeTruthy()
    expect(screen.getByTestId('repository-metadata-row')).toBeTruthy()
    expect(screen.getByTestId('repository-name')).toHaveTextContent('master')
    expect(screen.queryByText('Workflow Stage')).toBeNull()

    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toBeTruthy()
  })

  it('shows each persisted repository name on child rows and never falls back to the project default', async () => {
    const parent = makeCompositeParent({
      repositoryName: 'default-repo',
      children: [
        { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
        { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'blocked', repositoryName: 'web' },
      ],
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 1,
      },
    })
    mockIssue(parent)

    renderCompositeParentPage()

    const rows = await waitFor(() => screen.getAllByTestId('composite-child-row'))
    const reposInRows = rows.map(
      (row) => within(row).getByTestId('composite-child-repository').getAttribute('data-repository'),
    )
    expect(reposInRows).toEqual(['server', 'web'])
  })

  it('shows persisted repository metadata for a child detail page distinct from the parent', async () => {
    const child = {
      number: 13,
      title: 'Child issue',
      body: '',
      status: 'in_progress',
      health: 'active',
      projectId: 'proj-1',
      labels: {},
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      comments: [],
      isDraft: false,
      canStart: true,
      blocker: null,
      repositoryName: 'web',
      repository: { name: 'web', baseBranch: 'web-main', gitUrl: 'git@example.com:web.git' },
      parentIssueRef: { number: 14, title: 'Parent assigned to server' },
    }
    mockIssue(child)

    renderCompositeParentPage('/issues/13')

    const repositoryRow = await waitFor(() => screen.getByTestId('repository-metadata-row'))
    expect(within(repositoryRow).getByTestId('repository-name')).toHaveTextContent('web')
    expect(within(repositoryRow).getByTestId('repository-base-branch')).toHaveTextContent('web-main')

    expect(screen.getByTestId('parent-issue-backlink')).toBeTruthy()
    expect(screen.queryByTestId('composite-parent-overview')).toBeNull()
  })
})
