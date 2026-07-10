// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { useMswServer } from '../../../../tests/support/msw'
import { IssuesPage, type IssuesPageComponents } from './IssuesPage'

const mocks = {
  detailRenderCount: 0,
  boardRenderCount: 0,
}

const components: IssuesPageComponents = {
  KanbanBoard: () => {
    mocks.boardRenderCount += 1
    return <div data-testid="kanban-board-stub">KanbanBoard</div>
  },
}

function IssueDetailRouteSentinel() {
  mocks.detailRenderCount += 1
  return <div data-testid="issue-detail-stub">IssueDetailPage</div>
}

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

const HANDLERS = [
  http.get('*/api/projects/:projectId/issues', () => HttpResponse.json({ success: true, data: [] })),
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({
      success: true,
      data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } },
    }),
  ),
]

useMswServer(...HANDLERS)

function renderRoute(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[path]}>
          <Routes>
            <Route path="/:projectName" element={<Outlet />}>
              <Route path="issues" element={<IssuesPage components={components} />} />
              <Route path="issues/:number" element={<IssueDetailRouteSentinel />} />
            </Route>
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('routing: issues index vs issues/:number', () => {
  it('renders the Kanban board on /issues (issues index)', async () => {
    renderRoute('/demo/issues')

    expect(await screen.findByTestId('kanban-board-stub')).toBeInTheDocument()
    expect(mocks.boardRenderCount).toBe(1)
    expect(screen.queryByTestId('issue-detail-stub')).not.toBeInTheDocument()
  })

  it('renders the Issue Detail page on /issues/:number (does not shadow the detail route)', async () => {
    renderRoute('/demo/issues/42')

    expect(await screen.findByTestId('issue-detail-stub')).toBeInTheDocument()
    expect(mocks.detailRenderCount).toBe(1)
    expect(screen.queryByTestId('kanban-board-stub')).not.toBeInTheDocument()
  })
})
