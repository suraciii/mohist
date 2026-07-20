import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { readAgentModelAndVariant, unarchiveAgent, writeAgentModelAndVariant } from './client'

useMswServer()

function successResponse(payload: unknown) {
  return HttpResponse.json({ success: true, data: payload })
}

function requestPath(request: Request) {
  const url = new URL(request.url)
  return `${url.pathname}${url.search}`
}

describe('unarchiveAgent', () => {
  it('POSTs /api/projects/{ref}/agents/{id}/unarchive and returns the agent payload', async () => {
    const requests: Request[] = []
    const bodies: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentId/unarchive', async ({ request }) => {
        requests.push(request)
        bodies.push(await request.text())
        return successResponse({
          id: 'agent-1',
          projectId: 'proj-1',
          name: 'Agent 1',
          status: 'active',
        })
      }),
    )

    const result = await unarchiveAgent('proj-1', 'agent-1')

    expect(result).toMatchObject({ id: 'agent-1', status: 'active' })
    expect(requests).toHaveLength(1)
    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/agent-1/unarchive')
    expect(requests[0].method).toBe('POST')
    expect(requests[0].headers.get('content-type')).toBe('application/json')
    expect(bodies).toEqual([''])
  })

  it('encodes agent ids that need URL escaping', async () => {
    const paths: string[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/:agentId/unarchive', ({ request }) => {
        paths.push(requestPath(request))
        return successResponse({
          id: 'a/b',
          projectId: 'proj-1',
          name: 'A/B',
          status: 'active',
        })
      }),
    )

    await unarchiveAgent('proj-1', 'a/b')

    expect(paths).toEqual(['/api/projects/proj-1/agents/a%2Fb/unarchive'])
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

  it('writes only model and variant, dropping legacy keys', () => {
    // Per #410 T-002 design D5: the agent profile editor must save a
    // converged agentConfig that contains only {model, variant}. Legacy
    // ACP/liveness keys supplied via spread are not preserved.
    expect(
      writeAgentModelAndVariant({ type: 'opencode', temperature: 0.5 }, 'anthropic/claude', 'low'),
    ).toEqual({
      model: 'anthropic/claude',
      variant: 'low',
    })
  })

  it('drops the variant when null is passed', () => {
    expect(writeAgentModelAndVariant({ model: 'm', variant: 'high' }, 'm', null)).toEqual({
      model: 'm',
    })
  })

  it('returns null when model is null regardless of legacy keys', () => {
    // Dropping the model clears the agentConfig entirely; legacy keys
    // are not preserved on the converged path.
    expect(writeAgentModelAndVariant({ model: 'm', variant: 'high', type: 'opencode' }, null, null)).toBeNull()
  })

  it('returns null when writing an empty config', () => {
    expect(writeAgentModelAndVariant({}, null, null)).toBeNull()
  })

  it('returns null when the input is null and both fields are null', () => {
    expect(writeAgentModelAndVariant(null, null, null)).toBeNull()
  })
})
