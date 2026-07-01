import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  useClearProjectDefaultWorkflowProfile,
  useEffectiveDefaultWorkflowProfile,
  useModelVariants,
  useProjectDefaultWorkflowProfile,
  useSetProjectDefaultWorkflowProfile,
} from './queries'

const useQueryMock = vi.fn()
const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const toastSuccessMock = vi.fn()
const toastErrorMock = vi.fn()
const getProjectDefaultWorkflowProfileMock = vi.fn()
const setProjectDefaultWorkflowProfileMock = vi.fn()
const clearProjectDefaultWorkflowProfileMock = vi.fn()
const getWorkflowProfilesMock = vi.fn()
const invalidateQueriesMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@tanstack/react-query')>()
  return {
    ...actual,
    useQuery: (...args: unknown[]) => useQueryMock(...args),
    useMutation: (...args: unknown[]) => useMutationMock(...args),
    useQueryClient: () => useQueryClientMock(),
  }
})

vi.mock('sonner', () => ({
  toast: {
    success: (...args: unknown[]) => toastSuccessMock(...args),
    error: (...args: unknown[]) => toastErrorMock(...args),
  },
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>()
  return {
    ...actual,
    getProjectDefaultWorkflowProfile: (...args: unknown[]) => getProjectDefaultWorkflowProfileMock(...args),
    setProjectDefaultWorkflowProfile: (...args: unknown[]) => setProjectDefaultWorkflowProfileMock(...args),
    clearProjectDefaultWorkflowProfile: (...args: unknown[]) => clearProjectDefaultWorkflowProfileMock(...args),
    getWorkflowProfiles: (...args: unknown[]) => getWorkflowProfilesMock(...args),
  }
})

beforeEach(() => {
  useQueryMock.mockReset()
  useMutationMock.mockReset()
  useQueryClientMock.mockReset()
  useProjectMock.mockReset()
  toastSuccessMock.mockReset()
  toastErrorMock.mockReset()
  getProjectDefaultWorkflowProfileMock.mockReset()
  setProjectDefaultWorkflowProfileMock.mockReset()
  clearProjectDefaultWorkflowProfileMock.mockReset()
  getWorkflowProfilesMock.mockReset()
  invalidateQueriesMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
  useMutationMock.mockReturnValue({ mutate: vi.fn() })
  getProjectDefaultWorkflowProfileMock.mockResolvedValue({ projectId: 'proj-1', defaultTemplateId: null })
  setProjectDefaultWorkflowProfileMock.mockResolvedValue({ projectId: 'proj-1', defaultTemplateId: 'mohist/github-pr' })
  clearProjectDefaultWorkflowProfileMock.mockResolvedValue({ projectId: 'proj-1', defaultTemplateId: null })
  getWorkflowProfilesMock.mockResolvedValue([])
})

describe('useModelVariants', () => {
  it('returns an empty map when the underlying discovery query has no data', () => {
    useQueryMock.mockReturnValue({ data: undefined, isLoading: true })

    expect(useModelVariants()).toEqual({})
  })

  it('returns the populated modelVariants map when the underlying query exposes variants', () => {
    const variants = {
      'anthropic/claude-sonnet-4': ['low', 'medium', 'high'],
      'openai/gpt-5.1': ['low', 'high', 'max'],
    }
    useQueryMock.mockReturnValue({
      data: {
        models: ['anthropic/claude-sonnet-4', 'openai/gpt-5.1'],
        modelVariants: variants,
      },
    })

    expect(useModelVariants()).toEqual(variants)
  })

  it('returns an empty map when discovery data is present but no model exposes variants', () => {
    useQueryMock.mockReturnValue({
      data: {
        models: ['openai/gpt-4'],
        modelVariants: {},
      },
    })

    expect(useModelVariants()).toEqual({})
  })

  it('does not throw when the underlying data is malformed', () => {
    useQueryMock.mockReturnValue({ data: { models: ['openai/gpt-4'] } })

    expect(() => useModelVariants()).not.toThrow()
    expect(useModelVariants()).toEqual({})
  })

  it('reads from the same project-scoped opencode-model-ids query as useAvailableModelIds', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useModelVariants()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['opencode-model-ids', 'proj-1'])
    expect(config.enabled).toBe(true)
  })
})

describe('useProjectDefaultWorkflowProfile', () => {
  it('uses a project-scoped query key', () => {
    useProjectDefaultWorkflowProfile()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['project-workflow-profile', 'proj-1'])
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useProjectDefaultWorkflowProfile()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('invokes getProjectDefaultWorkflowProfile(projectId) as the query function', async () => {
    getProjectDefaultWorkflowProfileMock.mockResolvedValue({ projectId: 'proj-1', defaultTemplateId: 'mohist/github-pr' })

    useProjectDefaultWorkflowProfile()

    const config = useQueryMock.mock.calls[0][0]
    await config.queryFn()
    expect(getProjectDefaultWorkflowProfileMock).toHaveBeenCalledWith('proj-1')
  })
})

describe('useSetProjectDefaultWorkflowProfile', () => {
  it('passes the input through to setProjectDefaultWorkflowProfile for the active project', () => {
    useSetProjectDefaultWorkflowProfile()

    const config = useMutationMock.mock.calls[0][0]
    config.mutationFn({ templateId: 'mohist/github-pr' })

    expect(setProjectDefaultWorkflowProfileMock).toHaveBeenCalledWith('proj-1', 'mohist/github-pr')
  })

  it('invalidates the project workflow-profile query on success', () => {
    useSetProjectDefaultWorkflowProfile()

    const config = useMutationMock.mock.calls[0][0]
    config.onSuccess()

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['project-workflow-profile', 'proj-1'] })
    expect(toastSuccessMock).toHaveBeenCalled()
  })

  it('shows an error toast on failure', () => {
    useSetProjectDefaultWorkflowProfile()

    const config = useMutationMock.mock.calls[0][0]
    config.onError(new Error('boom'))

    expect(toastErrorMock).toHaveBeenCalledWith('boom')
  })
})

