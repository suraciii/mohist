import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useQualityMetrics } from './quality-metrics'

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
  vi.unstubAllGlobals()
  useQueryMock.mockReset()
  useProjectMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
})

describe('useQualityMetrics', () => {
  it('uses the query key ["issues","metrics","quality", projectId]', () => {
    useQualityMetrics()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'quality', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })

    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'quality', 'proj-other'])
  })

  it('fetches the project quality metrics endpoint', async () => {
    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window7d: {
              from: '2026-06-20T00:00:00+00:00',
              to: '2026-06-27T00:00:00+00:00',
              sampleCount: 5,
              firstTimeRightRate: 0.8,
              stages: [
                { stage: 'plan', enteredCount: 5, reworkRate: 0.2 },
                { stage: 'build', enteredCount: 4, reworkRate: 0 },
              ],
            },
            window30d: {
              from: '2026-05-28T00:00:00+00:00',
              to: '2026-06-27T00:00:00+00:00',
              sampleCount: 20,
              firstTimeRightRate: 0.75,
              stages: [
                { stage: 'plan', enteredCount: 20, reworkRate: 0.25 },
                { stage: 'build', enteredCount: 18, reworkRate: 0.05 },
              ],
            },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/quality')
    expect(data.window7d.sampleCount).toBe(5)
    expect(data.window7d.firstTimeRightRate).toBe(0.8)
    expect(data.window30d.sampleCount).toBe(20)
    expect(data.window30d.firstTimeRightRate).toBe(0.75)
  })

  it('uses a 60 second staleTime', () => {
    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })
})
