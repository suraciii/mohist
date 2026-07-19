import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { HttpResponse, http } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'
import { server } from '../../../../tests/support/msw'

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}</div>
}

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeParent(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Composite parent issue',
    body: 'Composite parent description that should remain visible.',
    status: 'in_progress',
    workflowStage: 'build',
    workflowStatus: 'running',
    workflowRunId: 'wr_composite_parent',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [
      {
        id: 'c1',
        author: 'tester',
        body: 'A reviewer comment that should remain visible on a parent.',
        createdAt: '2026-01-02T00:00:00Z',
      },
    ],
    isDraft: false,
    canStart: true,
    blocker: null,
    repositoryName: 'server',
    repository: {
      name: 'master',
      baseBranch: 'master',
      gitUrl: 'https://github.com/suraciii/mohist.git',
    },
    prerequisites: [
      { number: 9, title: 'Prerequisite issue', completed: true },
    ],
    children: [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'blocked', repositoryName: 'web' },
      { number: 15, title: 'Cancelled tool swap', status: 'cancelled', health: 'cancelled', repositoryName: 'server' },
    ],
    childIssuesSummary: {
      hasChildren: true,
      count: 3,
      backlogCount: 0,
      inProgressCount: 1,
      doneCount: 1,
      cancelledCount: 1,
      blockedCount: 1,
    },
    ...overrides,
  }
}

function renderPage(initialEntry: string = '/issues/14') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <LocationProbe />
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
            <Route path="/:projectName/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

