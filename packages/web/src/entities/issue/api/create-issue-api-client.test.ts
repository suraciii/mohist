import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { createIssue } from './client'

useMswServer()

interface CapturedRequest {
  path: string
  method: string
  contentType: string | null
  body: unknown
}

async function captureCreateIssueRequest(request: Request, requests: CapturedRequest[]) {
  const url = new URL(request.url)
  requests.push({
    path: `${url.pathname}${url.search}`,
    method: request.method,
    contentType: request.headers.get('content-type'),
    body: await request.json(),
  })
}

function issueResponse(title: string, extra: Record<string, unknown> = {}) {
  return {
    number: 1,
    title,
    body: null,
    status: 'backlog',
    health: 'active',
    projectId: 'proj_1',
    labels: {},
    createdAt: '2026-06-16T00:00:00.000Z',
    updatedAt: '2026-06-16T00:00:00.000Z',
    ...extra,
  }
}

function recordCreateIssueRequest(response: Record<string, unknown>) {
  const requests: CapturedRequest[] = []
  server.use(
    http.post('*/api/projects/:projectId/issues', async ({ request }) => {
      await captureCreateIssueRequest(request, requests)
      return HttpResponse.json({ success: true, data: response }, { status: 201 })
    }),
  )
  return requests
}

describe('createIssue api client', () => {
  it('includes risk in the request payload when provided', async () => {
    const requests = recordCreateIssueRequest(issueResponse('Risked', { risk: 'high' }))

    await createIssue({ title: 'Risked', risk: 'high', projectId: 'proj_1' })

    expect(requests).toEqual([
      {
        path: '/api/projects/proj_1/issues',
        method: 'POST',
        contentType: 'application/json',
        body: { title: 'Risked', risk: 'high' },
      },
    ])
  })

  it('omits risk when not provided', async () => {
    const requests = recordCreateIssueRequest(issueResponse('No risk'))

    await createIssue({ title: 'No risk', projectId: 'proj_1' })

    expect(requests).toEqual([
      {
        path: '/api/projects/proj_1/issues',
        method: 'POST',
        contentType: 'application/json',
        body: { title: 'No risk' },
      },
    ])
  })

  it('accepts null risk and sends it in the payload', async () => {
    const requests = recordCreateIssueRequest(issueResponse('Null risk'))

    await createIssue({ title: 'Null risk', risk: null, projectId: 'proj_1' })

    expect(requests).toEqual([
      {
        path: '/api/projects/proj_1/issues',
        method: 'POST',
        contentType: 'application/json',
        body: { title: 'Null risk', risk: null },
      },
    ])
  })

  it('includes workflowProfileId in the request payload when provided', async () => {
    const requests = recordCreateIssueRequest(issueResponse('Profiled', { workflowProfileId: 'feature-flow' }))

    await createIssue({ title: 'Profiled', workflowProfileId: 'feature-flow', projectId: 'proj_1' })

    expect(requests).toEqual([
      {
        path: '/api/projects/proj_1/issues',
        method: 'POST',
        contentType: 'application/json',
        body: { title: 'Profiled', workflowProfileId: 'feature-flow' },
      },
    ])
  })

  it('omits workflowProfileId when not provided', async () => {
    const requests = recordCreateIssueRequest(issueResponse('No profile'))

    await createIssue({ title: 'No profile', projectId: 'proj_1' })

    expect(requests).toEqual([
      {
        path: '/api/projects/proj_1/issues',
        method: 'POST',
        contentType: 'application/json',
        body: { title: 'No profile' },
      },
    ])
  })

  it('sends parentIssueNumber alongside repositoryName in one POST', async () => {
    const requests = recordCreateIssueRequest(issueResponse('Child issue'))

    await createIssue({
      title: 'Child issue',
      repositoryName: 'web',
      parentIssueNumber: 42,
      projectId: 'proj_1',
    })

    expect(requests).toEqual([
      {
        path: '/api/projects/proj_1/issues',
        method: 'POST',
        contentType: 'application/json',
        body: { title: 'Child issue', repositoryName: 'web', parentIssueNumber: 42 },
      },
    ])
  })
})
