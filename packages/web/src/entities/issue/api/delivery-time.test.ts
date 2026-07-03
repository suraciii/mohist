import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useDeliveryTime } from './delivery-time'

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

describe('useDeliveryTime', () => {
  it('uses the query key ["issues","metrics","delivery-time", projectId]', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'delivery-time', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'delivery-time', 'proj-other'])
  })

  it('issues GET .../metrics/delivery-time without a query string', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            points: [
              {
                issueNumber: 7,
                completedAt: '2026-06-25T12:00:00+00:00',
                leadDays: 4.25,
                cycleDays: 2.1,
              },
            ],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/delivery-time')

    vi.unstubAllGlobals()
  })

  it('applies a 60 second staleTime', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBeGreaterThanOrEqual(30_000)
    expect(config.staleTime).toBeLessThanOrEqual(5 * 60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('decodes a points array into the DeliveryTimeMetricsResponse shape', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            points: [
              { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: 1.5 },
              { issueNumber: 2, completedAt: '2026-06-20T00:00:00+00:00', leadDays: 5.0, cycleDays: null },
            ],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(data.points).toHaveLength(2)
    expect(data.points[0].issueNumber).toBe(1)
    expect(data.points[1].cycleDays).toBeNull()

    vi.unstubAllGlobals()
  })
})

describe('useDeliveryTime range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'delivery-time', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeliveryTime('7d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'delivery-time', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeliveryTime('7d')
    useDeliveryTime('30d')
    useDeliveryTime('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeliveryTime('90d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { points: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/delivery-time?range=90d')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useDeliveryTime()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { points: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/delivery-time')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})
