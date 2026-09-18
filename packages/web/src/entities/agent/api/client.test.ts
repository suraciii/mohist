import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import {
  BUILT_IN_AGENT_ID_PREFIX,
  customizeAgent,
  getAgentByName,
  isBuiltInAgentRef,
  readAgentDefinitionModelAndVariant,
  readAgentModelAndVariant,
  unarchiveAgent,
  writeAgentModelAndVariant,
} from './client'

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

describe('getAgentByName', () => {
  it('reads a built-in name through the project by-name route with literal slashes', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/by-name/*', ({ request }) => {
        paths.push(requestPath(request))
        return successResponse({ id: 'builtin:mohist/planner', name: 'mohist/planner', origin: 'built-in' })
      }),
    )

    const agent = await getAgentByName('proj-1', 'mohist/planner')

    expect(paths).toEqual(['/api/projects/proj-1/agents/by-name/mohist/planner'])
    expect(agent.id).toBe('builtin:mohist/planner')
  })

  it('encodes name segments that need URL escaping', async () => {
    const paths: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/agents/by-name/*', ({ request }) => {
        paths.push(requestPath(request))
        return successResponse({ id: 'agent-1', name: 'a b/c' })
      }),
    )

    await getAgentByName('proj-1', 'a b/c')

    expect(paths).toEqual(['/api/projects/proj-1/agents/by-name/a%20b/c'])
  })
})

describe('customizeAgent', () => {
  it('POSTs /agents/overrides with only the caller changes and returns the materialized Agent', async () => {
    const requests: Request[] = []
    const bodies: unknown[] = []
    server.use(
      http.post('*/api/projects/:projectId/agents/overrides', async ({ request }) => {
        requests.push(request)
        bodies.push(await request.json())
        return HttpResponse.json(
          { success: true, data: { id: 'agent-override', name: 'mohist/reviewer', origin: 'project' } },
          { status: 201 },
        )
      }),
    )

    const created = await customizeAgent('proj-1', { name: 'mohist/reviewer' })

    expect(requestPath(requests[0])).toBe('/api/projects/proj-1/agents/overrides')
    expect(requests[0].method).toBe('POST')
    expect(bodies).toEqual([{ name: 'mohist/reviewer' }])
    expect(created.id).toBe('agent-override')
  })

  it('surfaces the named conflict code and the existing Agent id', async () => {
    server.use(
      http.post('*/api/projects/:projectId/agents/overrides', () =>
        HttpResponse.json(
          {
            success: false,
            error: 'already overrides this built-in Agent',
            code: 'agent_override_conflict',
            data: { name: 'mohist/reviewer', agentId: 'agent-existing' },
          },
          { status: 409 },
        ),
      ),
    )

    await expect(customizeAgent('proj-1', { name: 'mohist/reviewer' })).rejects.toMatchObject({
      code: 'agent_override_conflict',
      data: { agentId: 'agent-existing' },
    })
  })
})

describe('built-in Agent refs', () => {
  it('recognizes the built-in id prefix', () => {
    expect(isBuiltInAgentRef(`${BUILT_IN_AGENT_ID_PREFIX}mohist/planner`)).toBe(true)
    expect(isBuiltInAgentRef('agent_123')).toBe(false)
    expect(isBuiltInAgentRef(null)).toBe(false)
    expect(isBuiltInAgentRef(undefined)).toBe(false)
  })
})

