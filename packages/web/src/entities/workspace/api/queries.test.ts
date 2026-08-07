import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import { closeWorkspaceMutationOptions } from './queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

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
