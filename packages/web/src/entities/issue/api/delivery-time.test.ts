import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { deliveryTimeQueryOptions } from './delivery-time'

const DELIVERY_TIME_DTO = {
  points: [
    { issueNumber: 7, completedAt: '2026-06-25T12:00:00+00:00', leadDays: 4.25, cycleDays: 2.1 },
  ],
}

const TWO_POINT_DTO = {
  points: [
    { issueNumber: 1, completedAt: '2026-06-10T00:00:00+00:00', leadDays: 2.0, cycleDays: 1.5 },
    { issueNumber: 2, completedAt: '2026-06-20T00:00:00+00:00', leadDays: 5.0, cycleDays: null },
  ],
}

const EMPTY_DTO = { points: [] }

function recordDeliveryTimeRequests(dto: object = DELIVERY_TIME_DTO) {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/metrics/delivery-time', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: dto })
    }),
  )
  return urls
}

useMswServer()

describe('deliveryTimeQueryOptions', () => {
  it('uses the query key ["issues","metrics","delivery-time", projectId]', () => {
    expect(deliveryTimeQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'delivery-time', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(deliveryTimeQueryOptions('proj-other').queryKey).toEqual(['issues', 'metrics', 'delivery-time', 'proj-other'])
  })

  it('issues GET .../metrics/delivery-time without a query string', async () => {
    const urls = recordDeliveryTimeRequests()

    await deliveryTimeQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/delivery-time'])
  })

  it('applies a 60 second staleTime', () => {
    const { staleTime } = deliveryTimeQueryOptions('proj-1')
    expect(staleTime).toBeGreaterThanOrEqual(30_000)
    expect(staleTime).toBeLessThanOrEqual(5 * 60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(deliveryTimeQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(deliveryTimeQueryOptions('proj-1').enabled).toBe(true)
  })

  it('decodes a points array into the DeliveryTimeMetricsResponse shape', async () => {
    recordDeliveryTimeRequests(TWO_POINT_DTO)

    const data = await deliveryTimeQueryOptions('proj-1').queryFn()

    expect(data.points).toHaveLength(2)
    expect(data.points[0].issueNumber).toBe(1)
    expect(data.points[1].cycleDays).toBeNull()
  })
})

describe('deliveryTimeQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    expect(deliveryTimeQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'delivery-time', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(deliveryTimeQueryOptions('proj-1', '7d').queryKey).toEqual(['issues', 'metrics', 'delivery-time', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = deliveryTimeQueryOptions('proj-1', '7d').queryKey
    const key30 = deliveryTimeQueryOptions('proj-1', '30d').queryKey
    const key90 = deliveryTimeQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    const urls = recordDeliveryTimeRequests(EMPTY_DTO)

    await deliveryTimeQueryOptions('proj-1', '90d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/delivery-time?range=90d'])
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordDeliveryTimeRequests(EMPTY_DTO)

    await deliveryTimeQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/delivery-time'])
    expect(urls[0]).not.toContain('range=')
  })
})
