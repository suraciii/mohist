import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  useCreateLabelDefinition,
  useDeleteLabelDefinition,
  useLabelCatalog,
  useUpdateLabelDefinition,
} from './queries'

const useQueryMock = vi.fn()
const useMutationMock = vi.fn()
const useQueryClientMock = vi.fn()
const useProjectMock = vi.fn()
const getLabelCatalogMock = vi.fn()
const createLabelDefinitionMock = vi.fn()
const updateLabelDefinitionMock = vi.fn()
const deleteLabelDefinitionMock = vi.fn()
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

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>()
  return {
    ...actual,
    getLabelCatalog: (...args: unknown[]) => getLabelCatalogMock(...args),
    createLabelDefinition: (...args: unknown[]) => createLabelDefinitionMock(...args),
    updateLabelDefinition: (...args: unknown[]) => updateLabelDefinitionMock(...args),
    deleteLabelDefinition: (...args: unknown[]) => deleteLabelDefinitionMock(...args),
  }
})

beforeEach(() => {
  useQueryMock.mockReset()
  useMutationMock.mockReset()
  useProjectMock.mockReset()
  getLabelCatalogMock.mockReset()
  createLabelDefinitionMock.mockReset()
  updateLabelDefinitionMock.mockReset()
  deleteLabelDefinitionMock.mockReset()
  invalidateQueriesMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryClientMock.mockReturnValue({ invalidateQueries: invalidateQueriesMock })
  useQueryMock.mockReturnValue({ data: [], isLoading: false })
  getLabelCatalogMock.mockResolvedValue([])
})

describe('useLabelCatalog', () => {
  it('uses a project-scoped query key', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useLabelCatalog()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['label-catalog', 'proj-1'])
  })

  it('invokes getLabelCatalog(projectId) as the query function', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    getLabelCatalogMock.mockResolvedValue([])

    useLabelCatalog()

    const config = useQueryMock.mock.calls[0][0]
    await config.queryFn()
    expect(getLabelCatalogMock).toHaveBeenCalledWith('proj-1')
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useLabelCatalog()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })
})

describe('useCreateLabelDefinition', () => {
  it('passes the input through to createLabelDefinition for the active project', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCreateLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    config.mutationFn({ key: 'module', description: 'subsystem' })

    expect(createLabelDefinitionMock).toHaveBeenCalledWith('proj-1', {
      key: 'module',
      description: 'subsystem',
    })
  })

  it('rejects when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useCreateLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    expect(() => config.mutationFn({ key: 'module', description: 'x' })).toThrow(
      'Project is required',
    )
  })

  it('invalidates the catalog query on success', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCreateLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    config.onSuccess()

    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['label-catalog', 'proj-1'],
    })
  })
})

describe('useUpdateLabelDefinition', () => {
  it('passes the key + patch to updateLabelDefinition', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useUpdateLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    config.mutationFn({ key: 'module', patch: { description: 'new' } })

    expect(updateLabelDefinitionMock).toHaveBeenCalledWith('proj-1', 'module', {
      description: 'new',
    })
  })

  it('invalidates the catalog query on success', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useUpdateLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    config.onSuccess()

    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['label-catalog', 'proj-1'],
    })
  })
})

describe('useDeleteLabelDefinition', () => {
  it('passes the key to deleteLabelDefinition', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeleteLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    config.mutationFn('module')

    expect(deleteLabelDefinitionMock).toHaveBeenCalledWith('proj-1', 'module')
  })

  it('rejects when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useDeleteLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    expect(() => config.mutationFn('module')).toThrow('Project is required')
  })

  it('invalidates the catalog query on success', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeleteLabelDefinition()

    const config = useMutationMock.mock.calls[0][0]
    config.onSuccess()

    expect(invalidateQueriesMock).toHaveBeenCalledWith({
      queryKey: ['label-catalog', 'proj-1'],
    })
  })
})
