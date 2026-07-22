import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { toast } from 'sonner'
import {
  availableModelIdsQueryOptions,
  clearProjectDefaultWorkflowProfileMutationOptions,
  projectDefaultWorkflowProfileQueryOptions,
  resolveEffectiveDefaultWorkflowProfile,
  selectModelVariants,
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

describe('availableModelIdsQueryOptions', () => {
  it('reads from the same project-scoped opencode-model-ids query as useModelVariants', () => {
    expect(availableModelIdsQueryOptions('proj-1').queryKey).toEqual(['opencode-model-ids', 'opencode', 'proj-1'])
    expect(availableModelIdsQueryOptions('proj-1').enabled).toBe(true)
  })
})

describe('projectDefaultWorkflowProfileQueryOptions', () => {
  it('uses a project-scoped query key', () => {
    expect(projectDefaultWorkflowProfileQueryOptions('proj-1').queryKey).toEqual([
      'project-workflow-profile',
      'proj-1',
    ])
  })

  it('is disabled when projectId is missing', () => {
    expect(projectDefaultWorkflowProfileQueryOptions(null).enabled).toBe(false)
  })

  it('invokes getProjectDefaultWorkflowProfile(projectId) as the query function', async () => {
    const captured: string[] = []
    server.use(
      http.get('*/api/projects/:projectId/workflow-profile', ({ request }) => {
        captured.push(new URL(request.url).pathname)
        return HttpResponse.json({
          success: true,
          data: { projectId: 'proj-1', defaultTemplateId: 'mohist/github-pr' },
        })
      }),
    )

    const data = await projectDefaultWorkflowProfileQueryOptions('proj-1').queryFn()

    expect(captured).toEqual(['/api/projects/proj-1/workflow-profile'])
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
      http.put('*/api/projects/:projectId/workflow-profile/default-template', async ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method, body: await request.json() })
        return HttpResponse.json({
          success: true,
          data: { projectId: 'proj-1', defaultTemplateId: 'mohist/github-pr' },
        })
      }),
    )

    await setProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).mutationFn({
      templateId: 'mohist/github-pr',
    })

    expect(captured).toEqual([
      {
        url: '/api/projects/proj-1/workflow-profile/default-template',
        method: 'PUT',
        body: { templateId: 'mohist/github-pr' },
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

describe('clearProjectDefaultWorkflowProfileMutationOptions', () => {
  it('calls clearProjectDefaultWorkflowProfile for the active project', async () => {
    const captured: { url: string; method: string }[] = []
    server.use(
      http.delete('*/api/projects/:projectId/workflow-profile/default-template', ({ request }) => {
        captured.push({ url: new URL(request.url).pathname, method: request.method })
        return HttpResponse.json({
          success: true,
          data: { projectId: 'proj-1', defaultTemplateId: null },
        })
      }),
    )

    await clearProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).mutationFn()

    expect(captured).toEqual([
      { url: '/api/projects/proj-1/workflow-profile/default-template', method: 'DELETE' },
    ])
  })

  it('invalidates the project workflow-profile query on success', () => {
    const qc = createInvalidationClient()
    clearProjectDefaultWorkflowProfileMutationOptions('proj-1', qc).onSuccess()
    expect(qc.invalidateQueries).toHaveBeenCalledWith({ queryKey: ['project-workflow-profile', 'proj-1'] })
  })

  it('toasts "Project default workflow cleared" on success', () => {
    clearProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).onSuccess()
    expect(toast.success).toHaveBeenCalledWith('Project default workflow cleared')
  })

  it('shows an error toast on failure', () => {
    clearProjectDefaultWorkflowProfileMutationOptions('proj-1', createInvalidationClient()).onError(new Error('boom'))
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
