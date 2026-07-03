import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../../shared/api/client'
import { useIssueWorkflowTaskLog } from './queries'

const useQueryMock = vi.fn()
const useProjectMock = vi.fn()
const getIssueWorkflowTaskLogMock = vi.fn()

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQuery: (...args: unknown[]) => useQueryMock(...args),
}))

vi.mock('../../project/@x/project-context', () => ({
  useProject: () => useProjectMock(),
}))

vi.mock('./client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./client')>()
  return {
    ...actual,
    getIssueWorkflowTaskLog: (...args: unknown[]) => getIssueWorkflowTaskLogMock(...args),
  }
})

beforeEach(() => {
  useQueryMock.mockReset()
  useProjectMock.mockReset()
  getIssueWorkflowTaskLogMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
  getIssueWorkflowTaskLogMock.mockResolvedValue({ lines: [], nextCursor: null, truncated: false })
})

describe('useIssueWorkflowTaskLog query key', () => {
  it('starts with [issueNumber, taskId] so it refetches when a different task is expanded', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey[0]).toBe(161)
    expect(config.queryKey[1]).toBe('build.1')
    expect(config.queryKey[2]).toBe('proj-1')
  })

  it('changes the query key when taskId changes (refetch per expanded task)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, 'build.1')
    useIssueWorkflowTaskLog(161, 'build.2')

    const first = useQueryMock.mock.calls[0][0]
    const second = useQueryMock.mock.calls[1][0]
    expect(first.queryKey[1]).toBe('build.1')
    expect(second.queryKey[1]).toBe('build.2')
    expect(first.queryKey).not.toEqual(second.queryKey)
  })

  it('disables the query when taskId is null', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, null)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('disables the query when taskId is undefined', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, undefined)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('disables the query when issueNumber is zero', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(0, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('disables the query when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when issueNumber > 0, taskId is non-empty, and projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('respects an explicit enabled=false override', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useIssueWorkflowTaskLog(161, 'build.1', {}, false)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })
})

describe('useIssueWorkflowTaskLog query function', () => {
  it('invokes getIssueWorkflowTaskLog with issueNumber, taskId, params, and projectId', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
    getIssueWorkflowTaskLogMock.mockResolvedValue({ lines: [], nextCursor: null, truncated: false })

    useIssueWorkflowTaskLog(161, 'build.1', { cursor: 5, limit: 50 })

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')
    await config.queryFn()
    expect(getIssueWorkflowTaskLogMock).toHaveBeenCalledWith(161, 'build.1', { cursor: 5, limit: 50 }, 'proj-1')
  })

  it('returns the fetched page on success', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
    const page = {
      lines: [{ seq: 1, timestamp: '2026-07-03T08:00:00.000Z', source: 'action:rebase', text: 'CONFLICT' }],
      nextCursor: 1,
      truncated: false,
    }
    getIssueWorkflowTaskLogMock.mockResolvedValue(page)

    useIssueWorkflowTaskLog(161, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    const result = await config.queryFn()
    expect(result).toBe(page)
  })

  it('returns an empty page when the endpoint is absent (404)', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
    getIssueWorkflowTaskLogMock.mockRejectedValue(new ApiError('endpoint missing', 404))

    useIssueWorkflowTaskLog(161, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    const result = await config.queryFn()
    expect(result).toEqual({ lines: [], nextCursor: null, truncated: false })
  })

  it('rethrows non-404 errors so callers can surface them', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })
    getIssueWorkflowTaskLogMock.mockRejectedValue(new ApiError('boom', 500))

    useIssueWorkflowTaskLog(161, 'build.1')

    const config = useQueryMock.mock.calls[0][0]
    await expect(config.queryFn()).rejects.toThrow('boom')
  })
})