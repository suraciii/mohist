import { beforeEach, describe, expect, it, vi } from 'vitest'
import { QueryClient } from '@tanstack/react-query'
import { useIssueEvents, useWorkspaceStatus } from './queries'
import * as clientModule from './client'

const useQueryMock = vi.fn()
const useProjectMock = vi.fn()
const getIssueEventsMock = vi.fn()
const getWorkspaceStatusMock = vi.fn()

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
    getIssueEvents: (...args: unknown[]) => getIssueEventsMock(...args),
    getWorkspaceStatus: (...args: unknown[]) => getWorkspaceStatusMock(...args),
  }
})

beforeEach(() => {
  useQueryMock.mockReset()
  useProjectMock.mockReset()
  getIssueEventsMock.mockReset()
  getWorkspaceStatusMock.mockReset()
  useProjectMock.mockReturnValue({ projectId: 'proj-1' })
  useQueryMock.mockReturnValue({ data: [], isLoading: false })
  getIssueEventsMock.mockResolvedValue([])
  getWorkspaceStatusMock.mockResolvedValue({ exists: true })
  void clientModule
})

describe('useIssueEvents', () => {
  it('uses the non-[issues] query key ["issue-events", number, projectId]', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(42)

    expect(useQueryMock).toHaveBeenCalledTimes(1)
    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issue-events', 42, 'proj-1'])
    expect(config.queryKey[0]).toBe('issue-events')
    expect(config.queryKey).not.toContain('issues')
  })

  it('does NOT prefix the query key with ["issues", ...] so LiveTaskProvider invalidations do not refetch it', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(42)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey[0]).not.toBe('issues')
    expect(config.queryKey).not.toEqual(expect.arrayContaining(['issues', 42, 'proj-1', 'events']))
    expect(config.queryKey[0]).toBe('issue-events')
  })

  it('invokes getIssueEvents(number, projectId) as the query function', async () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })
    getIssueEventsMock.mockResolvedValue([])

    useIssueEvents(42)

    const config = useQueryMock.mock.calls[0][0]
    expect(typeof config.queryFn).toBe('function')
    await config.queryFn()
    expect(getIssueEventsMock).toHaveBeenCalledWith(42, 'proj-1')
  })

  it('is disabled when number is 0 even if projectId is set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(0)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is disabled when projectId is missing', () => {
    useProjectMock.mockReturnValue({ projectId: null })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(42)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('is enabled when both number > 0 and projectId are set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(42)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(true)
  })

  it('respects an explicit enabled=false override', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(42, false)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('changes the query key when number changes (re-issued)', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: [], isLoading: false })

    useIssueEvents(42)
    useIssueEvents(43)

    expect(useQueryMock).toHaveBeenCalledTimes(2)
    const first = useQueryMock.mock.calls[0][0]
    const second = useQueryMock.mock.calls[1][0]
    expect(first.queryKey).toEqual(['issue-events', 42, 'proj-1'])
    expect(second.queryKey).toEqual(['issue-events', 43, 'proj-1'])
  })
})

describe('useWorkspaceStatus', () => {
  it('is enabled by default when issue number and project id are set', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: { exists: true }, isLoading: false })

    useWorkspaceStatus(161)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.queryKey).toEqual(['issues', 161, 'proj-1', 'workspace-status'])
    expect(config.enabled).toBe(true)
  })

  it('respects an explicit enabled=false override', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: undefined, isLoading: false })

    useWorkspaceStatus(161, false)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.enabled).toBe(false)
  })

  it('retries workspace status faster when the runner cannot check upstream', () => {
    useProjectMock.mockReturnValue({ projectId: 'proj-1' })
    useQueryMock.mockReturnValue({ data: { exists: true, reason: 'fetch_failed' }, isLoading: false })

    useWorkspaceStatus(161)

    const config = useQueryMock.mock.calls[0][0]
    expect(config.refetchInterval({ state: { data: { reason: 'fetch_failed' } } })).toBe(5_000)
    expect(config.refetchInterval({ state: { data: { reason: 'git_error', ahead: 0, behind: 0 } } })).toBe(5_000)
    expect(config.refetchInterval({ state: { data: { exists: true } } })).toBe(5_000)
    expect(config.refetchInterval({ state: { data: { exists: true, ahead: 1, behind: 2 } } })).toBe(30_000)
  })
})

void QueryClient
