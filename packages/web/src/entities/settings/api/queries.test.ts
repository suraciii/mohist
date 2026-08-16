import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  availableModelIdsQueryOptions,
  getWorkflowProfileAgentRuntime,
  projectDefaultWorkflowProfileQueryOptions,
  resolveEffectiveDefaultWorkflowProfile,
  selectAgentTurnActions,
  selectModelReasoningEfforts,
  selectModelVariants,
  setWorkflowProfileAgentActionMutationOptions,
  setProjectDefaultWorkflowProfileMutationOptions,
} from './queries'

useMswServer()

function createInvalidationClient() {
  return { invalidateQueries: vi.fn() }
}

describe('selectModelVariants', () => {
  it('returns an empty map when the discovery query has no data', () => {
    expect(selectModelVariants(undefined)).toEqual({})
  })

  it('returns the populated modelVariants map when the discovery query exposes variants', () => {
    const variants = {
      'anthropic/claude-sonnet-4': ['low', 'medium', 'high'],
      'openai/gpt-5.1': ['low', 'high', 'max'],
    }
    expect(
      selectModelVariants({
        models: ['anthropic/claude-sonnet-4', 'openai/gpt-5.1'],
        modelVariants: variants,
      }),
    ).toEqual(variants)
  })

  it('returns an empty map when discovery data is present but no model exposes variants', () => {
    expect(selectModelVariants({ models: ['openai/gpt-4'], modelVariants: {} })).toEqual({})
  })

  it('does not throw when the underlying data is malformed', () => {
    expect(() => selectModelVariants({ models: ['openai/gpt-4'] } as never)).not.toThrow()
    expect(selectModelVariants({ models: ['openai/gpt-4'] } as never)).toEqual({})
  })
})

describe('selectModelReasoningEfforts', () => {
  it('returns the per-model reasoningEfforts map without reading variants', () => {
    const efforts = { 'openai/gpt-5.5': ['low', 'high'] }
    expect(
      selectModelReasoningEfforts({
        models: ['openai/gpt-5.5'],
        reasoningEfforts: efforts,
      }),
    ).toEqual(efforts)
  })

  it('returns an empty map when the runtime does not publish efforts', () => {
    expect(selectModelReasoningEfforts(undefined)).toEqual({})
    expect(selectModelReasoningEfforts({ models: ['openai/gpt-5.5'], reasoningEfforts: {} })).toEqual({})
  })
})

describe('availableModelIdsQueryOptions', () => {
  it('reads from the same project-scoped opencode-model-ids query as useModelVariants', () => {
    expect(availableModelIdsQueryOptions('proj-1').queryKey).toEqual(['opencode-model-ids', 'opencode', 'proj-1'])
    expect(availableModelIdsQueryOptions('proj-1').enabled).toBe(true)
  })

  it('does not enable model discovery when the selected profile has no runtime', () => {
    const options = availableModelIdsQueryOptions('proj-1', null)
    expect(options.queryKey).toEqual(['opencode-model-ids', null, 'proj-1'])
    expect(options.enabled).toBe(false)
  })
})

describe('getWorkflowProfileAgentRuntime', () => {
  it('matches profile ids without regard to casing', () => {
    expect(
      getWorkflowProfileAgentRuntime(
        [
          {
            id: 'mohist/local',
            displayName: 'Mohist Local',
            description: '',
            isDefault: true,
            agentRuntime: 'opencode',
          },
        ],
        'MOHIST/LOCAL',
      ),
    ).toBe('opencode')
  })
})

describe('selectAgentTurnActions', () => {
  it('uses the declared capability and never infers Agent Actions from names', () => {
    expect(
      selectAgentTurnActions({
        actions: [
          { name: 'mohist/pi', capabilities: ['agent-turn'] },
          { name: 'team/looks-like-an-agent', capabilities: [] },
          { name: 'team/capable', capabilities: ['write-vars', 'agent-turn'] },
        ],
      }).map((action) => action.name),
    ).toEqual(['mohist/pi', 'team/capable'])
  })
})

describe('setWorkflowProfileAgentActionMutationOptions', () => {
  it('refreshes Profile collection, detail, and model catalogs after PATCH succeeds', () => {
    const qc = createInvalidationClient()
    const options = setWorkflowProfileAgentActionMutationOptions('proj-1', qc)

    options.onSuccess(
      {
        id: 'mohist/github-pr',
        displayName: 'GitHub PR',
        description: '',
        isDefault: false,
        agentAction: 'mohist/pi',
        agentRuntime: 'pi',
      },
      { profileId: 'mohist/github-pr', agentAction: 'mohist/pi' },
    )

    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['workflow-profiles', 'proj-1'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['workflow-profile', 'proj-1', 'mohist/github-pr'] })
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['opencode-model-ids'] })
  })
})

