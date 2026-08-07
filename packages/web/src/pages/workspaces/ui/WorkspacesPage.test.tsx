import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { WorkspacesPage } from './WorkspacesPage'

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'demo',
    repositories: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  },
]

function baseWorkspace(overrides: Record<string, unknown> = {}) {
  return {
    projectId: 'proj-1',
    name: 'pay-refactor',
    origin: { kind: 'manual' },
    repositories: ['server', 'web'],
    status: 'active',
    home: null,
    createdAt: '2026-01-01T00:00:00Z',
    archivedAt: null,
    boundSessionCount: 0,
    sessions: null,
    ...overrides,
  }
}

function mockWorkspaces(...workspaces: Record<string, unknown>[]) {
  server.use(
    http.get('*/api/projects/:projectId/workspaces', () => HttpResponse.json({
      success: true,
      data: workspaces,
    })),
  )
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/demo/workspaces']}>
          <Routes>
            <Route path="/:projectName/workspaces" element={<WorkspacesPage />} />
            <Route path="/:projectName/workspaces/:name" element={<div data-testid="workspace-detail-target" />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('WorkspacesPage', () => {
  useMswServer()

  it('renders active workspaces with origin, status, bound session count, and home', async () => {
    mockWorkspaces(
      baseWorkspace({ name: 'pay-refactor', origin: { kind: 'manual' }, home: { runnerId: 'runner-a', path: '/ws/pay' }, boundSessionCount: 2 }),
      baseWorkspace({ name: 'issue-14', origin: { kind: 'issue', issueNumber: 14 }, boundSessionCount: 0 }),
    )

    renderPage()

    const card = await screen.findByTestId('workspace-card-pay-refactor')
    expect(card).toBeInTheDocument()
    expect(screen.getByTestId('workspace-card-issue-14')).toBeInTheDocument()
    expect(screen.getByTestId('workspace-section-active')).toHaveTextContent('Active (2)')
    expect(screen.getAllByTestId('workspace-name')).toHaveLength(2)
    expect(within(card).getByTestId('workspace-name')).toHaveTextContent('pay-refactor')
    expect(screen.getByText('Issue #14')).toBeInTheDocument()
    expect(screen.getByText('Manual')).toBeInTheDocument()
    expect(within(card).getByTestId('workspace-bound-sessions')).toHaveTextContent('2 bound sessions')
    expect(within(card).getByTestId('workspace-home')).toHaveTextContent('runner-a')
    expect(within(card).getByTestId('workspace-home')).toHaveTextContent('/ws/pay')
    expect(screen.getByText('Not materialized')).toBeInTheDocument()
    expect(within(card).getByTestId('workspace-created-at')).toHaveTextContent('2026-01-01')
  })

  it('shows archived workspaces in a collapsed section that expands', async () => {
    mockWorkspaces(
      baseWorkspace({ name: 'pay-refactor', status: 'active' }),
      baseWorkspace({ name: 'old-project', status: 'archived', archivedAt: '2026-02-01T00:00:00Z' }),
    )

    renderPage()

    expect(await screen.findByTestId('workspace-section-archived')).toHaveTextContent('Archived (1)')
    expect(screen.queryByTestId('workspace-card-old-project')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('workspace-section-archived-toggle'))

    expect(await screen.findByTestId('workspace-card-old-project')).toBeInTheDocument()
    expect(screen.getByText('Archived')).toBeInTheDocument()
    expect(screen.getByTestId('workspace-archived-at')).toHaveTextContent('2026-02-01')
  })

  it('navigates to the workspace detail page on card click', async () => {
    mockWorkspaces(baseWorkspace({ name: 'pay-refactor' }))

    renderPage()

    fireEvent.click(await screen.findByTestId('workspace-card-pay-refactor'))
    expect(await screen.findByTestId('workspace-detail-target')).toBeInTheDocument()
  })
})
