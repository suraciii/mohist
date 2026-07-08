import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { stageDurationQueryOptions } from './stage-duration'

const STAGE_DURATION_DTO = {
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
}

function recordStageDurationRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/metrics/stage-duration', ({ request }) => {
      urls.push(new URL(request.url).pathname + new URL(request.url).search)
      return HttpResponse.json({ success: true, data: STAGE_DURATION_DTO })
    }),
  )
  return urls
}

useMswServer()

describe('stageDurationQueryOptions', () => {
  it('uses the query key ["issues","metrics","stage-duration", projectId]', () => {
    expect(stageDurationQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'stage-duration', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(stageDurationQueryOptions('proj-other').queryKey).toEqual(['issues', 'metrics', 'stage-duration', 'proj-other'])
  })

  it('issues GET .../metrics/stage-duration without a query string', async () => {
    const urls = recordStageDurationRequests()

    await stageDurationQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/stage-duration'])
  })

  it('applies a 60 second staleTime', () => {
    expect(stageDurationQueryOptions('proj-1').staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(stageDurationQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(stageDurationQueryOptions('proj-1').enabled).toBe(true)
  })

  it('decodes the response into the StageDurationMetricsResponse shape', async () => {
    recordStageDurationRequests()

    const data = await stageDurationQueryOptions('proj-1').queryFn()

    expect(data.stages).toHaveLength(4)
    expect(data.stages[0].stage).toBe('plan')
    expect(data.stages[3].sampleCount).toBe(0)
    expect(data.flowEfficiencyRatio).toBe(0.5)
    expect(data.waitBreakout?.averageApprovalGateWaitSeconds).toBe(300)
  })

  it('propagates fetch failures via the standard request path', async () => {
    server.use(
      http.get('*/api/projects/:projectId/issues/metrics/stage-duration', () =>
        HttpResponse.json(
          { success: false, error: { code: 'project_not_found', message: 'Project not found' } },
          { status: 404 },
        ),
      ),
    )

    await expect(stageDurationQueryOptions('proj-1').queryFn()).rejects.toThrow()
  })
})

describe('stageDurationQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    expect(stageDurationQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'stage-duration', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(stageDurationQueryOptions('proj-1', '30d').queryKey).toEqual(['issues', 'metrics', 'stage-duration', '30d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = stageDurationQueryOptions('proj-1', '7d').queryKey
    const key30 = stageDurationQueryOptions('proj-1', '30d').queryKey
    const key90 = stageDurationQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    const urls = recordStageDurationRequests()

    await stageDurationQueryOptions('proj-1', '7d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/stage-duration?range=7d'])
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordStageDurationRequests()

    await stageDurationQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/stage-duration'])
    expect(urls[0]).not.toContain('range=')
  })
})
