import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { createIssue, getIssueEvents, getLabels, updateIssue } from './client'

useMswServer()

function successResponse(payload: unknown) {
  return HttpResponse.json({ success: true, data: payload })
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

function issueResponse(labels: Record<string, string>) {
  return {
    number: 1,
    title: 'T',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels,
    createdAt: '2026-06-19T00:00:00.000Z',
    updatedAt: '2026-06-19T00:00:00.000Z',
  }
}

describe('getIssueEvents', () => {
  it('requests GET /api/projects/{ref}/issues/{number}/events', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/issues/:number/events', ({ request }) => {
        requests.push(request)
        return successResponse([])
      }),
    )

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/42/events')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
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
        data: { issueNumber: 42, },
        extensions: {},
      },
    ]
    server.use(
      http.get('*/api/projects/:projectId/issues/:number/events', () => successResponse(stored)),
    )

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual(stored)
  })

  it('returns an empty array when the server sends an empty list', async () => {
    server.use(
      http.get('*/api/projects/:projectId/issues/:number/events', () => successResponse([])),
    )

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
  })
})

describe('getLabels', () => {
  it('requests GET /api/projects/{ref}/labels and returns distinct keys', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/labels', ({ request }) => {
        requests.push(request)
        return successResponse(['stream', 'module'])
      }),
    )

    const keys = await getLabels('proj-1')

    expect(keys).toEqual(['stream', 'module'])
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/labels')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('returns an empty array when the project has no labels', async () => {
    server.use(
      http.get('*/api/projects/:projectId/labels', () => successResponse([])),
    )

    const keys = await getLabels('proj-empty')

    expect(keys).toEqual([])
  })
})

describe('createIssue / updateIssue with key-value labels', () => {
  it('createIssue POSTs title and key-value labels object', async () => {
    const requests: Request[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues', ({ request }) => {
        requests.push(request)
        return successResponse(issueResponse({ stream: 'frontend' }))
      }),
    )

    await createIssue({
      title: 'T',
      labels: { stream: 'frontend', module: 'auth' },
      projectId: 'proj-1',
    })

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    await expect(requests[0].json()).resolves.toEqual({
      title: 'T',
      labels: { stream: 'frontend', module: 'auth' },
    })
  })

  it('updateIssue PATCHes the full labels map (replacement)', async () => {
    const requests: Request[] = []
    server.use(
      http.patch('*/api/projects/:projectId/issues/:number', ({ request }) => {
        requests.push(request)
        return successResponse(issueResponse({ module: 'auth' }))
      }),
    )

    await updateIssue(1, { labels: { module: 'auth' } }, 'proj-1')

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/1')
    expect(requests[0].method).toBe('PATCH')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    await expect(requests[0].json()).resolves.toEqual({
      labels: { module: 'auth' },
    })
  })
})