mountIssueDetail({ issue: makeParent() })

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage composite parent overview', () => {
  it('renders the composite parent overview with progress, blocked count, and child list for a parent with mixed children', async () => {
    mockIssue(makeParent())

    renderPage()

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
    const parent = makeParent({
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

    renderPage()

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
    mockIssue(makeParent())

    renderPage()

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

    renderPage('/issues/13')

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

    renderPage()

    await waitFor(() => expect(screen.queryByTestId('composite-parent-overview')).toBeNull())
    expect(screen.queryByTestId('parent-issue-metadata-row')).toBeNull()
  })

  it('keeps description, comments, repository metadata, and edit button visible on a parent issue', async () => {
    mockIssue(makeParent())

    renderPage()

    expect(await waitFor(() => screen.getByTestId('description-section'))).toBeTruthy()
    expect(screen.getByTestId('comments-section')).toBeTruthy()
    expect(screen.getByTestId('edit-issue-button')).toBeTruthy()
    expect(screen.getByTestId('repository-metadata-row')).toBeTruthy()
    expect(screen.getByTestId('repository-name')).toHaveTextContent('master')

    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toBeTruthy()
  })

  it('shows each persisted repository name on child rows and never falls back to the project default', async () => {
    const parent = makeParent({
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

    renderPage()

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

    renderPage('/issues/13')

    const repositoryRow = await waitFor(() => screen.getByTestId('repository-metadata-row'))
    expect(within(repositoryRow).getByTestId('repository-name')).toHaveTextContent('web')
    expect(within(repositoryRow).getByTestId('repository-base-branch')).toHaveTextContent('web-main')

    expect(screen.getByTestId('parent-issue-backlink')).toBeTruthy()
    expect(screen.queryByTestId('composite-parent-overview')).toBeNull()
  })
})

describe('IssueDetailPage composite parent data refresh on relationship change', () => {
  it('reflects current server relationships after a detach — child absent from parent list and progress totals', async () => {
    let currentChildren: Array<Record<string, unknown>> = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'blocked', repositoryName: 'web' },
    ]
    server.use(
      http.get('*/api/projects/:projectId/issues/:number', () => {
        const data: Record<string, unknown> = {
          number: 14,
          title: 'Composite parent',
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
          children: currentChildren,
        }
        if (currentChildren.length > 0) {
          data.childIssuesSummary = {
            hasChildren: true,
            count: currentChildren.length,
            backlogCount: 0,
            inProgressCount: currentChildren.filter((c) => c.status === 'in_progress').length,
            doneCount: currentChildren.filter((c) => c.status === 'done').length,
            cancelledCount: currentChildren.filter((c) => c.status === 'cancelled').length,
            blockedCount: currentChildren.filter((c) => c.health === 'blocked').length,
          }
        } else {
          data.childIssuesSummary = null
        }
        return HttpResponse.json({ success: true, data })
      }),
    )

    const { unmount } = renderPage()
    const rowsBefore = await waitFor(() => screen.getAllByTestId('composite-child-row'))
    expect(rowsBefore.map((row) => row.getAttribute('data-child-number'))).toEqual(['12', '13'])
    expect(screen.getByTestId('composite-parent-overview')).toHaveAttribute('data-blocked-count', '1')

    currentChildren = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
    ]

    unmount()
    cleanup()
    renderPage()

    const rowsAfter = await waitFor(() => screen.getAllByTestId('composite-child-row'))
    expect(rowsAfter.map((row) => row.getAttribute('data-child-number'))).toEqual(['12'])
    expect(screen.getByTestId('composite-parent-progress-label')).toHaveTextContent('1/1 done')
    expect(screen.getByTestId('composite-parent-overview')).toHaveAttribute('data-blocked-count', '0')
  })

  it('reflects child health changes — blocked count drops after a child health flips from blocked to active', async () => {
    let currentChildren: Array<Record<string, unknown>> = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'blocked', repositoryName: 'web' },
    ]
    server.use(
      http.get('*/api/projects/:projectId/issues/:number', () => {
        const data: Record<string, unknown> = {
          number: 14,
          title: 'Composite parent',
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
          children: currentChildren,
          childIssuesSummary: {
            hasChildren: true,
            count: currentChildren.length,
            backlogCount: 0,
            inProgressCount: currentChildren.filter((c) => c.status === 'in_progress').length,
            doneCount: currentChildren.filter((c) => c.status === 'done').length,
            cancelledCount: currentChildren.filter((c) => c.status === 'cancelled').length,
            blockedCount: currentChildren.filter((c) => c.health === 'blocked').length,
          },
        }
        return HttpResponse.json({ success: true, data })
      }),
    )

    const { unmount } = renderPage()
    expect(
      await waitFor(() => screen.getByTestId('composite-parent-overview')),
    ).toHaveAttribute('data-blocked-count', '1')
    const rowBefore = screen.getAllByTestId('composite-child-row')[1]
    expect(rowBefore).toHaveAttribute('data-child-blocked', 'true')

    currentChildren = [
      { number: 12, title: 'Server refactor', status: 'done', health: 'done', repositoryName: 'server' },
      { number: 13, title: 'Web portal upgrade', status: 'in_progress', health: 'active', repositoryName: 'web' },
    ]

    unmount()
    cleanup()
    renderPage()

    const overview = await waitFor(() => screen.getByTestId('composite-parent-overview'))
    expect(overview).toHaveAttribute('data-blocked-count', '0')
    const rowAfter = screen.getAllByTestId('composite-child-row')[1]
    expect(rowAfter).toHaveAttribute('data-child-blocked', 'false')
    expect(within(rowAfter).queryByTestId('composite-child-blocked-indicator')).toBeNull()
  })
})

