// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'

const mocks = vi.hoisted(() => ({
  issues: [] as any[],
  issuesLoading: false,
  archivedIssues: [] as any[],
  agentStatus: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } } as any,
  labels: [] as string[],
  detailRenderCount: 0,
  boardRenderCount: 0,
}))

vi.mock('../../../entities/issue/api/queries', () => ({
  useIssues: () => ({ data: mocks.issues, isLoading: mocks.issuesLoading }),
  useArchivedIssues: () => ({ data: mocks.archivedIssues }),
  useLabels: () => ({ data: mocks.labels }),
}))

vi.mock('../../../entities/agent', () => ({
  useAgentStatus: () => ({ data: mocks.agentStatus }),
}))

vi.mock('../../../widgets/kanban-board/ui/KanbanBoard', () => ({
  KanbanBoard: () => {
    mocks.boardRenderCount += 1
    return <div data-testid="kanban-board-stub">KanbanBoard</div>
  },
}))

vi.mock('../../issue-detail/ui/IssueDetailPage', () => ({
  IssueDetailPage: () => {
    mocks.detailRenderCount += 1
    return <div data-testid="issue-detail-stub">IssueDetailPage</div>
  },
}))

import { IssuesPage } from './IssuesPage'
import { IssueDetailPage } from '../../issue-detail/ui/IssueDetailPage'

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

function renderRoute(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[path]}>
          <Routes>
            <Route path="/:projectName" element={<Outlet />}>
              <Route path="issues" element={<IssuesPage />} />
              <Route path="issues/:number" element={<IssueDetailPage />} />
            </Route>
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('routing: issues index vs issues/:number', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.issues = []
    mocks.issuesLoading = false
    mocks.archivedIssues = []
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the Kanban board on /issues (issues index)', () => {
    renderRoute('/demo/issues')

    expect(screen.getByTestId('kanban-board-stub')).toBeInTheDocument()
    expect(mocks.boardRenderCount).toBe(1)
    expect(screen.queryByTestId('issue-detail-stub')).not.toBeInTheDocument()
  })

  it('renders the Issue Detail page on /issues/:number (does not shadow the detail route)', () => {
    renderRoute('/demo/issues/42')

    expect(screen.getByTestId('issue-detail-stub')).toBeInTheDocument()
    expect(mocks.detailRenderCount).toBe(1)
    expect(screen.queryByTestId('kanban-board-stub')).not.toBeInTheDocument()
  })
})
