import { beforeEach, describe, expect, it, vi } from 'vitest'
import { agentUsageQueryKey, fetchAgentUsage, useAgentUsage } from './agent-usage'

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

describe('useAgentUsage', () => {
  it('uses the query key ["agent","usage", projectId]', () => {
    useAgentUsage()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'usage', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })

    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'usage', 'proj-other'])
  })

  it('issues GET .../agent/usage without a query string by default', async () => {
    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            rangeFrom: '2026-06-22',
            rangeTo: '2026-06-29',
            bucketGranularity: 'day',
            buckets: [],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/usage')

    vi.unstubAllGlobals()
  })

  it('applies a 60 second staleTime', () => {
    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
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
  it('calls the agent usage endpoint for the given projectId', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            rangeFrom: '2026-06-22',
            rangeTo: '2026-06-29',
            bucketGranularity: 'day',
            buckets: [],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await fetchAgentUsage('proj-1')

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/usage')

    vi.unstubAllGlobals()
  })
})

describe('useAgentUsage range threading', () => {
  it('preserves the existing queryKey shape when range is omitted (Dashboard back-compat)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'usage', 'proj-1'])
  })

  it('folds the range into the queryKey when supplied', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useAgentUsage('90d')
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['agent', 'usage', '90d', 'proj-1'])
  })

  it('produces a different queryKey for each range (cache isolation)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useAgentUsage('7d')
    useAgentUsage('30d')
    useAgentUsage('90d')

    const key7 = useQueryMock.mock.calls[0][0].queryKey
    const key30 = useQueryMock.mock.calls[1][0].queryKey
    const key90 = useQueryMock.mock.calls[2][0].queryKey
    expect(key7).not.toEqual(key30)
    expect(key7).not.toEqual(key90)
    expect(key30).not.toEqual(key90)
  })

  it('appends range=... to the fetch URL when supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useAgentUsage('30d')

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            rangeFrom: '2026-05-02',
            rangeTo: '2026-06-01',
            bucketGranularity: 'day',
            buckets: [],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/usage?range=30d')

    vi.unstubAllGlobals()
  })

  it('omits the range parameter from the URL when no range is supplied', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })

    useAgentUsage()

    const config = useQueryMock.mock.calls[0][0]
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            rangeFrom: '2026-06-22',
            rangeTo: '2026-06-29',
            bucketGranularity: 'day',
            buckets: [],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/agent/usage')
    expect(calledPath).not.toContain('range=')

    vi.unstubAllGlobals()
  })
})