import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { costRollupQueryKey, costRollupQueryOptions, fetchCostRollup } from './cost-rollup'

const ROLLUP_DTO = {
  totalCost: { amount: 1.5, currency: 'USD', sampleCount: 3 },
  todayCost: { amount: 0.25, currency: 'USD', sampleCount: 1 },
  doneIssuesCount: 6,
  costPerShip: { amount: 0.25, currency: 'USD', sampleCount: 1 },
}

const EMPTY_ROLLUP_DTO = {
  totalCost: { amount: 0, currency: 'USD', sampleCount: 2 },
  todayCost: { amount: null, currency: null, sampleCount: 0 },
  doneIssuesCount: 0,
  costPerShip: { amount: null, currency: null, sampleCount: 0 },
}

function recordCostRollupRequests(dto: typeof ROLLUP_DTO | typeof EMPTY_ROLLUP_DTO = ROLLUP_DTO) {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/agent/cost', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: dto })
    }),
  )
  return urls
}

useMswServer()

describe('costRollupQueryOptions', () => {
  it('uses the query key ["agent","cost-rollup", projectId]', () => {
    expect(costRollupQueryOptions('proj-1').queryKey).toEqual(['agent', 'cost-rollup', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(costRollupQueryOptions('proj-other').queryKey).toEqual(['agent', 'cost-rollup', 'proj-other'])
  })

  it('fetches the project agent cost rollup endpoint', async () => {
    const urls = recordCostRollupRequests()

    const data = await costRollupQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agent/cost'])
    expect(data.totalCost.amount).toBe(1.5)
    expect(data.totalCost.currency).toBe('USD')
    expect(data.totalCost.sampleCount).toBe(3)
    expect(data.doneIssuesCount).toBe(6)
    expect(data.costPerShip.amount).toBe(0.25)
  })

  it('uses a 60 second staleTime', () => {
    expect(costRollupQueryOptions('proj-1').staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(costRollupQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(costRollupQueryOptions('proj-1').enabled).toBe(true)
  })
})

describe('costRollupQueryKey', () => {
  it('returns a project-scoped key when a projectId is provided', () => {
    expect(costRollupQueryKey('proj-1')).toEqual(['agent', 'cost-rollup', 'proj-1'])
  })

  it('returns the shared prefix when projectId is missing', () => {
    expect(costRollupQueryKey()).toEqual(['agent', 'cost-rollup'])
    expect(costRollupQueryKey(null)).toEqual(['agent', 'cost-rollup'])
  })
})

describe('fetchCostRollup', () => {
  it('calls the agent cost endpoint for the given projectId', async () => {
    const urls = recordCostRollupRequests(EMPTY_ROLLUP_DTO)

    const data = await fetchCostRollup('proj-1')

    expect(urls).toEqual(['/api/projects/proj-1/agent/cost'])
    expect(data.totalCost.amount).toBe(0)
    expect(data.totalCost.sampleCount).toBe(2)
    expect(data.doneIssuesCount).toBe(0)
    expect(data.costPerShip.amount).toBeNull()
  })
})

describe('costRollupQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard FactoryStatusHeadline back-compat)', () => {
    expect(costRollupQueryOptions('proj-1').queryKey).toEqual(['agent', 'cost-rollup', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(costRollupQueryOptions('proj-1', '7d').queryKey).toEqual(['agent', 'cost-rollup', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = costRollupQueryOptions('proj-1', '7d').queryKey
    const key30 = costRollupQueryOptions('proj-1', '30d').queryKey
    const key90 = costRollupQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    const urls = recordCostRollupRequests(EMPTY_ROLLUP_DTO)

    await costRollupQueryOptions('proj-1', '90d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agent/cost?range=90d'])
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordCostRollupRequests(EMPTY_ROLLUP_DTO)

    await costRollupQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agent/cost'])
  })
})
