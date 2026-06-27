import type { QueryClient } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { invalidateApprovalWait, useApprovalWait } from './approval-wait'

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

describe('useApprovalWait', () => {
  it('uses the query key ["issues","metrics","approval-wait", projectId]', () => {
    useApprovalWait()

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'approval-wait', 'proj-1'])
  })

  it('uses the query key scoped to the projectId returned by useProject', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-other' })

    useApprovalWait()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 'metrics', 'approval-wait', 'proj-other'])
  })

  it('fetches the project approval-wait metrics endpoint', async () => {
    useApprovalWait()

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')

    const fetchMock = vi.fn<typeof fetch>()
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          success: true,
          data: {
            window: { from: '2026-06-20T00:00:00+00:00', to: '2026-06-27T00:00:00+00:00' },
            sampleCount: 1,
            averageSeconds: 11_520,
            medianSeconds: 11_520,
            maxSeconds: 11_520,
          },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    )
    vi.stubGlobal('fetch', fetchMock)

    const data = await config.queryFn()

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [calledPath] = fetchMock.mock.calls[0]
    expect(calledPath).toBe('/api/projects/proj-1/issues/metrics/approval-wait')
    expect(data.sampleCount).toBe(1)
    expect(data.averageSeconds).toBe(11_520)
  })

  it('uses a 60 second staleTime', () => {
    useApprovalWait()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })

    useApprovalWait()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    useApprovalWait()

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('invalidates the shared approval-wait query prefix', () => {
    const queryClient = {
      invalidateQueries: vi.fn(),
    } as unknown as QueryClient

    invalidateApprovalWait(queryClient)

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['issues', 'metrics', 'approval-wait'],
    })
  })
})
