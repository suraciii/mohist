import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getIssueWorkflowTaskLog } from './client'

useMswServer()

function successResponse(payload: unknown) {
  return HttpResponse.json({ success: true, data: payload })
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

function recordTaskLogRequest(payload: unknown = { lines: [], nextCursor: null, truncated: false }) {
  const requests: Request[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/:number/workflow/tasks/:taskId/logs', ({ request }) => {
      requests.push(request)
      return successResponse(payload)
    }),
  )
  return requests
}

describe('getIssueWorkflowTaskLog client', () => {
  it('issues a GET to the issue-path logs endpoint', async () => {
    const requests = recordTaskLogRequest({
      lines: [{ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'action:rebase', text: 'CONFLICT' }],
      nextCursor: 1,
      truncated: false,
    })

    const result = await getIssueWorkflowTaskLog(161, 'build.1', {}, 'proj-1')

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    expect(result.lines).toHaveLength(1)
    expect(result.lines[0].source).toBe('action:rebase')
    expect(result.lines[0].text).toBe('CONFLICT')
    expect(result.nextCursor).toBe(1)
    expect(result.truncated).toBe(false)
  })

  it('serializes cursor and limit query params', async () => {
    const requests = recordTaskLogRequest()

    await getIssueWorkflowTaskLog(161, 'build.1', { cursor: 5, limit: 50 }, 'proj-1')

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs?cursor=5&limit=50')
  })

  it('encodes taskId in the path', async () => {
    const requests = recordTaskLogRequest()

    await getIssueWorkflowTaskLog(161, 'integrate:publish.1', {}, 'proj-1')

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/161/workflow/tasks/integrate%3Apublish.1/logs')
  })

  it('omits query string when no params are provided', async () => {
    const requests = recordTaskLogRequest()

    await getIssueWorkflowTaskLog(161, 'build.1', {}, 'proj-1')

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs')
  })
})
