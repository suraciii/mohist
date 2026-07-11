import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { getIssueTemplate, getIssueTemplates } from './client'

useMswServer()

function successResponse(payload: unknown) {
  return HttpResponse.json({ success: true, data: payload })
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

describe('getIssueTemplates', () => {
  it('requests GET /api/issue-templates with the active project id', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/issue-templates', ({ request }) => {
        requests.push(request)
        return successResponse([])
      }),
    )

    await getIssueTemplates('proj-1')

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/issue-templates?projectId=proj-1')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })

  it('returns the issue template info list', async () => {
    const payload = [
      { id: 'feature', name: 'Feature', description: 'Three-voice PRD template', source: 'builtin' },
    ]
    server.use(
      http.get('*/api/issue-templates', () => successResponse(payload)),
    )

    const list = await getIssueTemplates('proj-1')

    expect(list).toEqual(payload)
  })
})

describe('getIssueTemplate', () => {
  it('requests GET /api/issue-templates/{name} with the literal slash left unencoded', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/issue-templates/*', ({ request }) => {
        requests.push(request)
        return successResponse({
          id: 'feature',
          name: 'Feature',
          description: 'Three-voice PRD template',
          body: '## User Voice\n\n<user voice>',
          source: 'builtin',
        })
      }),
    )

    await getIssueTemplate('mohist/default', 'proj-1')

    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/issue-templates/mohist/default?projectId=proj-1')
    expect(requestPath(requests[0])).not.toContain('mohist%2Fdefault')
    expect(requestPath(requests[0])).not.toContain('%2F')
    expect(requests[0].method).toBe('GET')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
  })
})
