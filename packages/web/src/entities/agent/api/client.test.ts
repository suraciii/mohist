import { afterEach, describe, expect, it, vi } from 'vitest'
import { readAgentModelAndVariant, unarchiveAgent, writeAgentModelAndVariant } from './client'

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

describe('unarchiveAgent', () => {
  it('POSTs /api/projects/{ref}/agents/{id}/unarchive and returns the agent payload', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      mockJsonResponse({
        id: 'agent-1',
        projectId: 'proj-1',
        name: 'Agent 1',
        status: 'active',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    const result = await unarchiveAgent('proj-1', 'agent-1')

    expect(result).toMatchObject({ id: 'agent-1', status: 'active' })
    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agents/agent-1/unarchive')
    expect(calledInit?.method).toBe('POST')
  })

  it('encodes agent ids that need URL escaping', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      mockJsonResponse({
        id: 'a/b',
        projectId: 'proj-1',
        name: 'A/B',
        status: 'active',
      }),
    )
    vi.stubGlobal('fetch', fetchMock)

    await unarchiveAgent('proj-1', 'a/b')

    const [calledPath, calledInit] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agents/a%2Fb/unarchive')
    expect(calledInit?.method).toBe('POST')
  })
})

describe('readAgentModelAndVariant', () => {
  it('returns null model and variant when agent config is missing', () => {
    expect(readAgentModelAndVariant(null)).toEqual({ model: null, variant: null })
  })

  it('returns null model and variant when agent config is not an object', () => {
    expect(readAgentModelAndVariant({ agentConfig: 'not-an-object' as unknown as Record<string, unknown> })).toEqual({
      model: null,
      variant: null,
    })
  })

  it('returns the stored model and variant', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: { model: 'anthropic/claude', variant: 'high' },
      }),
    ).toEqual({ model: 'anthropic/claude', variant: 'high' })
  })

  it('drops empty/whitespace model and variant', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: { model: '   ', variant: '' },
      }),
    ).toEqual({ model: null, variant: null })
  })

  it('omits the variant when no model is set', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: { variant: 'high' },
      }),
    ).toEqual({ model: null, variant: null })
  })
})

describe('writeAgentModelAndVariant', () => {
  it('writes model and variant to an empty config', () => {
    expect(writeAgentModelAndVariant(null, 'anthropic/claude', 'high')).toEqual({
      model: 'anthropic/claude',
      variant: 'high',
    })
  })

  it('preserves other keys while updating model and variant', () => {
    expect(
      writeAgentModelAndVariant({ type: 'opencode', temperature: 0.5 }, 'anthropic/claude', 'low'),
    ).toEqual({
      type: 'opencode',
      temperature: 0.5,
      model: 'anthropic/claude',
      variant: 'low',
    })
  })

  it('drops the variant when null is passed', () => {
    expect(writeAgentModelAndVariant({ model: 'm', variant: 'high' }, 'm', null)).toEqual({
      model: 'm',
    })
  })

  it('drops both model and variant when model is null (atomic clear)', () => {
    expect(writeAgentModelAndVariant({ model: 'm', variant: 'high', type: 'opencode' }, null, null)).toEqual({
      type: 'opencode',
    })
  })

  it('returns null when writing an empty config', () => {
    expect(writeAgentModelAndVariant({}, null, null)).toBeNull()
  })

  it('returns null when the input is null and both fields are null', () => {
    expect(writeAgentModelAndVariant(null, null, null)).toBeNull()
  })
})
