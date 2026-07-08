import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { completionThroughputQueryOptions } from './completion-trend'

const THROUGHPUT_DTO = {
  bucket: 'day',
  window: { from: '2026-06-01T00:00:00+00:00', to: '2026-06-30T00:00:00+00:00' },
  buckets: [{ boundary: '2026-06-01', completed: 0, failed: 0 }],
}

const DENSE_BUCKETS = Array.from({ length: 30 }, (_, index) => ({
  boundary: `2026-06-${String(index + 1).padStart(2, '0')}`,
  completed: index,
  failed: index % 3 === 0 ? 1 : 0,
}))

function recordCompletionRequests(dto: unknown = THROUGHPUT_DTO) {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/metrics/completion', ({ request }) => {
      urls.push(new URL(request.url).pathname + new URL(request.url).search)
      return HttpResponse.json({ success: true, data: dto })
    }),
  )
  return urls
}

useMswServer()

describe('completionThroughputQueryOptions', () => {
  it('uses the query key ["issues","metrics","completion","day", projectId]', () => {
    expect(completionThroughputQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'completion', 'day', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(completionThroughputQueryOptions('proj-other').queryKey).toEqual(['issues', 'metrics', 'completion', 'day', 'proj-other'])
  })

  it('issues GET .../metrics/completion?bucket=day', async () => {
    const urls = recordCompletionRequests()

    await completionThroughputQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/completion?bucket=day'])
    expect(urls[0]).toContain('bucket=day')
    expect(urls[0]).not.toContain('bucket=week')
  })

  it('applies a modest staleTime around 60 seconds', () => {
    const { staleTime } = completionThroughputQueryOptions('proj-1')
    expect(staleTime).toBeGreaterThanOrEqual(30_000)
    expect(staleTime).toBeLessThanOrEqual(5 * 60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(completionThroughputQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(completionThroughputQueryOptions('proj-1').enabled).toBe(true)
  })

  it('decodes a dense 30-bucket response carrying per-bucket Completed and Failed', async () => {
    recordCompletionRequests({
      bucket: 'day',
      window: { from: '2026-06-01T00:00:00+00:00', to: '2026-06-30T00:00:00+00:00' },
      buckets: DENSE_BUCKETS,
    })

    const data = await completionThroughputQueryOptions('proj-1').queryFn()

    expect(data.bucket).toBe('day')
    expect(data.buckets).toHaveLength(30)
    expect(data.buckets[0].completed).toBe(0)
    expect(data.buckets[0].failed).toBe(1)
    expect(data.buckets[1].completed).toBe(1)
    expect(data.buckets[1].failed).toBe(0)
    expect(data.buckets[29].completed).toBe(29)
    expect(data.window.from).toBe('2026-06-01T00:00:00+00:00')
  })
})
