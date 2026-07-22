import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { issueEventsQueryOptions, issueWorkflowTaskLogQueryOptions, workspaceStatusQueryOptions } from './queries'

const EVENTS_DTO = [
  { id: 'evt-1', type: 'issue.updated', time: '2026-07-03T08:00:00.000Z' },
]

function recordIssueEventsRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/:issueNumber/events', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: EVENTS_DTO })
    }),
  )
  return urls
}

useMswServer()

describe('issueEventsQueryOptions', () => {
  it('uses the issue-workflow namespace for issue events', () => {
    const options = issueEventsQueryOptions('proj-1', 42)

    expect(options.queryKey).toEqual(['issue-workflow', 'proj-1', 42, 'events'])
    expect(options.queryKey[0]).toBe('issue-workflow')
    expect(options.queryKey).not.toContain('issues')
  })

  it('does NOT prefix the query key with ["issues", ...] so LiveTaskProvider invalidations do not refetch it', () => {
    const options = issueEventsQueryOptions('proj-1', 42)

    expect(options.queryKey[0]).not.toBe('issues')
    expect(options.queryKey).not.toEqual(expect.arrayContaining(['issues', 42, 'proj-1', 'events']))
    expect(options.queryKey[0]).toBe('issue-workflow')
  })

  it('fetches the issue events endpoint for (number, projectId)', async () => {
    const urls = recordIssueEventsRequests()

    const data = await issueEventsQueryOptions('proj-1', 42).queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/42/events'])
    expect(data).toEqual(EVENTS_DTO)
  })

  it('is disabled when number is 0 even if projectId is set', () => {
    expect(issueEventsQueryOptions('proj-1', 0).enabled).toBe(false)
  })

  it('is disabled when projectId is missing', () => {
    expect(issueEventsQueryOptions(null, 42).enabled).toBe(false)
  })

  it('is enabled when both number > 0 and projectId are set', () => {
    expect(issueEventsQueryOptions('proj-1', 42).enabled).toBe(true)
  })

  it('respects an explicit enabled=false override', () => {
    expect(issueEventsQueryOptions('proj-1', 42, false).enabled).toBe(false)
  })

  it('changes the query key when number changes (re-issued)', () => {
    const first = issueEventsQueryOptions('proj-1', 42)
    const second = issueEventsQueryOptions('proj-1', 43)

    expect(first.queryKey).toEqual(['issue-workflow', 'proj-1', 42, 'events'])
    expect(second.queryKey).toEqual(['issue-workflow', 'proj-1', 43, 'events'])
  })
})

describe('workspaceStatusQueryOptions', () => {
  it('is enabled by default when issue number and project id are set', () => {
    const options = workspaceStatusQueryOptions('proj-1', 161)

    expect(options.queryKey).toEqual(['issue-workflow', 'proj-1', 161, 'workspace'])
    expect(options.enabled).toBe(true)
  })

  it('respects an explicit enabled=false override', () => {
    expect(workspaceStatusQueryOptions('proj-1', 161, false).enabled).toBe(false)
  })

  it('retries workspace status faster when the runner cannot check upstream', () => {
    const options = workspaceStatusQueryOptions('proj-1', 161)

    expect(options.refetchInterval({ state: { data: { reason: 'fetch_failed' } } })).toBe(5_000)
    expect(options.refetchInterval({ state: { data: { reason: 'git_error', ahead: 0, behind: 0 } } })).toBe(5_000)
    expect(options.refetchInterval({ state: { data: { exists: true } } })).toBe(5_000)
    expect(options.refetchInterval({ state: { data: { exists: true, ahead: 1, behind: 2 } } })).toBe(30_000)
  })
})

describe('issueWorkflowTaskLogQueryOptions', () => {
  it('includes workflowRunId in the cache key so reruns with the same task id do not reuse old logs', () => {
    const first = issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1', { limit: 5000 }, true, 'wr-1')
    const second = issueWorkflowTaskLogQueryOptions('proj-1', 161, 'build.1', { limit: 5000 }, true, 'wr-2')

    expect(first.queryKey).toEqual(['issue-workflow', 'proj-1', 161, 'task-log', 'build.1', 'wr-1', { limit: 5000 }])
    expect(second.queryKey).toEqual(['issue-workflow', 'proj-1', 161, 'task-log', 'build.1', 'wr-2', { limit: 5000 }])
  })
})
