import { afterEach, describe, expect, it, vi } from 'vitest'
import { createIssue, getIssueEvents, getLabels, updateIssue } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function mockJsonResponse(payload: unknown, status: number = 200): Response {
  return new Response(JSON.stringify({ success: true, data: payload }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('getIssueEvents', () => {
  it('requests GET /api/projects/{ref}/issues/{number}/events', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/42/events')
    expect(calledInit?.method).toBeUndefined()
  })

  it('returns the stored cloud events payload', async () => {
    const stored = [
      {
        id: 1,
        eventId: 'evt-1',
        source: '/mohist/test',
        type: 'com.mohist.workflow.run.started',
        specVersion: '1.0',
        subject: null,
        time: '2026-06-18T00:00:00.0000000Z',
        dataContentType: 'application/json',
        data: { issueNumber: 42 },
        extensions: {},
      },
    ]
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse(stored)))

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual(stored)
  })

  it('returns an empty array when the server sends an empty list', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse([])))

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
  })
})

describe('getLabels', () => {
  it('requests GET /api/projects/{ref}/labels and returns distinct keys', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse(['stream', 'module']))
    vi.stubGlobal('fetch', fetchMock)

    const keys = await getLabels('proj-1')

    expect(keys).toEqual(['stream', 'module'])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/labels')
  })

  it('returns an empty array when the project has no labels', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse([])))

    const keys = await getLabels('proj-empty')

    expect(keys).toEqual([])
  })
})

describe('createIssue / updateIssue with key-value labels', () => {
  it('createIssue POSTs title and key-value labels object', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      mockJsonResponse({
        id: 'issue-1',
        number: 1,
        title: 'T',
        status: 'backlog',
        health: 'active',
        projectId: 'proj-1',
        labels: { stream: 'frontend' },
        createdAt: '2026-06-19T00:00:00.000Z',
        updatedAt: '2026-06-19T00:00:00.000Z',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await createIssue({
      title: 'T',
      labels: { stream: 'frontend', module: 'auth' },
      projectId: 'proj-1',
    })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues')
    expect(calledInit?.method).toBe('POST')
    expect(JSON.parse(calledInit?.body as string)).toEqual({
      title: 'T',
      labels: { stream: 'frontend', module: 'auth' },
    })
  })

  it('updateIssue PATCHes the full labels map (replacement)', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      mockJsonResponse({
        id: 'issue-1',
        number: 1,
        title: 'T',
        status: 'backlog',
        health: 'active',
        projectId: 'proj-1',
        labels: { module: 'auth' },
        createdAt: '2026-06-19T00:00:00.000Z',
        updatedAt: '2026-06-19T00:00:00.000Z',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await updateIssue(1, { labels: { module: 'auth' } }, 'proj-1')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/1')
    expect(calledInit?.method).toBe('PATCH')
    expect(JSON.parse(calledInit?.body as string)).toEqual({
      labels: { module: 'auth' },
    })
  })
})