describe('readAgentModelAndVariant', () => {
  it('returns null model and variant when agent config is missing', () => {
    expect(readAgentModelAndVariant(null)).toEqual({
      model: null,
      variant: null,
      reasoningEffort: null,
      runtime: 'pi',
    })
  })

  it('returns null model and variant when agent config is not an object', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: 'not-an-object' as unknown as Record<string, unknown>,
      }),
    ).toEqual({
      model: null,
      variant: null,
      reasoningEffort: null,
      runtime: 'pi',
    })
  })

  it('returns the stored model and variant', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: { model: 'anthropic/claude', variant: 'high' },
      }),
    ).toEqual({
      model: 'anthropic/claude',
      variant: 'high',
      reasoningEffort: null,
      runtime: 'pi',
    })
  })

  it('reads raw definition fields without materializing an effective default', () => {
    const agent = {
      agentConfig: null,
      effectiveExecutionConfig: {
        runtime: 'pi' as const,
        model: 'provider/default',
        variant: 'balanced',
      },
    }
    expect(readAgentDefinitionModelAndVariant(agent)).toEqual({
      model: null,
      variant: null,
      reasoningEffort: null,
      runtime: 'pi',
    })
  })

  it('preserves codex from the raw agent definition', () => {
    expect(
      readAgentDefinitionModelAndVariant({
        agentConfig: { runtime: 'codex' },
      }),
    ).toEqual({
      model: null,
      variant: null,
      reasoningEffort: null,
      runtime: 'codex',
    })
  })

  it('preserves codex from the effective execution config', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: null,
        effectiveExecutionConfig: {
          runtime: 'codex',
          model: 'openai/codex-mini',
          variant: null,
        },
      }),
    ).toEqual({
      model: 'openai/codex-mini',
      variant: null,
      reasoningEffort: null,
      runtime: 'codex',
    })
  })

  it('drops empty/whitespace model and variant', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: { model: '   ', variant: '' },
      }),
    ).toEqual({
      model: null,
      variant: null,
      reasoningEffort: null,
      runtime: 'pi',
    })
  })

  it('preserves the raw variant when no model is set', () => {
    expect(
      readAgentModelAndVariant({
        agentConfig: { variant: 'high' },
      }),
    ).toEqual({
      model: null,
      variant: 'high',
      reasoningEffort: null,
      runtime: 'pi',
    })
  })
})

describe('writeAgentModelAndVariant', () => {
  it('writes model and variant to an empty config', () => {
    expect(writeAgentModelAndVariant(null, 'anthropic/claude', 'high')).toEqual({
      model: 'anthropic/claude',
      variant: 'high',
      runtime: 'pi',
    })
  })

  it('writes only model and variant, dropping legacy keys', () => {
    // Per #410 T-002 design D5: the agent profile editor must save a
    // converged agentConfig that contains only {model, variant}. Legacy
    // ACP/liveness keys supplied via spread are not preserved.
    expect(writeAgentModelAndVariant({ type: 'opencode', temperature: 0.5 }, 'anthropic/claude', 'low')).toEqual({
      model: 'anthropic/claude',
      variant: 'low',
      runtime: 'pi',
    })
  })

  it('drops the variant when null is passed', () => {
    expect(writeAgentModelAndVariant({ model: 'm', variant: 'high' }, 'm', null)).toEqual({
      model: 'm',
      runtime: 'pi',
    })
  })

  it('returns null when model is null regardless of legacy keys', () => {
    // Dropping the model clears the agentConfig entirely; legacy keys
    // are not preserved on the converged path.
    expect(writeAgentModelAndVariant({ model: 'm', variant: 'high', type: 'opencode' }, null, null)).toBeNull()
  })

  it('preserves a raw variant-only definition', () => {
    expect(writeAgentModelAndVariant({ variant: 'high' }, null, 'high')).toEqual({ variant: 'high' })
  })

  it('returns null when writing an empty config', () => {
    expect(writeAgentModelAndVariant({}, null, null)).toBeNull()
  })

  it('returns null when the input is null and both fields are null', () => {
    expect(writeAgentModelAndVariant(null, null, null)).toBeNull()
  })

  it('preserves runtime through a read-modify-write round trip', () => {
    const read = readAgentModelAndVariant({
      agentConfig: { model: 'pi/model', variant: 'medium', runtime: 'pi' },
    })
    expect(
      writeAgentModelAndVariant(
        { model: 'pi/model', variant: 'medium', runtime: 'pi' },
        read.model,
        'high',
        read.runtime,
      ),
    ).toEqual({ model: 'pi/model', variant: 'high', runtime: 'pi' })
  })

  it('prefers the server effective execution projection over an empty raw config', () => {
    const read = readAgentModelAndVariant({
      agentConfig: null,
      effectiveExecutionConfig: {
        runtime: 'pi',
        model: 'provider/model',
        variant: 'balanced',
      },
    })
    expect(read).toEqual({
      model: 'provider/model',
      variant: 'balanced',
      reasoningEffort: null,
      runtime: 'pi',
    })
  })
})
