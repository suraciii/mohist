import { beforeEach, describe, expect, it, vi } from 'vitest'
import { costRollupQueryKey, fetchCostRollup, useCostRollup } from './cost-rollup'

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

describe('useCostRollup', () => {
  it('uses the query key ["agent","cost-rollup", projectId]', () => {
    useCostRollup()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'cost-rollup', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })

    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'cost-rollup', 'proj-other'])
  })

  it('fetches the project agent cost rollup endpoint', async () => {
    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            totalCost: { amount: 1.5, currency: 'USD', sampleCount: 3 },
            todayCost: { amount: 0.25, currency: 'USD', sampleCount: 1 },
            doneIssuesCount: 6,
            costPerShip: { amount: 0.25, currency: 'USD', sampleCount: 1 },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/cost')
    expect(data.totalCost.amount).toBe(1.5)
    expect(data.totalCost.currency).toBe('USD')
    expect(data.totalCost.sampleCount).toBe(3)
    expect(data.doneIssuesCount).toBe(6)
    expect(data.costPerShip.amount).toBe(0.25)
  })

  it('uses a 60 second staleTime', () => {
    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })
})

describe('costRollupQueryKey', () => {
  it('returns a project-scoped key when a projectId is provided', () => {
    expect(costRollupQueryKey('proj-1')).toEqual([
      'agent',
      'cost-rollup',
      'proj-1',
    ])
  })

  it('returns the shared prefix when projectId is missing', () => {
    expect(costRollupQueryKey()).toEqual(['agent', 'cost-rollup'])
    expect(costRollupQueryKey(null)).toEqual(['agent', 'cost-rollup'])
  })
})

describe('fetchCostRollup', () => {
  it('calls the agent cost endpoint for the given projectId', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            totalCost: { amount: 0, currency: 'USD', sampleCount: 2 },
            todayCost: { amount: null, currency: null, sampleCount: 0 },
            doneIssuesCount: 0,
            costPerShip: { amount: null, currency: null, sampleCount: 0 },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await fetchCostRollup('proj-1')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/cost')
    expect(data.totalCost.amount).toBe(0)
    expect(data.totalCost.sampleCount).toBe(2)
    expect(data.doneIssuesCount).toBe(0)
    expect(data.costPerShip.amount).toBeNull()
  })
})

describe('useCostRollup range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard FactoryStatusHeadline back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'cost-rollup', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCostRollup('7d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'cost-rollup', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCostRollup('7d')
    useCostRollup('30d')
    useCostRollup('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCostRollup('90d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            totalCost: { amount: 0, currency: 'USD', sampleCount: 0 },
            todayCost: { amount: 0, currency: 'USD', sampleCount: 0 },
            doneIssuesCount: 0,
            costPerShip: { amount: null, currency: null, sampleCount: 0 },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/cost?range=90d')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useCostRollup()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            totalCost: { amount: 0, currency: 'USD', sampleCount: 0 },
            todayCost: { amount: 0, currency: 'USD', sampleCount: 0 },
            doneIssuesCount: 0,
            costPerShip: { amount: null, currency: null, sampleCount: 0 },
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/cost')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})