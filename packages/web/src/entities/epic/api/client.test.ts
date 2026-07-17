import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getEpicEvents, getEpics } from './client'

useMswServer()

function successResponse(payload: unknown) {
  return HttpResponse.json({ success: true, data: payload })
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

function recordEpicListRequest(payload: unknown = []) {
  const requests: Request[] = []
  server.use(
    http.get('*/api/projects/:projectId/epics', ({ request }) => {
      requests.push(request)
      return successResponse(payload)
    }),
  )
  return requests
}

describe('getEpics query-string forwarding', () => {
  it('requests GET /api/projects/{ref}/epics with no query string when params are omitted', async () => {
    const requests = recordEpicListRequest()

    const list = await getEpics({ projectId: 'proj-1' })

    expect(list).toEqual([])
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/epics')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('appends ?search={term} when search is provided', async () => {
    const requests = recordEpicListRequest()

    await getEpics({ projectId: 'proj-1', search: 'auth' })

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/epics?search=auth')
  })

  it('appends ?sort=...&dir=... when sort fields are provided', async () => {
    const requests = recordEpicListRequest()

    await getEpics({ projectId: 'proj-1', sort: 'priority', dir: 'asc' })

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/epics?sort=priority&dir=asc')
  })

  it('combines search + sort + dir into a single querystring', async () => {
    const requests = recordEpicListRequest()

    await getEpics({ projectId: 'proj-1', search: 'auth', sort: 'updated', dir: 'desc' })

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/epics?search=auth&sort=updated&dir=desc')
  })

  it('skips empty search and default sort/dir keys so the URL stays minimal', async () => {
    const requests = recordEpicListRequest()

    await getEpics({ projectId: 'proj-1', search: '' })

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/epics')
  })

  it('returns the parsed epics array on a 200 success envelope', async () => {
    const payload = [
      {
        projectId: 'proj-1',
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
    recordEpicListRequest(payload)

    const result = await getEpics({ projectId: 'proj-1', search: 'auth' })

    expect(result).toHaveLength(1)
    expect(result[0].number).toBe(1)
  })
})

describe('getEpicEvents', () => {
  it('requests GET /api/projects/{ref}/epics/{number}/events', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/epics/:number/events', ({ request }) => {
        requests.push(request)
        return successResponse([])
      }),
    )

    await getEpicEvents(1, 'proj-1')

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/epics/1/events')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('returns the parsed events array on a 200 success envelope', async () => {
    const payload = [
      {
        id: 1,
        eventId: 'evt-1',
        source: '/mohist/projects/proj-1/epics/1',
        type: 'com.mohist.epic.created',
        specVersion: '1.0',
        subject: '1',
        time: '2026-06-30T12:00:00+00:00',
        dataContentType: 'application/json',
        data: { title: 'Auth epic', description: 'desc', priority: 'p2' },
        extensions: { projectid: 'proj-1', epic: '1' },
      },
    ]
    server.use(
      http.get('*/api/projects/:projectId/epics/:number/events', () => successResponse(payload)),
    )

    const result = await getEpicEvents(1, 'proj-1')

    expect(result).toHaveLength(1)
    expect(result[0].type).toBe('com.mohist.epic.created')
    expect(result[0].data).toMatchObject({ title: 'Auth epic', priority: 'p2' })
  })
})
