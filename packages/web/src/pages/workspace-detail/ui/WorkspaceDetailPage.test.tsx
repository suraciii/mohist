import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { WorkspaceDetailPage } from './WorkspaceDetailPage'

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
    home: { runnerId: 'runner-a', path: '/ws/pay' },
    createdAt: '2026-01-01T00:00:00Z',
    archivedAt: null,
    boundSessionCount: 1,
    sessions: [
      {
        id: 'session-1',
        source: 'agent-launch',
        runtimeSessionId: 'runtime-1',
        runtime: 'opencode',
        activity: 'active',
        createdAt: '2026-01-01T01:00:00Z',
        lastActivityAt: '2026-01-01T02:00:00Z',
        model: 'model-x',
        agentId: 'agent-1',
        agentName: 'Reviewer',
      },
    ],
    ...overrides,
  }
}

let workspaceData: Record<string, unknown> = {}

function mockWorkspace(workspace: Record<string, unknown>) {
  workspaceData = workspace
  server.use(
    http.get('*/api/projects/:projectId/workspaces/:name', () => HttpResponse.json({
      success: true,
      data: workspaceData,
    })),
  )
}

function mockClose(response: { status?: number; body: Record<string, unknown> }) {
  server.use(
    http.post('*/api/projects/:projectId/workspaces/:name/close', () => {
      const archived = response.body.success ? { ...workspaceData, status: 'archived', archivedAt: '2026-01-02T00:00:00Z' } : workspaceData
      if (response.body.success) workspaceData = archived
      return HttpResponse.json(response.body.success ? { success: true, data: archived } : response.body, { status: response.status ?? 200 })
    }),
  )
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/demo/workspaces/pay-refactor']}>
          <Routes>
            <Route path="/:projectName/workspaces" element={<div data-testid="workspaces-target" />} />
            <Route path="/:projectName/workspaces/:name" element={<WorkspaceDetailPage />} />
            <Route path="/:projectName/sessions/:sessionId" element={<div data-testid="session-target" />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('WorkspaceDetailPage', () => {
  useMswServer()

  it('renders repositories, home, created/archived times, and bound sessions that link to session detail', async () => {
    mockWorkspace(baseWorkspace())

    renderPage()

    expect(await screen.findByTestId('workspace-detail-name')).toHaveTextContent('pay-refactor')
    expect(screen.getByTestId('workspace-detail-home')).toHaveTextContent('runner-a')
    expect(screen.getByTestId('workspace-detail-home')).toHaveTextContent('/ws/pay')
    expect(screen.getAllByTestId('workspace-repository').map(node => node.textContent)).toEqual(['server', 'web'])
    expect(screen.getByTestId('workspace-detail-created-at')).toHaveTextContent('2026-01-01')

    const sessionLink = screen.getByTestId('workspace-session-session-1')
    expect(sessionLink).toHaveTextContent('Reviewer')
    expect(sessionLink).toHaveTextContent('active · model-x')

    fireEvent.click(sessionLink)
    expect(await screen.findByTestId('session-target')).toBeInTheDocument()
  })

  it('archives the workspace after confirming close', async () => {
    mockWorkspace(baseWorkspace())
    mockClose({ body: { success: true, data: baseWorkspace({ status: 'archived', archivedAt: '2026-01-02T00:00:00Z' }) } })

    renderPage()

    const closeTrigger = await screen.findByTestId('workspace-close-trigger')
    fireEvent.click(closeTrigger)
    fireEvent.click(await screen.findByTestId('workspace-close-confirm-confirm'))

    await waitFor(() => {
      expect(screen.queryByTestId('workspace-close-trigger')).not.toBeInTheDocument()
    })
    expect(screen.getByTestId('workspace-detail-archived-at')).toHaveTextContent('2026-01-02')
  })

  it('shows the rejection with next step when close is refused due to active bound sessions', async () => {
    mockWorkspace(baseWorkspace())
    mockClose({
      status: 409,
      body: {
        success: false,
        error: "Workspace 'pay-refactor' has 2 active bound session(s).",
        code: 'workspace_has_active_sessions',
        details: { hint: 'Stop or wait for the bound sessions to finish, then retry. List them with \'mo session list --workspace <name>\'.' },
      },
    })

    renderPage()

    fireEvent.click(await screen.findByTestId('workspace-close-trigger'))
    fireEvent.click(await screen.findByTestId('workspace-close-confirm-confirm'))

    const error = await screen.findByTestId('workspace-close-error')
    expect(error).toHaveTextContent("Workspace 'pay-refactor' has 2 active bound session(s).")
    expect(screen.getByTestId('workspace-close-error-hint')).toHaveTextContent('Stop or wait for the bound sessions to finish, then retry')
    expect(screen.getByTestId('workspace-close-trigger')).toBeInTheDocument()
  })

  it('renders an archived workspace without a close action', async () => {
    mockWorkspace(baseWorkspace({ status: 'archived', archivedAt: '2026-01-02T00:00:00Z' }))

    renderPage()

    expect(await screen.findByTestId('workspace-detail-name')).toHaveTextContent('pay-refactor')
    expect(screen.queryByTestId('workspace-close-trigger')).not.toBeInTheDocument()
    expect(screen.getByTestId('workspace-detail-archived-at')).toHaveTextContent('2026-01-02')
  })
})
