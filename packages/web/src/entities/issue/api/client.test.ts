import { afterEach, describe, expect, it, vi } from 'vitest'
import { getIssueEvents } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function mockJsonResponse(payload: unknown, status: number = 200): Response {
  return new Response(JSON.stringify({ success: true, data: payload }), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('getIssueEvents', () => {
  it('requests GET /api/projects/{ref}/issues/{number}/events', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/42/events')
    expect(calledInit?.method).toBeUndefined()
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
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse(stored)))

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual(stored)
  })

  it('returns an empty array when the server sends an empty list', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse([])))

    const events = await getIssueEvents(42, 'proj-1')

    expect(events).toEqual([])
  })
})