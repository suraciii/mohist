import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useCompletionTrend, useCompletionThroughput } from './completion-trend'

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

describe('useCompletionTrend', () => {
  it('uses the query key ["issues","metrics","completion","week", projectId]', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'week', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'week', 'proj-other'])
  })

  it('hard-codes the by-week bucketing (bucket=week is not in the query key, queryFn issues GET .../metrics/completion?bucket=week)', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            bucket: 'week',
            window: { from: '2026-06-15T00:00:00+00:00', to: '2026-06-22T00:00:00+00:00' },
            buckets: [{ boundary: '2026-06-15', completed: 0, failed: 0 }],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/completion?bucket=week')
    expect(calledPath).toContain('bucket=week')
    expect(calledPath).not.toContain('bucket=day')

    vi.unstubAllGlobals()
  })

  it('applies a modest staleTime around 60 seconds', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBeGreaterThanOrEqual(30_000)
    expect(config.staleTime).toBeLessThanOrEqual(5 * 60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('decodes a dense 12-week bucket response into the CompletionTrendResponse shape', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]

    const denseBuckets = Array.from({ length: 12 }, (_, index) => ({
      boundary: `2026-04-${String(index + 6).padStart(2, '0')}`,
      completed: index,
      failed: 0,
    }))

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            bucket: 'week',
            window: { from: '2026-04-06T00:00:00+00:00', to: '2026-06-29T00:00:00+00:00' },
            buckets: denseBuckets,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(data.bucket).toBe('week')
    expect(data.buckets).toHaveLength(12)
    expect(data.buckets[0].completed).toBe(0)
    expect(data.buckets[11].completed).toBe(11)
    expect(data.window.from).toBe('2026-04-06T00:00:00+00:00')

    vi.unstubAllGlobals()
  })
})

describe('useCompletionTrend range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'week', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionTrend('7d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'week', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionTrend('7d')
    useCompletionTrend('30d')
    useCompletionTrend('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL (composed with bucket=)', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionTrend('30d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { bucket: 'week', window: { from: '2026-05-02T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00' }, buckets: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/completion?bucket=week&range=30d')
    expect(calledPath).toContain('range=30d')
    expect(calledPath).toContain('bucket=week')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionTrend()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { bucket: 'week', window: { from: '2026-05-02T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00' }, buckets: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/completion?bucket=week')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})

describe('useCompletionThroughput range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'day', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionThroughput('90d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'day', '90d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionThroughput('7d')
    useCompletionThroughput('30d')
    useCompletionThroughput('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL (composed with bucket=day)', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionThroughput('90d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { bucket: 'day', window: { from: '2026-03-03T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00' }, buckets: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/completion?bucket=day&range=90d')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { bucket: 'day', window: { from: '2026-05-02T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00' }, buckets: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/completion?bucket=day')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})
