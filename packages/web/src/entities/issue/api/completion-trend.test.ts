import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { completionThroughputQueryOptions, completionTrendQueryOptions } from './completion-trend'

const WEEK_TREND_DTO = {
  bucket: 'week',
  window: { from: '2026-06-15T00:00:00+00:00', to: '2026-06-22T00:00:00+00:00' },
  buckets: [{ boundary: '2026-06-15', completed: 0, failed: 0 }],
}

const DENSE_WEEK_TREND_DTO = {
  bucket: 'week',
  window: { from: '2026-04-06T00:00:00+00:00', to: '2026-06-29T00:00:00+00:00' },
  buckets: Array.from({ length: 12 }, (_, index) => ({
    boundary: `2026-04-${String(index + 6).padStart(2, '0')}`,
    completed: index,
    failed: 0,
  })),
}

const DAY_TREND_DTO = {
  bucket: 'day',
  window: { from: '2026-05-02T00:00:00+00:00', to: '2026-06-01T00:00:00+00:00' },
  buckets: [],
}

function recordCompletionTrendRequests(dto: object = WEEK_TREND_DTO) {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/metrics/completion', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: dto })
    }),
  )
  return urls
}

useMswServer()

describe('completionTrendQueryOptions', () => {
  it('uses the query key ["issues","metrics","completion","week", projectId]', () => {
    expect(completionTrendQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'completion', 'week', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(completionTrendQueryOptions('proj-other').queryKey).toEqual(['issues', 'metrics', 'completion', 'week', 'proj-other'])
  })

  it('hard-codes the by-week bucketing (bucket=week is not in the query key, queryFn issues GET .../metrics/completion?bucket=week)', async () => {
    const urls = recordCompletionTrendRequests()

    await completionTrendQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/completion?bucket=week'])
    expect(urls[0]).toContain('bucket=week')
    expect(urls[0]).not.toContain('bucket=day')
  })

  it('applies a modest staleTime around 60 seconds', () => {
    const { staleTime } = completionTrendQueryOptions('proj-1')
    expect(staleTime).toBeGreaterThanOrEqual(30_000)
    expect(staleTime).toBeLessThanOrEqual(5 * 60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(completionTrendQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(completionTrendQueryOptions('proj-1').enabled).toBe(true)
  })

  it('decodes a dense 12-week bucket response into the CompletionTrendResponse shape', async () => {
    recordCompletionTrendRequests(DENSE_WEEK_TREND_DTO)

    const data = await completionTrendQueryOptions('proj-1').queryFn()

    expect(data.bucket).toBe('week')
    expect(data.buckets).toHaveLength(12)
    expect(data.buckets[0].completed).toBe(0)
    expect(data.buckets[11].completed).toBe(11)
    expect(data.window.from).toBe('2026-04-06T00:00:00+00:00')
  })
})

describe('completionTrendQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    expect(completionTrendQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'completion', 'week', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(completionTrendQueryOptions('proj-1', '7d').queryKey).toEqual(['issues', 'metrics', 'completion', 'week', '7d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = completionTrendQueryOptions('proj-1', '7d').queryKey
    const key30 = completionTrendQueryOptions('proj-1', '30d').queryKey
    const key90 = completionTrendQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL (composed with bucket=)', async () => {
    const urls = recordCompletionTrendRequests()

    await completionTrendQueryOptions('proj-1', '30d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/completion?bucket=week&range=30d'])
    expect(urls[0]).toContain('range=30d')
    expect(urls[0]).toContain('bucket=week')
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordCompletionTrendRequests()

    await completionTrendQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/completion?bucket=week'])
    expect(urls[0]).not.toContain('range=')
  })
})

describe('completionThroughputQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    expect(completionThroughputQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'completion', 'day', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(completionThroughputQueryOptions('proj-1', '90d').queryKey).toEqual(['issues', 'metrics', 'completion', 'day', '90d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = completionThroughputQueryOptions('proj-1', '7d').queryKey
    const key30 = completionThroughputQueryOptions('proj-1', '30d').queryKey
    const key90 = completionThroughputQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL (composed with bucket=day)', async () => {
    const urls = recordCompletionTrendRequests(DAY_TREND_DTO)

    await completionThroughputQueryOptions('proj-1', '90d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/completion?bucket=day&range=90d'])
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordCompletionTrendRequests(DAY_TREND_DTO)

    await completionThroughputQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/completion?bucket=day'])
    expect(urls[0]).not.toContain('range=')
  })
})
