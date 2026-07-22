import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { issueWorkflowTaskLogQueryOptions } from './queries'

const EMPTY_PAGE = { lines: [], nextCursor: null, truncated: false }

function recordTaskLogRequests(
  respond: () => Response = () => HttpResponse.json({ success: true, data: EMPTY_PAGE }),
) {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/:issueNumber/workflow/tasks/:taskId/logs', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return respond()
    }),
  )
  return urls
}

useMswServer()

describe('issueWorkflowTaskLogQueryOptions query key', () => {
  it('uses the issue-workflow namespace and includes the task id', () => {
    const options = issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1')

    expect(options.queryKey[0]).toBe('issue-workflow')
    expect(options.queryKey[2]).toBe(161)
    expect(options.queryKey[4]).toBe('build.1')
  })

  it('changes the query key when taskId changes (refetch per expanded task)', () => {
    const first = issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1')
    const second = issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.2')

    expect(first.queryKey[4]).toBe('build.1')
    expect(second.queryKey[4]).toBe('build.2')
    expect(first.queryKey).not.toEqual(second.queryKey)
  })

  it('disables the query when taskId is null', () => {
    expect(issueWorkflowTaskLogQueryOptions('proj-1', 161, null).enabled).toBe(false)
  })

  it('disables the query when taskId is undefined', () => {
    expect(issueWorkflowTaskLogQueryOptions('proj-1', 161, undefined).enabled).toBe(false)
  })

  it('disables the query when issueNumber is zero', () => {
    expect(issueWorkflowTaskLogQueryOptions('proj-1', 0, 'build.1').enabled).toBe(false)
  })

  it('disables the query when projectId is missing', () => {
    expect(issueWorkflowTaskLogQueryOptions(null, 161, 'build.1').enabled).toBe(false)
  })

  it('is enabled when issueNumber > 0, taskId is non-empty, and projectId is set', () => {
    expect(issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1').enabled).toBe(true)
  })

  it('respects an explicit enabled=false override', () => {
    expect(issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1', {}, false).enabled).toBe(false)
  })
})

describe('issueWorkflowTaskLogQueryOptions query function', () => {
  it('fetches the task log endpoint with issueNumber, taskId, params, and projectId', async () => {
    const urls = recordTaskLogRequests()

    await issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1', { cursor: 5, limit: 50 }).queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs?cursor=5&limit=50'])
  })

  it('returns the fetched page on success', async () => {
    const page = {
      lines: [{ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'action:rebase', text: 'CONFLICT' }],
      nextCursor: 1,
      truncated: false,
    }
    recordTaskLogRequests(() => HttpResponse.json({ success: true, data: page }))

    const result = await issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1').queryFn()

    expect(result).toEqual(page)
  })

  it('returns an empty page when an older server is missing the endpoint route', async () => {
    recordTaskLogRequests(() => new HttpResponse('', { status: 404 }))

    const result = await issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1').queryFn()

    expect(result).toEqual({ lines: [], nextCursor: null, truncated: false })
  })

  it('rethrows structured 404 errors so real missing resources are visible', async () => {
    recordTaskLogRequests(() =>
      HttpResponse.json({ success: false, error: 'Issue #161 not found', code: 'not_found' }, { status: 404 }),
    )

    await expect(issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1').queryFn()).rejects.toThrow('Issue #161 not found')
  })

  it('rethrows non-404 errors so callers can surface them', async () => {
    recordTaskLogRequests(() => HttpResponse.json({ success: false, error: 'boom' }, { status: 500 }))

    await expect(issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1').queryFn()).rejects.toThrow('boom')
  })
})