describe('projectDefaultWorkflowProfileQueryOptions', () => {
  it('uses a project-scoped query key', () => {
    expect(projectDefaultWorkflowProfileQueryOptions('proj-1').queryKey).toEqual(['project-workflow-profile', 'proj-1'])
  })

  it('is disabled when projectId is missing', () => {
    expect(projectDefaultWorkflowProfileQueryOptions(null).enabled).toBe(false)
  })

  it('invokes getProjectDefaultWorkflowProfile(projectId) as the query function', async () => {
    const captured: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/workflow-profile/default', ({ request }) => {
        captured.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: { projectId: 'proj-1', defaultWorkflowProfileId: 'mohist/github-pr' },
        })
      }),
    )

    const data = await projectDefaultWorkflowProfileQueryOptions('proj-1').queryFn()

    expect(captured).toEqual(['/api/projects/proj-1/workflow-profile/default'])
    expect(data).toEqual({
      projectId: 'proj-1',
      defaultTemplateId: 'mohist/github-pr',
      disabledWorkflowProfileIds: [],
    })
  })
})

describe('setProjectDefaultWorkflowProfileMutationOptions', () => {
  it('passes the input through to setProjectDefaultWorkflowProfile for the active project', async () => {
    const captured: { url: string; method: string; body: unknown }[] = []
    server.use(
      http.put('*/api/projects/:projectId/workflow-profile/default', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({
          success: true,
          data: { projectId: 'proj-1', profileId: 'mohist/github-pr' },
        })
      }),
    )

    await setProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      templateId: 'mohist/github-pr',
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/workflow-profile/default',
        method: 'PUT',
        body: { profileId: 'mohist/github-pr' },
      },
    ])
  })

  it('invalidates the project workflow-profile query on success', () => {
    const qc = createInvalidationClient()
    setProjectDefaultWorkflowProfileMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['project-workflow-profile', 'proj-1'] })
  })

  it('toasts "Project default workflow updated" on success', () => {
    setProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Project default workflow updated')
  })

  it('shows an error toast on failure', () => {
    setProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).onError(new Error('boom'))
    expect(toast.error).toHaveBeenCalledWith('boom')
  })
})

describe('resolveEffectiveDefaultWorkflowProfile', () => {
  it('returns project source when defaultTemplateId is set', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: 'mohist/github-pr', disabledWorkflowProfileIds: [] },
        [{ id: 'mohist/local', displayName: 'mohist/local', description: '', isDefault: true }],
      ),
    ).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'project',
      configuredTemplateId: 'mohist/github-pr',
    })
  })

  it('returns system source when defaultTemplateId is unset and an isDefault profile exists', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: null, disabledWorkflowProfileIds: [] },
        [{ id: 'mohist/local', displayName: 'mohist/local', description: '', isDefault: true }],
      ),
    ).toEqual({
      effectiveTemplateId: 'mohist/local',
      source: 'system',
      configuredTemplateId: null,
    })
  })

  it('skips a disabled configured default and falls through to the filtered enabled profile list', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: 'mohist/local', disabledWorkflowProfileIds: ['mohist/local'] },
        [{ id: 'mohist/github-pr', displayName: 'mohist/github-pr', description: '', isDefault: false }],
      ),
    ).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: 'mohist/local',
    })
  })

  it('does not return a disabled system default', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: 'mohist/local', disabledWorkflowProfileIds: ['mohist/local'] },
        [
          { id: 'mohist/local', displayName: 'mohist/local', description: '', isDefault: true },
          { id: 'mohist/github-pr', displayName: 'mohist/github-pr', description: '', isDefault: false },
        ],
      ),
    ).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: 'mohist/local',
    })
  })

  it('skips a mixed-case configured default that matches a disabled profile id', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: 'MOHIST/LOCAL', disabledWorkflowProfileIds: ['mohist/local'] },
        [{ id: 'mohist/github-pr', displayName: 'mohist/github-pr', description: '', isDefault: false }],
      ),
    ).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: 'MOHIST/LOCAL',
    })
  })

  it('returns the first enabled profile as effective default when no project or system default exists', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: null, disabledWorkflowProfileIds: [] },
        [{ id: 'mohist/github-pr', displayName: 'mohist/github-pr', description: '', isDefault: false }],
      ),
    ).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: null,
    })
  })

  it('returns none source when the filtered list is empty', () => {
    expect(
      resolveEffectiveDefaultWorkflowProfile(
        { projectId: 'proj-1', defaultTemplateId: null, disabledWorkflowProfileIds: [] },
        [],
      ),
    ).toEqual({
      effectiveTemplateId: '',
      source: 'none',
      configuredTemplateId: null,
    })
  })
})
