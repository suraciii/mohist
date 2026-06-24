import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useModelVariants } from './queries'

const useQueryMock = vi.fn()
const useProjectMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQuery: (...args: unknown[]) => useQueryMock(...args),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

beforeEach(() => {
  useQueryMock.mockReset()
  useProjectMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
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