describe('useClearProjectDefaultWorkflowProfile', () => {
  it('calls clearProjectDefaultWorkflowProfile for the active project', () => {
    useClearProjectDefaultWorkflowProfile()

    const config = useMutationMock.mock.calls[0][0]
    config.mutationFn()

    expect(clearProjectDefaultWorkflowProfileMock).toHaveBeenCalledWith('proj-1')
  })

  it('invalidates the project workflow-profile query on success', () => {
    useClearProjectDefaultWorkflowProfile()

    const config = useMutationMock.mock.calls[0][0]
    config.onSuccess()

    expect(invalidateQueriesMock).toHaveBeenCalledWith({ queryKey: ['project-workflow-profile', 'proj-1'] })
    expect(toastSuccessMock).toHaveBeenCalled()
  })

  it('shows an error toast on failure', () => {
    useClearProjectDefaultWorkflowProfile()

    const config = useMutationMock.mock.calls[0][0]
    config.onError(new Error('boom'))

    expect(toastErrorMock).toHaveBeenCalledWith('boom')
  })
})

describe('useEffectiveDefaultWorkflowProfile', () => {
  function mockQueryData(projectDefault: string | null, profiles: Array<{ id: string; isDefault: boolean }>) {
    useQueryMock.mockImplementation((config) => {
      if (config.queryKey[0] === 'project-workflow-profile') {
        return { data: { projectId: 'proj-1', defaultTemplateId: projectDefault, disabledWorkflowProfileIds: [] } }
      }
      if (config.queryKey[0] === 'workflow-templates') {
        return {
          data: profiles.map((p) => ({
            id: p.id,
            displayName: p.id,
            description: '',
            isDefault: p.isDefault,
          })),
        }
      }
      return { data: undefined }
    })
  }

  function mockQueryDataWithDisabled(
    projectDefault: string | null,
    disabledWorkflowProfileIds: string[],
    profiles: Array<{ id: string; isDefault: boolean }>,
  ) {
    useQueryMock.mockImplementation((config) => {
      if (config.queryKey[0] === 'project-workflow-profile') {
        return { data: { projectId: 'proj-1', defaultTemplateId: projectDefault, disabledWorkflowProfileIds } }
      }
      if (config.queryKey[0] === 'workflow-templates') {
        return {
          data: profiles.map((p) => ({
            id: p.id,
            displayName: p.id,
            description: '',
            isDefault: p.isDefault,
          })),
        }
      }
      return { data: undefined }
    })
  }

  it('returns project source when defaultTemplateId is set', () => {
    mockQueryData('mohist/github-pr', [{ id: 'mohist/local', isDefault: true }])

    const result = useEffectiveDefaultWorkflowProfile()

    expect(result).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'project',
      configuredTemplateId: 'mohist/github-pr',
    })
  })

  it('returns system source when defaultTemplateId is unset and an isDefault profile exists', () => {
    mockQueryData(null, [{ id: 'mohist/local', isDefault: true }])

    const result = useEffectiveDefaultWorkflowProfile()

    expect(result).toEqual({
      effectiveTemplateId: 'mohist/local',
      source: 'system',
      configuredTemplateId: null,
    })
  })

  it('skips a disabled configured default and falls through to the filtered enabled profile list', () => {
    mockQueryDataWithDisabled(
      'mohist/local',
      ['mohist/local'],
      [{ id: 'mohist/github-pr', isDefault: false }],
    )

    const result = useEffectiveDefaultWorkflowProfile()

    expect(result).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: 'mohist/local',
    })
  })

  it('skips a mixed-case configured default that matches a disabled profile id', () => {
    mockQueryDataWithDisabled(
      'MOHIST/LOCAL',
      ['mohist/local'],
      [{ id: 'mohist/github-pr', isDefault: false }],
    )

    const result = useEffectiveDefaultWorkflowProfile()

    expect(result).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: 'MOHIST/LOCAL',
    })
  })

  it('returns the first enabled profile as effective default when no project or system default exists', () => {
    mockQueryData(null, [{ id: 'mohist/github-pr', isDefault: false }])

    const result = useEffectiveDefaultWorkflowProfile()

    expect(result).toEqual({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: null,
    })
  })

  it('returns none source when the filtered list is empty', () => {
    mockQueryData(null, [])

    const result = useEffectiveDefaultWorkflowProfile()

    expect(result).toEqual({
      effectiveTemplateId: '',
      source: 'none',
      configuredTemplateId: null,
    })
  })
})
