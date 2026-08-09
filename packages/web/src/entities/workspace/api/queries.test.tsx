import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { server, useMswServer } from '../../../../tests/support/msw'
import { ProjectProvider } from '../../project'
import { toast } from 'sonner'
import { closeWorkspaceMutationOptions, createWorkspaceMutationOptions, useWorkspaces } from './queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

function WorkspaceQueryProbe() {
  const { data, isError, refetch } = useWorkspaces('active')
  return (
    <>
      {isError && <span data-testid="workspace-query-error">Workspace query failed.</span>}
      {data && <span data-testid="workspace-query-loaded">{data[0]?.name ?? 'empty'}</span>}
      <button type="button" onClick={() => refetch()} data-testid="workspace-query-retry">Retry</button>
    </>
  )
}

function renderWorkspaceQuery() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ProjectProvider
        initialProjectId="proj-1"
        initialProjects={[{
          id: 'proj-1',
          name: 'Test',
          repositories: [],
          createdAt: '2026-01-01T00:00:00.000Z',
          updatedAt: '2026-01-01T00:00:00.000Z',
        }]}
      >
        <WorkspaceQueryProbe />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('useWorkspaces', () => {
  it('surfaces a 500 as an error and refetches successfully', async () => {
    let calls = 0
    server.use(
      http.get('*/api/projects/:projectId/workspaces', () => {
        calls += 1
        return calls === 1
          ? HttpResponse.json({ success: false, error: 'workspace query failed' }, { status: 500 })
          : HttpResponse.json({
            success: true,
            data: [{
              projectId: 'proj-1',
              name: 'workspace-1',
              origin: { kind: 'manual' },
              repositories: ['main'],
              status: 'active',
              home: null,
              createdAt: '2026-01-01T00:00:00.000Z',
              boundSessionCount: 0,
            }],
          })
      }),
    )

    renderWorkspaceQuery()

    expect(await screen.findByTestId('workspace-query-error')).toBeInTheDocument()
    fireEvent.click(screen.getByTestId('workspace-query-retry'))
    expect(await screen.findByTestId('workspace-query-loaded')).toHaveTextContent('workspace-1')
    expect(calls).toBe(2)
  })
})

describe('closeWorkspaceMutationOptions', () => {
  it('posts to the workspace close route with the project id', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.post('*/api/projects/:projectId/workspaces/:name/close', ({ request, params }) => {
        captured.push({
          url: new URL(request.url).pathname,
          method: request.method,
        })
        return HttpResponse.json({
          success: true,
          data: { name: params.name, status: 'archived', archivedAt: '2026-01-02T00:00:00Z' },
        })
      }),
    )

    const result = await closeWorkspaceMutationOptions('proj-1', createInvalidationClient()).mutationFn('pay-refactor')

    expect(captured).toEqual([{ url: '/api/projects/proj-1/workspaces/pay-refactor/close', method: 'POST' }])
    expect(result).toMatchObject({ name: 'pay-refactor', status: 'archived' })
  })

  it('throws when projectId is null (projectApiPath requires a project)', () => {
    const options = closeWorkspaceMutationOptions(null, createInvalidationClient())
    expect(() => options.mutationFn('pay-refactor')).toThrow('Project is required')
  })

  it('invalidates the workspace query family and toasts on success', () => {
    const qc = createInvalidationClient()
    closeWorkspaceMutationOptions('proj-1', qc).onSuccess()

    const invalidatedKeys = qc.invalidateQueries.mock.calls.map((call) => call[0].queryKey)
    expect(invalidatedKeys).toEqual([['workspaces']])
    expect(toast.success).toHaveBeenCalledWith('Workspace archived')
  })
})

describe('createWorkspaceMutationOptions', () => {
  it('posts the explicit name and repository membership', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.post('*/api/projects/:projectId/workspaces', async ({ request }) => {
        captured.push({
          url: new URL(request.url).pathname,
          method: request.method,
          body: await request.json(),
        })
        return HttpResponse.json({ success: true, data: { projectId: 'proj-1', name: 'manual', repositories: ['main'] } })
      }),
    )

    const result = await createWorkspaceMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      name: 'manual',
      repos: ['main'],
    })

    expect(captured).toEqual([{
      url: '/api/projects/proj-1/workspaces',
      method: 'POST',
      body: { name: 'manual', repos: ['main'] },
    }])
    expect(result).toMatchObject({ name: 'manual', repositories: ['main'] })
  })

  it('fails closed without a project id and invalidates workspaces after creation', async () => {
    const missingProject = createWorkspaceMutationOptions(null, createInvalidationClient())
    expect(() => missingProject.mutationFn({ name: 'manual', repos: [] })).toThrow('Project is required')

    const queryClient = createInvalidationClient()
    createWorkspaceMutationOptions('proj-1', queryClient).onSuccess()
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['workspaces'] })
  })
})
