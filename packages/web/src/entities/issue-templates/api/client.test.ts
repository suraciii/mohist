import { afterEach, describe, expect, it, vi } from 'vitest'
import { getIssueTemplate, getIssueTemplates } from './client'

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

describe('getIssueTemplates', () => {
  it('requests GET /api/issue-templates with the active project id', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse([]))
    vi.stubGlobal('fetch', fetchMock)

    await getIssueTemplates('proj-1')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/issue-templates?projectId=proj-1')
  })

  it('returns the issue template info list', async () => {
    const payload = [
      { id: 'feature', name: 'Feature', description: 'Three-voice PRD template', source: 'builtin' },
    ]
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(mockJsonResponse(payload)))

    const list = await getIssueTemplates('proj-1')

    expect(list).toEqual(payload)
  })
})

describe('getIssueTemplate', () => {
  it('requests GET /api/issue-templates/{name} with the literal slash left unencoded', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(mockJsonResponse({
      id: 'feature',
      name: 'Feature',
      description: 'Three-voice PRD template',
      body: '## User Voice\n\n<user voice>',
      source: 'builtin',
    }))
    vi.stubGlobal('fetch', fetchMock)

    await getIssueTemplate('mohist/default', 'proj-1')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/issue-templates/mohist/default?projectId=proj-1')
    expect(calledPath).not.toContain('mohist%2Fdefault')
    expect(calledPath).not.toContain('%2F')
  })
})
