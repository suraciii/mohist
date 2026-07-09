// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { server } from '../../../../tests/support/msw'

const mocks = vi.hoisted(() => ({
  detailRenderCount: 0,
  boardRenderCount: 0,
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

const HANDLERS = [
  http.get('*/api/projects/:projectId/issues', () => HttpResponse.json({ success: true, data: [] })),
  http.get('*/api/projects/:projectId/agent/status', () =>
    HttpResponse.json({
      success: true,
      data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } },
    }),
  ),
]

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
  server.use(...HANDLERS)
})
afterEach(() => {
  cleanup()
  server.resetHandlers()
  server.use(...HANDLERS)
})
afterAll(() => server.close())

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
