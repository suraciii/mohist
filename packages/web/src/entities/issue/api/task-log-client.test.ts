import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getIssueWorkflowTaskLog } from './client'

beforeEach(() => {
  vi.unstubAllGlobals()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('getIssueWorkflowTaskLog client', () => {
  it('issues a GET to the issue-path logs endpoint', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            lines: [{ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'action:rebase', text: 'CONFLICT' }],
            nextCursor: 1,
            truncated: false,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await getIssueWorkflowTaskLog(161, 'build.1', {}, 'proj-1')

    const [calledPath, init] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs')
    expect(init?.method).toBeUndefined()
    expect(result.lines).toHaveLength(1)
    expect(result.lines[0].source).toBe('action:rebase')
    expect(result.lines[0].text).toBe('CONFLICT')
    expect(result.nextCursor).toBe(1)
    expect(result.truncated).toBe(false)
  })

  it('serializes cursor and limit query params', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({ success: true, data: { lines: [], nextCursor: null, truncated: false } }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await getIssueWorkflowTaskLog(161, 'build.1', { cursor: 5, limit: 50 }, 'proj-1')

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs?cursor=5&limit=50')
  })

  it('encodes taskId in the path', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({ success: true, data: { lines: [], nextCursor: null, truncated: false } }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await getIssueWorkflowTaskLog(161, 'integrate:publish.1', {}, 'proj-1')

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/161/workflow/tasks/integrate%3Apublish.1/logs')
  })

  it('omits query string when no params are provided', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({ success: true, data: { lines: [], nextCursor: null, truncated: false } }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await getIssueWorkflowTaskLog(161, 'build.1', {}, 'proj-1')

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/161/workflow/tasks/build.1/logs')
  })
})