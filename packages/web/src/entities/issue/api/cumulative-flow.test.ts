import { beforeEach, describe, expect, it, vi } from 'vitest'
import { cumulativeFlowQueryKey, fetchCumulativeFlow, useCumulativeFlow } from './cumulative-flow'

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

describe('useCumulativeFlow', () => {
  it('uses the query key ["issues","metrics","cumulative-flow", projectId]', () => {
    useCumulativeFlow()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'cumulative-flow', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })

    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'cumulative-flow', 'proj-other'])
  })

  it('issues GET .../metrics/cumulative-flow without a query string', async () => {
    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            snapshots: [],
            rangeFrom: '2026-06-01',
            rangeTo: '2026-06-30',
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/cumulative-flow')

    vi.unstubAllGlobals()
  })

  it('applies a 60 second staleTime', () => {
    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBe(60_000)
  })

  it('does not set a refetchInterval (matches the other metric chart hooks; polling is for live ops data)', () => {
    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.refetchInterval).toBeUndefined()
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('decodes the response into the CumulativeFlowResponse shape', async () => {
    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            snapshots: [
              {
                day: '2026-06-28',
                backlog: 5,
                plan: 1,
                build: 2,
                check: 0,
                integrate: 0,
                done: 3,
              },
              {
                day: '2026-06-29',
                backlog: 4,
                plan: 2,
                build: 1,
                check: 1,
                integrate: 0,
                done: 4,
              },
            ],
            rangeFrom: '2026-04-01',
            rangeTo: '2026-06-30',
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(data.snapshots).toHaveLength(2)
    expect(data.snapshots[0].day).toBe('2026-06-28')
    expect(data.snapshots[0].backlog).toBe(5)
    expect(data.snapshots[0].done).toBe(3)
    expect(data.rangeFrom).toBe('2026-04-01')
    expect(data.rangeTo).toBe('2026-06-30')

    vi.unstubAllGlobals()
  })

  it('propagates fetch failures via the standard request path', async () => {
    useCumulativeFlow()

    const config = useQueryMock.mock.calls[0][0]

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: false,
          error: { code: 'project_not_found', message: 'Project not found' },
        }),
        { status: 404, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await expect(config.queryFn()).rejects.toThrow()

    vi.unstubAllGlobals()
  })

  it('fetchCumulativeFlow issues a GET request for the supplied project', async () => {
    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: { snapshots: [], rangeFrom: '2026-06-01', rangeTo: '2026-06-30' },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await fetchCumulativeFlow('proj-x')

    const [calledPath, init] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-x/issues/metrics/cumulative-flow')
    expect(init?.method).toBeUndefined()

    vi.unstubAllGlobals()
  })

  it('cumulativeFlowQueryKey projects the projectId when present', () => {
    expect(cumulativeFlowQueryKey('proj-1')).toEqual(['issues', 'metrics', 'cumulative-flow', 'proj-1'])
  })

  it('cumulativeFlowQueryKey returns the unscoped key when projectId is nullish', () => {
    expect(cumulativeFlowQueryKey(null)).toEqual(['issues', 'metrics', 'cumulative-flow'])
    expect(cumulativeFlowQueryKey(undefined)).toEqual(['issues', 'metrics', 'cumulative-flow'])
  })
})
