import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useIssueTemplate, useIssueTemplates } from './queries'
import * as clientModule from './client'

const useQueryMock = vi.fn()
const useProjectMock = vi.fn()
const getIssueTemplatesMock = vi.fn()
const getIssueTemplateMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQuery: (...args: unknown[]) => useQueryMock(...args),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>()
  return {
    ...actual,
    getIssueTemplates: (...args: unknown[]) => getIssueTemplatesMock(...args),
    getIssueTemplate: (...args: unknown[]) => getIssueTemplateMock(...args),
  }
})

beforeEach(() => {
  useQueryMock.mockReset()
  useProjectMock.mockReset()
  getIssueTemplatesMock.mockReset()
  getIssueTemplateMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryMock.mockReturnValue({ data: [], isLoading: false })
  getIssueTemplatesMock.mockResolvedValue([])
  getIssueTemplateMock.mockResolvedValue({
    id: 'mohist/default',
    name: 'Mohist Default',
    about: '',
    isDefault: true,
    suitableFor: [],
    defaults: null,
    sections: [],
    source: 'builtin',
  })
  void clientModule
})

describe('useIssueTemplates', () => {
  it('uses a project-scoped query key', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useIssueTemplates()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issue-templates', 'proj-1'])
  })

  it('invokes getIssueTemplates(projectId) as the query function', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    getIssueTemplatesMock.mockResolvedValue([])

    useIssueTemplates()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')
    await config.queryFn()
    expect(getIssueTemplatesMock).toHaveBeenCalledWith('proj-1')
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useIssueTemplates()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useIssueTemplates()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })
})

describe('useIssueTemplate', () => {
  it('uses a query key keyed on (projectId, name)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useIssueTemplate('mohist/default')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issue-template', 'proj-1', 'mohist/default'])
  })

  it('invokes getIssueTemplate(name, projectId) as the query function', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    getIssueTemplateMock.mockResolvedValue({})

    useIssueTemplate('mohist/default')

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')
    await config.queryFn()
    expect(getIssueTemplateMock).toHaveBeenCalledWith('mohist/default', 'proj-1')
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useIssueTemplate('mohist/default')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is disabled when name is null', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useIssueTemplate(null)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when both projectId and name are set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useIssueTemplate('mohist/default')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })
})
