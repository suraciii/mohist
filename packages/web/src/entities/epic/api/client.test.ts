import { afterEach, describe, expect, it, vi } from 'vitest'
import { getEpics } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function mockJsonResponse(payload: unknown): Response {
  return new Response(JSON.stringify({ success: true, data: payload }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('getEpics query-string forwarding', () => {
  it('requests GET /api/projects/{ref}/epics with no query string when params are omitted', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    const list = await getEpics({ projectId: 'proj-1' })

    expect(list).toEqual([])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/epics')
    expect(calledInit?.method).toBeUndefined()
  })

  it('appends ?search={term} when search is provided', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    await getEpics({ projectId: 'proj-1', search: 'auth' })

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/epics?search=auth')
  })

  it('appends ?sort=...&dir=... when sort fields are provided', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    await getEpics({ projectId: 'proj-1', sort: 'priority', dir: 'asc' })

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/epics?sort=priority&dir=asc')
  })

  it('combines search + sort + dir into a single querystring', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    await getEpics({ projectId: 'proj-1', search: 'auth', sort: 'updated', dir: 'desc' })

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/epics?search=auth&sort=updated&dir=desc')
  })

  it('skips empty search and default sort/dir keys so the URL stays minimal', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    await getEpics({ projectId: 'proj-1', search: '' })

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/epics')
  })

  it('returns the parsed epics array on a 200 success envelope', async () => {
    const payload = [
      {
        id: 'epic-1',
        number: 1,
        title: 'Auth migration',
        description: 'desc',
        priority: 'p2',
        status: 'idle',
        createdAt: '2026-01-01T00:00:00Z',
        updatedAt: '2026-01-01T00:00:00Z',
        progress: {
          deliveredCount: 0,
          totalIssueCount: 0,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      },
    ]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse(payload))
    vi.stubGlobal('fetch', fetchMock)

    const result = await getEpics({ projectId: 'proj-1', search: 'auth' })

    expect(result).toHaveLength(1)
    expect(result[0].id).toBe('epic-1')
  })
})
