import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  createLabelDefinition,
  deleteLabelDefinition,
  getLabelCatalog,
  isValidLabelKey,
  LABEL_KEY_PATTERN,
  updateLabelDefinition,
} from './client'
import { ApiError } from '../../../shared/api/client'

useMswServer()

interface CapturedRequest {
  path: string
  method: string
  contentType: string | null
  body?: unknown
}

async function captureRequest(request: Request, requests: CapturedRequest[]) {
  const url = new URL(request.url)
  const captured: CapturedRequest = {
    path: `${url.pathname}${url.search}`,
    method: request.method,
    contentType: request.headers.get('content-type'),
  }
  if (request.method !== 'GET' && request.method !== 'DELETE') {
    captured.body = await request.json()
  }
  requests.push(captured)
}

function successResponse(payload: unknown, status = 200) {
  return HttpResponse.json({ success: true, data: payload }, { status })
}

function errorResponse(message: string, status: number, code = 'error') {
  return HttpResponse.json({ success: false, error: message, code }, { status })
}

describe('isValidLabelKey', () => {
  it('accepts simple lowercase keys', () => {
    expect(isValidLabelKey('module')).toBe(true)
    expect(isValidLabelKey('a')).toBe(true)
    expect(isValidLabelKey('a1')).toBe(true)
  })

  it('accepts keys with interior dashes', () => {
    expect(isValidLabelKey('a-b')).toBe(true)
    expect(isValidLabelKey('refactor-target')).toBe(true)
  })

  it('rejects uppercase, leading dash, trailing dash, and empty keys', () => {
    expect(isValidLabelKey('Module')).toBe(false)
    expect(isValidLabelKey('-mod')).toBe(false)
    expect(isValidLabelKey('mod-')).toBe(false)
    expect(isValidLabelKey('')).toBe(false)
    expect(isValidLabelKey('mod_b')).toBe(false)
  })

  it('exposes the canonical pattern', () => {
    expect(LABEL_KEY_PATTERN.source).toBe('^[a-z0-9]([-a-z0-9]*[a-z0-9])?$')
  })
})

describe('getLabelCatalog', () => {
  it('GETs /api/projects/{id}/labels/catalog and returns the list', async () => {
    const definitions = [
      { key: 'module', description: 'subsystem' },
      { key: 'kind', description: 'change type' },
    ]
    const requests: CapturedRequest[] = []
    server.use(
      http.get('*/api/projects/:projectId/labels/catalog', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse(definitions)
      }),
    )

    const result = await getLabelCatalog('proj-1')

    expect(result).toEqual(definitions)
    expect(requests).toEqual([{
      path: '/api/projects/proj-1/labels/catalog',
      method: 'GET',
      contentType: 'application/json',
    }])
  })

  it('throws when projectId is missing', () => {
    expect(() => getLabelCatalog(null)).toThrow(ApiError)
  })

  it('throws synchronously when projectId is empty string', () => {
    expect(() => getLabelCatalog('')).toThrow(ApiError)
  })
})

describe('createLabelDefinition', () => {
  it('POSTs key/description/supportedValues to the catalog endpoint', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.post('*/api/projects/:projectId/labels/catalog', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse({ key: 'module', description: 'subsystem' }, 201)
      }),
    )

    await createLabelDefinition('proj-1', {
      key: 'module',
      description: 'subsystem',
      supportedValues: ['auth', 'ui'],
    })

    expect(requests).toEqual([{
      path: '/api/projects/proj-1/labels/catalog',
      method: 'POST',
      contentType: 'application/json',
      body: { key: 'module', description: 'subsystem', supportedValues: ['auth', 'ui'] },
    }])
  })

  it('omits supportedValues when not provided', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.post('*/api/projects/:projectId/labels/catalog', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse({ key: 'freeform', description: 'no values' }, 201)
      }),
    )

    await createLabelDefinition('proj-1', { key: 'freeform', description: 'no values' })

    expect(requests).toEqual([{
      path: '/api/projects/proj-1/labels/catalog',
      method: 'POST',
      contentType: 'application/json',
      body: { key: 'freeform', description: 'no values' },
    }])
  })

  it('surfaces server error messages on conflict', async () => {
    server.use(
      http.post('*/api/projects/:projectId/labels/catalog', () => errorResponse("Key 'module' already exists", 409, 'conflict')),
    )

    await expect(
      createLabelDefinition('proj-1', { key: 'module', description: 'x' }),
    ).rejects.toThrow("Key 'module' already exists")
  })
})

describe('updateLabelDefinition', () => {
  it('PATCHes only the description field when supportedValues is omitted', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.patch('*/api/projects/:projectId/labels/catalog/:key', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse({ key: 'module', description: 'new', supportedValues: ['auth'] })
      }),
    )

    await updateLabelDefinition('proj-1', 'module', { description: 'new' })

    expect(requests).toEqual([{
      path: '/api/projects/proj-1/labels/catalog/module',
      method: 'PATCH',
      contentType: 'application/json',
      body: { description: 'new' },
    }])
  })

  it('PATCHes only supportedValues when description is omitted', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.patch('*/api/projects/:projectId/labels/catalog/:key', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse({ key: 'module', description: 'same', supportedValues: ['a', 'b'] })
      }),
    )

    await updateLabelDefinition('proj-1', 'module', { supportedValues: ['a', 'b'] })

    expect(requests[0]?.body).toEqual({ supportedValues: ['a', 'b'] })
  })

  it('PATCHes both fields when both are provided', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.patch('*/api/projects/:projectId/labels/catalog/:key', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse({ key: 'module', description: 'd', supportedValues: ['v'] })
      }),
    )

    await updateLabelDefinition('proj-1', 'module', {
      description: 'd',
      supportedValues: ['v'],
    })

    expect(requests[0]?.body).toEqual({ description: 'd', supportedValues: ['v'] })
  })

  it('encodes the key segment', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.patch('*/api/projects/:projectId/labels/catalog/:key', async ({ request }) => {
        await captureRequest(request, requests)
        return successResponse({ key: 'a/b', description: 'x' })
      }),
    )

    await updateLabelDefinition('proj-1', 'a/b', { description: 'x' })

    expect(requests[0]?.path).toBe('/api/projects/proj-1/labels/catalog/a%2Fb')
  })

  it('surfaces the server-provided error on 404', async () => {
    server.use(
      http.patch('*/api/projects/:projectId/labels/catalog/:key', () => errorResponse("Key 'missing' not found", 404, 'not_found')),
    )

    await expect(
      updateLabelDefinition('proj-1', 'missing', { description: 'x' }),
    ).rejects.toThrow('not found')
  })
})

describe('deleteLabelDefinition', () => {
  it('treats HTTP 204 as success', async () => {
    const requests: CapturedRequest[] = []
    server.use(
      http.delete('*/api/projects/:projectId/labels/catalog/:key', async ({ request }) => {
        await captureRequest(request, requests)
        return new HttpResponse(null, { status: 204 })
      }),
    )

    await expect(deleteLabelDefinition('proj-1', 'module')).resolves.toBeUndefined()

    expect(requests).toEqual([{
      path: '/api/projects/proj-1/labels/catalog/module',
      method: 'DELETE',
      contentType: 'application/json',
    }])
  })

  it('surfaces the server-provided error on 404', async () => {
    server.use(
      http.delete('*/api/projects/:projectId/labels/catalog/:key', () => errorResponse("Key 'missing' not found", 404, 'not_found')),
    )

    await expect(deleteLabelDefinition('proj-1', 'missing')).rejects.toThrow(
      'not found',
    )
  })
})
