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

describe('useQualityMetrics range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'quality', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useQualityMetrics('7d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'quality', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useQualityMetrics('7d')
    useQualityMetrics('30d')
    useQualityMetrics('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useQualityMetrics('90d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window7d: { from: '2026-05-25T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00', sampleCount: 0, firstTimeRightRate: null, stages: [] },
            window30d: { from: '2026-03-03T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00', sampleCount: 0, firstTimeRightRate: null, stages: [] },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/quality?range=90d')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useQualityMetrics()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window7d: { from: '2026-06-20T00:00:00+00:00', to: '2026-06-27T00:00:00+00:00', sampleCount: 0, firstTimeRightRate: null, stages: [] },
            window30d: { from: '2026-05-28T00:00:00+00:00', to: '2026-06-27T00:00:00+00:00', sampleCount: 0, firstTimeRightRate: null, stages: [] },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/quality')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})
