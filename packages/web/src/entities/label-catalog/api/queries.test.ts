import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  catalogQueryKey,
  createLabelDefinitionMutationOptions,
  deleteLabelDefinitionMutationOptions,
  labelCatalogQueryOptions,
  updateLabelDefinitionMutationOptions,
} from './queries'

const CATALOG_DTO = [
  { key: 'module', description: 'subsystem', supportedValues: null },
]

function recordCatalogRequests() {
  const requests: { method: string; url: string; body: unknown }[] = []
  server.use(
    http.get('*/api/projects/:projectId/labels/catalog', ({ request }) => {
      requests.push({ method: request.method, url: request.url, body: null })
      return HttpResponse.json({ success: true, data: CATALOG_DTO })
    }),
    http.post('*/api/projects/:projectId/labels/catalog', async ({ request }) => {
      requests.push({
        method: request.method,
        url: request.url,
        body: await request.json(),
      })
      return HttpResponse.json({
        success: true,
        data: { key: 'module', description: 'subsystem', supportedValues: null },
      })
    }),
    http.patch('*/api/projects/:projectId/labels/catalog/:key', async ({ request }) => {
      requests.push({
        method: request.method,
        url: request.url,
        body: await request.json(),
      })
      return HttpResponse.json({
        success: true,
        data: { key: 'module', description: 'new', supportedValues: null },
      })
    }),
    http.delete('*/api/projects/:projectId/labels/catalog/:key', ({ request }) => {
      requests.push({ method: request.method, url: request.url, body: null })
      return new HttpResponse(null, { status: 204 })
    }),
  )
  return requests
}

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

describe('catalogQueryKey', () => {
  it('scopes the key to the project', () => {
    expect(catalogQueryKey('proj-1')).toEqual(['label-catalog', 'proj-1'])
  })
})

describe('labelCatalogQueryOptions', () => {
  it('uses a project-scoped query key', () => {
    expect(labelCatalogQueryOptions('proj-1').queryKey).toEqual(['label-catalog', 'proj-1'])
  })

  it('fetches the project labels catalog endpoint', async () => {
    const requests = recordCatalogRequests()

    const data = await labelCatalogQueryOptions('proj-1').queryFn()

    expect(requests.map((r) => r.method + ' ' + new URL(r.url).pathname)).toEqual([
      'GET /api/projects/proj-1/labels/catalog',
    ])
    expect(data).toEqual(CATALOG_DTO)
  })

  it('is enabled when projectId is present', () => {
    expect(labelCatalogQueryOptions('proj-1').enabled).toBe(true)
  })

  it('is disabled when projectId is missing', () => {
    expect(labelCatalogQueryOptions(null).enabled).toBe(false)
    expect(labelCatalogQueryOptions(undefined).enabled).toBe(false)
  })
})

describe('createLabelDefinitionMutationOptions', () => {
  it('POSTs the label input to the project catalog endpoint', async () => {
    const requests = recordCatalogRequests()

    await createLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      key: 'module',
      description: 'subsystem',
    })

    expect(requests).toHaveLength(1)
    expect(requests[0].method).toBe('POST')
    expect(new URL(requests[0].url).pathname).toBe('/api/projects/proj-1/labels/catalog')
    expect(requests[0].body).toEqual({ key: 'module', description: 'subsystem' })
  })

  it('rejects when projectId is missing', () => {
    const options = createLabelDefinitionMutationOptions(null, createInvalidationClient())
    expect(() => options.mutationFn({ key: 'module', description: 'x' })).toThrow(
      'Project is required',
    )
  })

  it('invalidates the catalog query on success', () => {
    const qc = createInvalidationClient()
    createLabelDefinitionMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['label-catalog', 'proj-1'],
    })
  })

  it('toasts "Label definition added" on success', () => {
    createLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Label definition added')
  })

  it('toasts the error message on failure', () => {
    createLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onError(
      new Error('duplicate key'),
    )
    expect(toast.error).toHaveBeenCalledWith('duplicate key')
  })

  it('falls back to "Failed to add label definition" on empty error message', () => {
    createLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onError(
      new Error(''),
    )
    expect(toast.error).toHaveBeenCalledWith('Failed to add label definition')
  })
})

describe('updateLabelDefinitionMutationOptions', () => {
  it('PATCHes the key + patch to the project catalog endpoint', async () => {
    const requests = recordCatalogRequests()

    await updateLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      key: 'module',
      patch: { description: 'new' },
    })

    expect(requests).toHaveLength(1)
    expect(requests[0].method).toBe('PATCH')
    expect(new URL(requests[0].url).pathname).toBe('/api/projects/proj-1/labels/catalog/module')
    expect(requests[0].body).toEqual({ description: 'new' })
  })

  it('invalidates the catalog query on success', () => {
    const qc = createInvalidationClient()
    updateLabelDefinitionMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['label-catalog', 'proj-1'],
    })
  })

  it('toasts "Label definition updated" on success', () => {
    updateLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Label definition updated')
  })

  it('falls back to "Failed to update label definition" on empty error message', () => {
    updateLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onError(
      new Error(''),
    )
    expect(toast.error).toHaveBeenCalledWith('Failed to update label definition')
  })
})

describe('deleteLabelDefinitionMutationOptions', () => {
  it('DELETEs the key from the project catalog endpoint', async () => {
    const requests = recordCatalogRequests()

    await deleteLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).mutationFn(
      'module',
    )

    expect(requests).toHaveLength(1)
    expect(requests[0].method).toBe('DELETE')
    expect(new URL(requests[0].url).pathname).toBe('/api/projects/proj-1/labels/catalog/module')
  })

  it('rejects when projectId is missing', () => {
    const options = deleteLabelDefinitionMutationOptions(null, createInvalidationClient())
    expect(() => options.mutationFn('module')).toThrow('Project is required')
  })

  it('invalidates the catalog query on success', () => {
    const qc = createInvalidationClient()
    deleteLabelDefinitionMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['label-catalog', 'proj-1'],
    })
  })

  it('toasts "Label definition removed" on success', () => {
    deleteLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Label definition removed')
  })

  it('falls back to "Failed to remove label definition" on empty error message', () => {
    deleteLabelDefinitionMutationOptions('proj-1', createInvalidationClient()).onError(
      new Error(''),
    )
    expect(toast.error).toHaveBeenCalledWith('Failed to remove label definition')
  })
})