describe('IssueDetailPage composite parent suppresses workflow surfaces', () => {
  let workflowCalls: string[]

  beforeEach(() => {
    workflowCalls = []
    server.use(
      http.get('*/api/projects/:projectId/issues/:number/diff', () => {
        workflowCalls.push('diff')
        return HttpResponse.json({ success: true, data: { available: false, reason: 'not_started' } })
      }),
      http.get('*/api/projects/:projectId/issues/:number/commits', () => {
        workflowCalls.push('commits')
        return HttpResponse.json({ success: true, data: { available: false, reason: 'not_started' } })
      }),
      http.get('*/api/projects/:projectId/issues/:number/workflow/status', () => {
        workflowCalls.push('timeline')
        return HttpResponse.json({ success: true, data: { workflow: null } })
      }),
      http.get('*/api/projects/:projectId/issues/:number/workspace-status', () => {
        workflowCalls.push('workspace-status')
        return HttpResponse.json({ success: true, data: { exists: false } })
      }),
      http.get('*/api/projects/:projectId/issues/:number/workflow/artifacts', () => {
        workflowCalls.push('artifacts')
        return HttpResponse.json({ success: true, data: [] })
      }),
      http.get('*/api/projects/:projectId/issues/:number/workflow-profile', () => {
        workflowCalls.push('workflow-profile')
        return HttpResponse.json({
          success: true,
          data: {
            issueNumber: 14,
            projectId: 'proj-1',
            hasCustomTemplate: false,
            yaml: null,
            workflowRunId: null,
            profileId: '',
          },
        })
      }),
      http.get('*/api/projects/:projectId/workflow-profile', () => {
        workflowCalls.push('project-workflow-profile')
        return HttpResponse.json({
          success: true,
          data: {
            projectId: 'proj-1',
            defaultTemplateId: null,
            disabledWorkflowProfileIds: [],
          },
        })
      }),
      http.get('*/api/workflow-templates/system*', () => {
        workflowCalls.push('workflow-templates')
        return HttpResponse.json({ success: true, data: [] })
      }),
      http.get('*/api/workflow-runs/:runId/sessions', () => {
        workflowCalls.push('workflow-run-sessions')
        return HttpResponse.json({ success: true, data: [] })
      }),
      http.get('*/api/workflow-runs/:runId/yaml', () => {
        workflowCalls.push('workflow-run-yaml')
        return HttpResponse.json({ success: true, data: { workflowRunId: 'unused', yaml: '' } })
      }),
      http.post('*/api/projects/:projectId/issues/:number/rebase', () => {
        workflowCalls.push('rebase')
        return HttpResponse.json({ success: true, data: { status: 'queued' } })
      }),
    )
  })

  it('does not render the workflow view, branch bar, sessions, diff, commits, or artifacts panels for a parent', async () => {
    mockIssue(makeParent())

    const { container } = renderPage()

    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toBeTruthy()

    expect(container.querySelector('[data-testid="branch-bar-frame"]')).toBeNull()
    expect(container.querySelector('[data-testid="workflow-view-frame"]')).toBeNull()
    expect(container.querySelector('[data-testid="workflow-sessions-panel"]')).toBeNull()
    expect(container.querySelector('[data-testid="task-progress-panel"]')).toBeNull()
    expect(container.querySelector('[data-testid="diff-files-section"]')).toBeNull()
    expect(container.querySelector('[data-testid="diff-summary-banner"]')).toBeNull()
    expect(container.querySelector('[data-testid="commits-section"]')).toBeNull()
    expect(container.querySelector('[data-testid="latest-artifacts-panel"]')).toBeNull()
    expect(container.querySelector('[data-testid="pr-delivery-summary-frame"]')).toBeNull()
    expect(container.querySelector('[data-testid="runtime-evidence-frame"]')).toBeNull()
    expect(container.querySelector('[data-testid="workflow-yaml-dialog-frame"]')).toBeNull()

    expect(container.querySelector('[data-testid="reference-rail-workflow-profile"]')).toBeNull()
    expect(container.querySelector('[data-testid="reference-rail-drift"]')).toBeNull()
    expect(container.querySelector('[data-testid="reference-rail-convergence"]')).toBeNull()
    expect(container.querySelector('[data-testid="runtime-decision-surface-frame"]')).toBeNull()
    expect(container.querySelector('[data-testid="mobile-action-bar"]')).toBeNull()
  })

  it('fails when a workflow-only request is fired for a parent issue', async () => {
    mockIssue(makeParent())

    renderPage()

    expect(await waitFor(() => screen.getByTestId('composite-parent-overview'))).toBeTruthy()

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 100))
    })

    expect(workflowCalls).toEqual([])
  })
})
