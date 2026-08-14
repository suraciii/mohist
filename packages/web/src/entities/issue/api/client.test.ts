import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  addComment,
  approveIssue,
  createIssue,
  getIssueEvents,
  getIssueWorkflowArtifactContent,
  getIssues,
  getLabels,
  getParentIssueCandidates,
  getWorkflowRunDetail,
  requestChangesIssue,
  updateIssue,
} from './client'

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
        data: { issueNumber: 42 },
        extensions: {},
      },
    ]
    server.use(http.get('*/api/projects/:projectId/issues/:number/events', () => successResponse(stored)))

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual(stored)
  })

  it('returns an empty array when the server sends an empty list', async () => {
    server.use(http.get('*/api/projects/:projectId/issues/:number/events', () => successResponse([])))

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
  })
})

describe('getWorkflowRunDetail', () => {
  it('reads the Run-bound Agent projection from the global workflow-run detail resource', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/workflow-runs/:workflowRunId', ({ request }) => {
        requests.push(request)
        return successResponse({
          status: { workflowRunId: 'run-42', status: 'running' },
          issueRef: { projectId: 'proj-1', number: 42, title: 'Issue' },
          workflowProfileId: 'mohist/github-pr',
          agentAction: 'mohist/pi',
          agentRuntime: 'pi',
        })
      }),
    )

    await expect(getWorkflowRunDetail('run-42')).resolves.toEqual(
      expect.objectContaining({
        workflowProfileId: 'mohist/github-pr',
        agentAction: 'mohist/pi',
        agentRuntime: 'pi',
      }),
    )
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/workflow-runs/run-42')
  })
})

describe('issue list and parent candidate clients', () => {
  it('forwards the TanStack cancellation signal to the list request', async () => {
    const controller = new AbortController()
    let observedRequest: Request | undefined
    server.use(
      http.get('*/api/projects/:projectId/issues', ({ request }) => {
        observedRequest = request
        return successResponse([])
      }),
    )

    await getIssues({ projectId: 'proj-1' }, controller.signal)

    expect(observedRequest!.signal.aborted).toBe(false)
    controller.abort()
    expect(observedRequest!.signal.aborted).toBe(true)
  })

  it('requests the compact project-scoped parent candidate endpoint', async () => {
    const requests: Request[] = []
    server.use(
      http.get('*/api/projects/:projectId/issues/parent-candidates', ({ request }) => {
        requests.push(request)
        return successResponse([{ number: 7, title: 'Parent' }])
      }),
    )

    await expect(getParentIssueCandidates('proj-1')).resolves.toEqual([{ number: 7, title: 'Parent' }])
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/issues/parent-candidates')
  })
})

describe('addComment', () => {
  it('sends the display alias and returns the persisted author and alias', async () => {
    let requestBody: unknown
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/comments', async ({ request }) => {
        requestBody = await request.json()
        return successResponse({
          id: 'cmt-1',
          author: 'admin',
          displayName: 'Ada Lovelace',
          body: 'Looks good',
          createdAt: '2026-07-21T08:00:00Z',
        })
      }),
    )

    const comment = await addComment(42, 'Ada Lovelace', 'Looks good', 'proj-1', ['att-1'])

    expect(requestBody).toEqual({ displayName: 'Ada Lovelace', body: 'Looks good', attachmentIds: ['att-1'] })
    expect(comment.author).toBe('admin')
    expect(comment.displayName).toBe('Ada Lovelace')
  })
})

describe('approval decisions', () => {
  it('sends approval decisions without attribution when no operator is provided', async () => {
    const requestBodies: unknown[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/approve', async ({ request }) => {
        requestBodies.push(await request.json())
        return successResponse(null)
      }),
      http.post('*/api/projects/:projectId/issues/:number/feedback', async ({ request }) => {
        requestBodies.push(await request.json())
        return successResponse({ id: 'feedback-1' })
      }),
    )

    await approveIssue(42, {}, 'proj-1')
    const feedback = await requestChangesIssue(42, { stage: 'plan', body: 'Narrow the scope.' }, 'proj-1')

    expect(requestBodies).toEqual([{}, { stage: 'plan', body: 'Narrow the scope.' }])
    expect(feedback).toEqual({ id: 'feedback-1' })
  })

  it('sends the declared operator for approve and send back', async () => {
    const requestBodies: unknown[] = []
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/approve', async ({ request }) => {
        requestBodies.push(await request.json())
        return successResponse(null)
      }),
      http.post('*/api/projects/:projectId/issues/:number/feedback', async ({ request }) => {
        requestBodies.push(await request.json())
        return successResponse({ id: 'feedback-1' })
      }),
    )

    await approveIssue(42, { displayName: 'Ada' }, 'proj-1')
    const feedback = await requestChangesIssue(
      42,
      { stage: 'plan', body: 'Narrow the scope.', displayName: 'Ada' },
      'proj-1',
    )

    expect(requestBodies).toEqual([
      { displayName: 'Ada' },
      { stage: 'plan', body: 'Narrow the scope.', displayName: 'Ada' },
    ])
    expect(feedback).toEqual({ id: 'feedback-1' })
  })
})

describe('getIssueWorkflowArtifactContent', () => {
  it('preserves a JSON file that has directory-like fields', async () => {
    const body = '{\n  "entries": [],\n  "totalSize": 0\n}'
    server.use(
      http.get(
        '*/api/projects/:projectId/issues/:number/workflow/artifacts/:artifactId/content',
        () =>
          new HttpResponse(body, {
            headers: { 'content-type': 'application/json' },
          }),
      ),
    )

    await expect(
      getIssueWorkflowArtifactContent(455, 'artifact-tasks', { artifactKind: 'file' }, 'proj-1'),
    ).resolves.toEqual({
      kind: 'text',
      content: body,
      contentType: 'application/json',
    })
  })

  it('keeps directory listings as directory content', async () => {
    server.use(
      http.get('*/api/projects/:projectId/issues/:number/workflow/artifacts/:artifactId/content', () =>
        HttpResponse.json({
          entries: [{ relativePath: 'report.md', size: 42, contentType: 'text/markdown' }],
          totalSize: 42,
        }),
      ),
    )

    await expect(
      getIssueWorkflowArtifactContent(455, 'artifact-directory', { artifactKind: 'directory' }, 'proj-1'),
    ).resolves.toEqual({
      kind: 'directory',
      entries: [{ relativePath: 'report.md', size: 42, contentType: 'text/markdown' }],
      totalSize: 42,
    })
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
    server.use(http.get('*/api/projects/:projectId/labels', () => successResponse([])))

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
