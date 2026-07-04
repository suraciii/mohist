import { afterEach, describe, expect, it, vi } from 'vitest'
import { createIssue } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('createIssue api client', () => {
  it('includes risk in the request payload when provided', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: true,
      data: {
        id: 'issue_1',
        number: 1,
        title: 'Risked',
        body: null,
        status: 'backlog',
        health: 'active',
        projectId: 'proj_1',
        labels: {},
        createdAt: '2026-06-16T00:00:00.000Z',
        updatedAt: '2026-06-16T00:00:00.000Z',
        risk: 'high',
      },
    }, { status: 201 }))
    vi.stubGlobal('fetch', fetchMock)

    await createIssue({ title: 'Risked', risk: 'high', projectId: 'proj_1' })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const callArgs = fetchMock.mock.calls[0]
    const [url, init] = callArgs as unknown as [string, RequestInit]
    expect(url).toBe('/api/projects/proj_1/issues')
    expect(init.method).toBe('POST')
    const body = JSON.parse(init.body as string)
    expect(body).toEqual({ title: 'Risked', risk: 'high' })
  })

  it('omits risk when not provided', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: true,
      data: { id: 'issue_1', number: 1, title: 'No risk' },
    }, { status: 201 }))
    vi.stubGlobal('fetch', fetchMock)

    await createIssue({ title: 'No risk', projectId: 'proj_1' })

    const callArgs = fetchMock.mock.calls[0]
    const [, init] = callArgs as unknown as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body).toEqual({ title: 'No risk' })
    expect(body).not.toHaveProperty('risk')
  })

  it('accepts null risk and sends it in the payload', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: true,
      data: { id: 'issue_1', number: 1, title: 'Null risk' },
    }, { status: 201 }))
    vi.stubGlobal('fetch', fetchMock)

    await createIssue({ title: 'Null risk', risk: null, projectId: 'proj_1' })

    const callArgs = fetchMock.mock.calls[0]
    const [, init] = callArgs as unknown as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body).toEqual({ title: 'Null risk', risk: null })
  })

  it('includes workflowProfileId in the request payload when provided', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: true,
      data: {
        id: 'issue_1',
        number: 1,
        title: 'Profiled',
        body: null,
        status: 'backlog',
        health: 'active',
        projectId: 'proj_1',
        labels: {},
        createdAt: '2026-06-16T00:00:00.000Z',
        updatedAt: '2026-06-16T00:00:00.000Z',
        workflowProfileId: 'feature-flow',
      },
    }, { status: 201 }))
    vi.stubGlobal('fetch', fetchMock)

    await createIssue({ title: 'Profiled', workflowProfileId: 'feature-flow', projectId: 'proj_1' })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const callArgs = fetchMock.mock.calls[0]
    const [url, init] = callArgs as unknown as [string, RequestInit]
    expect(url).toBe('/api/projects/proj_1/issues')
    expect(init.method).toBe('POST')
    const body = JSON.parse(init.body as string)
    expect(body).toEqual({ title: 'Profiled', workflowProfileId: 'feature-flow' })
  })

  it('omits workflowProfileId when not provided', async () => {
    const fetchMock = vi.fn(async () => Response.json({
      success: true,
      data: { id: 'issue_1', number: 1, title: 'No profile' },
    }, { status: 201 }))
    vi.stubGlobal('fetch', fetchMock)

    await createIssue({ title: 'No profile', projectId: 'proj_1' })

    const callArgs = fetchMock.mock.calls[0]
    const [, init] = callArgs as unknown as [string, RequestInit]
    const body = JSON.parse(init.body as string)
    expect(body).toEqual({ title: 'No profile' })
    expect(body).not.toHaveProperty('workflowProfileId')
  })
})
