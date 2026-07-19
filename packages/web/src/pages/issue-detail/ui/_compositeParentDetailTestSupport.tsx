import { render } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mountIssueDetail } from './_issueDetailMsw'

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

export function makeCompositeParent(overrides: Record<string, unknown> = {}) {
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
    canBeParent: false,
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

export function renderCompositeParentPage(initialEntry: string = '/issues/14') {
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

mountIssueDetail({ issue: makeCompositeParent() })
