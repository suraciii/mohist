import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { agentUsageQueryKey, agentUsageQueryOptions, fetchAgentUsage } from './agent-usage'

const USAGE_DTO = {
  rangeFrom: '2026-06-22',
  rangeTo: '2026-06-29',
  bucketGranularity: 'day',
  buckets: [],
}

function recordAgentUsageRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/agent/usage', ({ request }) => {
      urls.push(new URL(request.url).pathname + new URL(request.url).search)
      return HttpResponse.json({ success: true, data: USAGE_DTO })
    }),
  )
  return urls
}

useMswServer()

describe('agentUsageQueryOptions', () => {
  it('uses the query key ["agent","usage", projectId]', () => {
    expect(agentUsageQueryOptions('proj-1').queryKey).toEqual(['agent', 'usage', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(agentUsageQueryOptions('proj-other').queryKey).toEqual(['agent', 'usage', 'proj-other'])
  })

  it('issues GET .../agent/usage without a query string by default', async () => {
    const urls = recordAgentUsageRequests()

    await agentUsageQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agent/usage'])
  })

  it('applies a 60 second staleTime', () => {
    expect(agentUsageQueryOptions('proj-1').staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(agentUsageQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(agentUsageQueryOptions('proj-1').enabled).toBe(true)
  })
})

describe('agentUsageQueryKey', () => {
  it('returns a project-scoped key when a projectId is provided', () => {
    expect(agentUsageQueryKey('proj-1')).toEqual(['agent', 'usage', 'proj-1'])
  })

  it('returns the shared prefix when projectId is missing', () => {
    expect(agentUsageQueryKey()).toEqual(['agent', 'usage'])
    expect(agentUsageQueryKey(null)).toEqual(['agent', 'usage'])
  })

  it('folds the range into the key when provided alongside projectId', () => {
    expect(agentUsageQueryKey('proj-1', '90d')).toEqual(['agent', 'usage', '90d', 'proj-1'])
  })
})

describe('fetchAgentUsage', () => {
  it('calls the agent usage endpoint for the given projectId and returns the payload', async () => {
    const urls = recordAgentUsageRequests()

    const data = await fetchAgentUsage('proj-1')

    expect(urls).toEqual(['/api/projects/proj-1/agent/usage'])
    expect(data).toEqual(USAGE_DTO)
  })
})

describe('agentUsageQueryOptions range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    expect(agentUsageQueryOptions('proj-1').queryKey).toEqual(['agent', 'usage', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    expect(agentUsageQueryOptions('proj-1', '90d').queryKey).toEqual(['agent', 'usage', '90d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    const key7 = agentUsageQueryOptions('proj-1', '7d').queryKey
    const key30 = agentUsageQueryOptions('proj-1', '30d').queryKey
    const key90 = agentUsageQueryOptions('proj-1', '90d').queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    const urls = recordAgentUsageRequests()

    await agentUsageQueryOptions('proj-1', '30d').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agent/usage?range=30d'])
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    const urls = recordAgentUsageRequests()

    await agentUsageQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/agent/usage'])
  })
})
