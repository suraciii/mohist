import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createLabelDefinition,
  deleteLabelDefinition,
  getLabelCatalog,
  isValidLabelKey,
  LABEL_KEY_PATTERN,
  updateLabelDefinition,
} from './client'
import { ApiError } from '../../../shared/api/client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function okJson<T>(payload: T, status = 200): Response {
  return new Response(JSON.stringify({ success: true, data: payload }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function errorJson(message: string, status: number, code?: string): Response {
  return new Response(
    JSON.stringify({ success: false, error: message, code: code ?? 'error' }),
    { status, headers: { 'Content-Type': 'application/json' } },
  )
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
      { key: 'module', description: 'subsystem', origin: 'user' },
      { key: 'refactor', description: 'cleanup', origin: 'system' },
    ]
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(okJson(definitions))
    vi.stubGlobal('fetch', fetchMock)

    const result = await getLabelCatalog('proj-1')

    expect(result).toEqual(definitions)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/labels/catalog')
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
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      okJson({ key: 'module', description: 'subsystem', origin: 'user' }, 201),
    )
    vi.stubGlobal('fetch', fetchMock)

    await createLabelDefinition('proj-1', {
      key: 'module',
      description: 'subsystem',
      supportedValues: ['auth', 'ui'],
    })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, init] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/labels/catalog')
    expect(init?.method).toBe('POST')
    expect(init?.body).toBe(
      JSON.stringify({ key: 'module', description: 'subsystem', supportedValues: ['auth', 'ui'] }),
    )
  })

  it('omits supportedValues when not provided', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      okJson({ key: 'freeform', description: 'no values', origin: 'user' }, 201),
    )
    vi.stubGlobal('fetch', fetchMock)

    await createLabelDefinition('proj-1', { key: 'freeform', description: 'no values' })

    const [, init] = fetchMock.mock.calls[0]
    expect(init?.body).toBe(JSON.stringify({ key: 'freeform', description: 'no values' }))
  })

  it('surfaces server error messages on conflict', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      errorJson("Key 'module' already exists", 409, 'conflict'),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      createLabelDefinition('proj-1', { key: 'module', description: 'x' }),
    ).rejects.toThrow("Key 'module' already exists")
  })
})

describe('updateLabelDefinition', () => {
  it('PATCHes only the description field when supportedValues is omitted', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      okJson({ key: 'module', description: 'new', origin: 'user', supportedValues: ['auth'] }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await updateLabelDefinition('proj-1', 'module', { description: 'new' })

    const [calledPath, init] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/labels/catalog/module')
    expect(init?.method).toBe('PATCH')
    expect(init?.body).toBe(JSON.stringify({ description: 'new' }))
  })

  it('PATCHes only supportedValues when description is omitted', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      okJson({ key: 'module', description: 'same', origin: 'user', supportedValues: ['a', 'b'] }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await updateLabelDefinition('proj-1', 'module', { supportedValues: ['a', 'b'] })

    const [, init] = fetchMock.mock.calls[0]
    expect(init?.body).toBe(JSON.stringify({ supportedValues: ['a', 'b'] }))
  })

  it('PATCHes both fields when both are provided', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      okJson({ key: 'module', description: 'd', origin: 'user', supportedValues: ['v'] }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await updateLabelDefinition('proj-1', 'module', {
      description: 'd',
      supportedValues: ['v'],
    })

    const [, init] = fetchMock.mock.calls[0]
    expect(init?.body).toBe(JSON.stringify({ description: 'd', supportedValues: ['v'] }))
  })

  it('encodes the key segment', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      okJson({ key: 'a/b', description: 'x', origin: 'user' }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await updateLabelDefinition('proj-1', 'a/b', { description: 'x' })

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/labels/catalog/a%2Fb')
  })

  it('surfaces server error messages on 404 / 409', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(errorJson("Key 'missing' not found", 404, 'not_found'))
      .mockResolvedValueOnce(
        errorJson("Definition 'refactor' is immutable", 409, 'conflict'),
      )
    vi.stubGlobal('fetch', fetchMock)

    await expect(
      updateLabelDefinition('proj-1', 'missing', { description: 'x' }),
    ).rejects.toThrow('not found')
    await expect(
      updateLabelDefinition('proj-1', 'refactor', { description: 'x' }),
    ).rejects.toThrow('immutable')
  })
})

describe('deleteLabelDefinition', () => {
  it('treats HTTP 204 as success', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(null, { status: 204 }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(deleteLabelDefinition('proj-1', 'module')).resolves.toBeUndefined()

    const [calledPath, init] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/labels/catalog/module')
    expect(init?.method).toBe('DELETE')
  })

  it('surfaces the server-provided error on 409', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      errorJson("Definition 'refactor' is immutable", 409, 'conflict'),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(deleteLabelDefinition('proj-1', 'refactor')).rejects.toThrow(
      'immutable',
    )
  })

  it('surfaces the server-provided error on 404', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      errorJson("Key 'missing' not found", 404, 'not_found'),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(deleteLabelDefinition('proj-1', 'missing')).rejects.toThrow(
      'not found',
    )
  })
})
