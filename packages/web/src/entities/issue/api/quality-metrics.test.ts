import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { qualityMetricsQueryOptions } from './quality-metrics'

const QUALITY_DTO = {
  window: {
    from: '2026-05-28T00:00:00+00:00',
    to: '2026-06-27T00:00:00+00:00',
    sampleCount: 20,
    firstTimeRightRate: 0.75,
    stages: [
      { stage: 'plan', enteredCount: 20, reworkRate: 0.25 },
      { stage: 'build', enteredCount: 18, reworkRate: 0.05 },
    ],
  },
}

function recordQualityMetricsRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/metrics/quality', ({ request }) => {
      urls.push(new URL(request.url).pathname + new URL(request.url).search)
      return HttpResponse.json({ success: true, data: QUALITY_DTO })
    }),
  )
  return urls
}

useMswServer()

describe('qualityMetricsQueryOptions', () => {
  it('uses the query key ["issues","metrics","quality", projectId]', () => {
    expect(qualityMetricsQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'quality', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(qualityMetricsQueryOptions('proj-other').queryKey).toEqual(['issues', 'metrics', 'quality', 'proj-other'])
  })

  it('fetches the project quality metrics endpoint and returns the payload', async () => {
    const urls = recordQualityMetricsRequests()

    const data = await qualityMetricsQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/quality'])
    expect(data.window.sampleCount).toBe(20)
    expect(data.window.firstTimeRightRate).toBe(0.75)
  })

  it('uses a 60 second staleTime', () => {
    expect(qualityMetricsQueryOptions('proj-1').staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(qualityMetricsQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(qualityMetricsQueryOptions('proj-1').enabled).toBe(true)
  })
})

describe('qualityMetricsQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    expect(qualityMetricsQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'quality', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(qualityMetricsQueryOptions('proj-1', '7d').queryKey).toEqual(['issues', 'metrics', 'quality', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = qualityMetricsQueryOptions('proj-1', '7d').queryKey
    const key30 = qualityMetricsQueryOptions('proj-1', '30d').queryKey
    const key90 = qualityMetricsQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    const urls = recordQualityMetricsRequests()

    await qualityMetricsQueryOptions('proj-1', '90d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/quality?range=90d'])
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordQualityMetricsRequests()

    await qualityMetricsQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/quality'])
    expect(urls[0]).not.toContain('range=')
  })
})
