import { afterEach, describe, expect, it, vi } from 'vitest'
import { composeIssueTemplateBody, getIssueTemplate, getIssueTemplates } from './client'

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
      { id: 'mohist/default', name: 'Mohist Default', about: 'three-voice PRD', isDefault: true, suitableFor: ['prd'], source: 'builtin' },
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
      id: 'mohist/default',
      name: 'Mohist Default',
      about: 'three-voice PRD',
      isDefault: true,
      suitableFor: ['prd'],
      defaults: null,
      sections: [],
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

describe('composeIssueTemplateBody', () => {
  it('joins "## {title}\\n{placeholder}" for each section in order, excluding guidance', () => {
    const body = composeIssueTemplateBody({
      sections: [
        { title: 'User Voice', guidance: 'what to write', placeholder: '<user voice>' },
        { title: 'Product Shape', guidance: 'what to write', placeholder: '<product shape>' },
      ],
    })

    expect(body).toBe([
      '## User Voice',
      '<user voice>',
      '',
      '## Product Shape',
      '<product shape>',
    ].join('\n'))
  })

  it('returns an empty string for templates with no sections', () => {
    expect(composeIssueTemplateBody({ sections: [] })).toBe('')
  })

  it('uses only the placeholder, never the guidance, even when guidance is non-empty', () => {
    const body = composeIssueTemplateBody({
      sections: [
        { title: 'AC', guidance: 'sensitive guidance that must not leak', placeholder: 'placeholder' },
      ],
    })

    expect(body).toContain('## AC')
    expect(body).toContain('placeholder')
    expect(body).not.toContain('sensitive guidance that must not leak')
  })
})
