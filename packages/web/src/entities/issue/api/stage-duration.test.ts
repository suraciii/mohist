import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useStageDuration } from './stage-duration'

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

describe('useStageDuration', () => {
  it('uses the query key ["issues","metrics","stage-duration", projectId]', () => {
    useStageDuration()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'stage-duration', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })

    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'stage-duration', 'proj-other'])
  })

  it('issues GET .../metrics/stage-duration without a query string', async () => {
    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
            stages: [
              { stage: 'plan', sampleCount: 4, averageSeconds: 1800, medianSeconds: 1500 },
            ],
            flowEfficiencyRatio: 0.62,
            waitBreakout: {
              averageApprovalGateWaitSeconds: 600,
              averageInactiveGapSeconds: 1200,
            },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/stage-duration')

    vi.unstubAllGlobals()
  })

  it('applies a 60 second staleTime', () => {
    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('decodes the response into the StageDurationMetricsResponse shape', async () => {
    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
            stages: [
              { stage: 'plan', sampleCount: 2, averageSeconds: 1200, medianSeconds: 1100 },
              { stage: 'build', sampleCount: 2, averageSeconds: 3600, medianSeconds: 3400 },
              { stage: 'check', sampleCount: 1, averageSeconds: 7200, medianSeconds: 7200 },
              { stage: 'integrate', sampleCount: 0, averageSeconds: null, medianSeconds: null },
            ],
            flowEfficiencyRatio: 0.5,
            waitBreakout: {
              averageApprovalGateWaitSeconds: 300,
              averageInactiveGapSeconds: 900,
            },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(data.stages).toHaveLength(4)
    expect(data.stages[0].stage).toBe('plan')
    expect(data.stages[3].sampleCount).toBe(0)
    expect(data.flowEfficiencyRatio).toBe(0.5)
    expect(data.waitBreakout?.averageApprovalGateWaitSeconds).toBe(300)

    vi.unstubAllGlobals()
  })

  it('propagates fetch failures via the standard request path', async () => {
    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: false,
          error: { code: 'project_not_found', message: 'Project not found' },
        }),
        { status: 404, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(config.queryFn()).rejects.toThrow()

    vi.unstubAllGlobals()
  })
})

describe('useStageDuration range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'stage-duration', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useStageDuration('30d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'stage-duration', '30d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useStageDuration('7d')
    useStageDuration('30d')
    useStageDuration('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useStageDuration('7d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window: { from: '2026-05-25T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00' },
            stages: [],
            flowEfficiencyRatio: null,
            waitBreakout: null,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/stage-duration?range=7d')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useStageDuration()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window: { from: '2026-06-01T00:00:00+00:00', to: '2026-07-01T00:00:00+00:00' },
            stages: [],
            flowEfficiencyRatio: null,
            waitBreakout: null,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/stage-duration')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})