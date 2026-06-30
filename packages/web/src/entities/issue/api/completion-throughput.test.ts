import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useCompletionThroughput } from './completion-trend'

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
  useQueryMock.mockReset()
  useProjectMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
})

describe('useCompletionThroughput', () => {
  it('uses the query key ["issues","metrics","completion","day", projectId]', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'day', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'completion', 'day', 'proj-other'])
  })

  it('issues GET .../metrics/completion?bucket=day', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            bucket: 'day',
            window: { from: '2026-06-01T00:00:00+00:00', to: '2026-06-30T00:00:00+00:00' },
            buckets: [{ boundary: '2026-06-01', completed: 0, failed: 0 }],
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/completion?bucket=day')
    expect(calledPath).toContain('bucket=day')
    expect(calledPath).not.toContain('bucket=week')

    vi.unstubAllGlobals()
  })

  it('applies a modest staleTime around 60 seconds', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBeGreaterThanOrEqual(30_000)
    expect(config.staleTime).toBeLessThanOrEqual(5 * 60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('decodes a dense 30-bucket response carrying per-bucket Completed and Failed', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useCompletionThroughput()

    const config = useQueryMock.mock.calls[0][0]

    const denseBuckets = Array.from({ length: 30 }, (_, index) => ({
      boundary: `2026-06-${String(index + 1).padStart(2, '0')}`,
      completed: index,
      failed: index % 3 === 0 ? 1 : 0,
    }))

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            bucket: 'day',
            window: { from: '2026-06-01T00:00:00+00:00', to: '2026-06-30T00:00:00+00:00' },
            buckets: denseBuckets,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(data.bucket).toBe('day')
    expect(data.buckets).toHaveLength(30)
    expect(data.buckets[0].completed).toBe(0)
    expect(data.buckets[0].failed).toBe(1)
    expect(data.buckets[1].completed).toBe(1)
    expect(data.buckets[1].failed).toBe(0)
    expect(data.buckets[29].completed).toBe(29)
    expect(data.window.from).toBe('2026-06-01T00:00:00+00:00')

    vi.unstubAllGlobals()
  })
})
