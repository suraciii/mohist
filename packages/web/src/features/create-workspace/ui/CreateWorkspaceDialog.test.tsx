import '@testing-library/jest-dom'
import { describe, expect, it, vi } from 'vitest'
import type { ComponentProps } from 'react'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'

import { ProjectProvider } from '@/entities/project'
import type { Project } from '@/entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { CreateWorkspaceDialog } from './CreateWorkspaceDialog'

const projects: Project[] = [{
  id: 'proj-1',
  name: 'demo',
  repositories: [],
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
}]

const repositories = [
  { name: 'main', gitUrl: 'https://example.test/main.git', baseBranch: 'main', isDefault: true },
  { name: 'web', gitUrl: 'https://example.test/web.git', baseBranch: 'main', isDefault: false },
]

function createdWorkspace(repositoriesForWorkspace: string[] = []) {
  return {
    projectId: 'proj-1',
    name: 'new-workspace',
    origin: { kind: 'manual' },
    repositories: repositoriesForWorkspace,
    status: 'active',
    home: null,
    createdAt: '2026-01-01T00:00:00Z',
    archivedAt: null,
    boundSessionCount: 0,
    sessions: null,
  }
}

function mockRepositories(data = repositories) {
  server.use(
    http.get('*/api/projects/:projectId/repositories', () => HttpResponse.json({ success: true, data })),
  )
}

function renderDialog(props: Partial<ComponentProps<typeof CreateWorkspaceDialog>> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <CreateWorkspaceDialog open onClose={vi.fn()} {...props} />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('CreateWorkspaceDialog', () => {
  useMswServer()

  it('posts the selected repository names and returns the server membership', async () => {
    mockRepositories()
    let requestBody: unknown
    server.use(
      http.post('*/api/projects/:projectId/workspaces', async ({ request }) => {
        requestBody = await request.json()
        return HttpResponse.json({ success: true, data: createdWorkspace(['main']) })
      }),
    )
    const onCreated = vi.fn()

    renderDialog({ initialRepositoryNames: [], onCreated })
    fireEvent.change(await screen.findByTestId('create-workspace-name'), { target: { value: 'new-workspace' } })
    fireEvent.click(screen.getByTestId('create-workspace-repository-main'))
    fireEvent.click(screen.getByTestId('create-workspace-submit'))

    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(expect.objectContaining({ name: 'new-workspace', repositories: ['main'] })))
    expect(requestBody).toEqual({ name: 'new-workspace', repos: ['main'] })
  })

  it('shows invalid-name and name-taken states without closing the form', async () => {
    mockRepositories([])
    server.use(
      http.post('*/api/projects/:projectId/workspaces', () => HttpResponse.json(
        { success: false, code: 'workspace_name_taken', error: 'name already exists' },
        { status: 409 },
      )),
    )

    renderDialog()
    const name = await screen.findByTestId('create-workspace-name')
    fireEvent.blur(name)
    expect(screen.getByTestId('create-workspace-name-error')).toBeInTheDocument()

    fireEvent.change(name, { target: { value: 'new-workspace' } })
    fireEvent.click(screen.getByTestId('create-workspace-submit'))
    expect(await screen.findByTestId('create-workspace-error')).toHaveTextContent('already exists')
    expect(screen.getByTestId('create-workspace-dialog')).toBeInTheDocument()
  })

  it('shows a repository loading error and keeps creation disabled', async () => {
    server.use(
      http.get('*/api/projects/:projectId/repositories', () => HttpResponse.json(
        { success: false, error: 'repository service unavailable' },
        { status: 503 },
      )),
    )

    renderDialog()
    expect(await screen.findByTestId('create-workspace-repositories-error')).toBeInTheDocument()
    expect(screen.getByTestId('create-workspace-submit')).toBeDisabled()
  })
})
