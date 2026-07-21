import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { act, cleanup, screen, waitFor, within } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'
import { server } from '../../../../tests/support/msw'
import { makeCompositeParent, renderCompositeParentPage } from './_compositeParentDetailTestSupport'

mountIssueDetail({ issue: makeCompositeParent() })

afterEach(() => {
  cleanup()
})

function currentParent(children: Array<Record<string, unknown>>) {
  return {
    ...makeCompositeParent(),
    children,
    childIssuesSummary: {
      hasChildren: true,
      count: children.length,
      backlogCount: 0,
      inProgressCount: children.filter((child) => child.status === 'in_progress').length,
      doneCount: children.filter((child) => child.status === 'done').length,
      cancelledCount: children.filter((child) => child.status === 'cancelled').length,
      blockedCount: children.filter((child) => child.health === 'blocked').length,
    },
  }
}

describe('IssueDetailPage composite parent data refresh', () => {
  it('reflects a detached child after a parent detail refresh', async () => {
    let children: Array<Record<string, unknown>> = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'blocked', repositoryName: 'web' },
    ]
    server.use(http.get('*/api/projects/:projectId/issues/:number', () =>
      HttpResponse.json({ success: true, data: currentParent(children) })))

    const { unmount } = renderCompositeParentPage()
    expect((await waitFor(() => screen.getAllByTestId('composite-child-row')))
      .map((row) => row.getAttribute('data-child-number'))).toEqual(['12', '13'])

    children = [{ number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' }]
    unmount()
    cleanup()
    renderCompositeParentPage()

    expect((await waitFor(() => screen.getAllByTestId('composite-child-row')))
      .map((row) => row.getAttribute('data-child-number'))).toEqual(['12'])
    expect(screen.getByTestId('composite-parent-progress-label')).toHaveTextContent('1/1 done')
    expect(screen.getByTestId('composite-parent-overview')).toHaveAttribute('data-blocked-count', '0')
  })

  it('removes blocked treatment after a child health refresh', async () => {
    let children: Array<Record<string, unknown>> = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'blocked', repositoryName: 'web' },
    ]
    server.use(http.get('*/api/projects/:projectId/issues/:number', () =>
      HttpResponse.json({ success: true, data: currentParent(children) })))

    const { unmount } = renderCompositeParentPage()
    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toHaveAttribute('data-blocked-count', '1')

    children = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'active', repositoryName: 'web' },
    ]
    unmount()
    cleanup()
    renderCompositeParentPage()

    const row = (await waitFor(() => screen.getAllByTestId('composite-child-row')))[1]
    expect(screen.getByTestId('composite-parent-overview')).toHaveAttribute('data-blocked-count', '0')
    expect(row).toHaveAttribute('data-child-blocked', 'false')
    expect(within(row).queryByTestId('composite-child-blocked-indicator')).toBeNull()
  })
})

describe('IssueDetailPage composite parent workflow suppression', () => {
  let workflowCalls: string[]

  beforeEach(() => {
    workflowCalls = []
    server.use(
      http.get('*/api/projects/:projectId/issues/:number/:surface', ({ params }) => {
        workflowCalls.push(String(params.surface))
        return HttpResponse.json({ success: true, data: [] })
      }),
      http.get('*/api/workflow-runs/:runId/:surface', ({ params }) => {
        workflowCalls.push(String(params.surface))
        return HttpResponse.json({ success: true, data: [] })
      }),
      http.post('*/api/projects/:projectId/issues/:number/rebase', () => {
        workflowCalls.push('rebase')
        return HttpResponse.json({ success: true, data: { status: 'queued' } })
      }),
    )
  })

  it('does not render workflow-specific surfaces for a parent', async () => {
    mockIssue(makeCompositeParent())
    const { container } = renderCompositeParentPage()

    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toBeTruthy()
    for (const testId of [
      'branch-bar-frame', 'workflow-view-frame', 'workflow-sessions-panel', 'task-progress-panel',
      'diff-files-section', 'diff-summary-banner', 'commits-section', 'latest-artifacts-panel',
      'pr-delivery-summary-frame', 'runtime-evidence-frame', 'workflow-yaml-dialog-frame',
       'reference-rail-workflow-profile', 'reference-rail-drift', 'reference-rail-convergence',
       'runtime-decision-surface-frame',
    ]) {
      expect(container.querySelector(`[data-testid="${testId}"]`)).toBeNull()
    }
  })

  it('does not request workflow data for a parent', async () => {
    mockIssue(makeCompositeParent())
    renderCompositeParentPage()

    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toBeTruthy()
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 100))
    })
    expect(workflowCalls).toEqual([])
  })
})
