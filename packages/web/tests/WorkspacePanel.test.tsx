import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from './test-utils'

type WorkspaceStatus = {
  exists: boolean
  reason?: string
  branch: string
  ahead: number
  behind: number
  canFastForward: boolean
  isRebaseInProgress?: boolean
  rebaseInProgress?: boolean
}

vi.mock('../src/entities/issue/api/queries', async () => {
  return {
    useWorkspaceStatus: vi.fn(),
  }
})

vi.mock('../src/entities/issue/model/rebase-events', async () => ({
  onRebaseEvent: vi.fn(() => () => {}),
}))

vi.mock('../src/entities/issue/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/entities/issue/api/client')>()),
  rebaseIssue: vi.fn(),
}))

vi.mock('../src/shared/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../src/shared/api/client')>()),
  ApiError: class ApiError extends Error {
    data: unknown
    constructor(message: string, data: unknown) {
      super(message)
      this.data = data
    }
  },
}))

import { useWorkspaceStatus } from '../src/entities/issue/api/queries'
import { WorkspacePanel } from '../src/widgets/workspace/ui/WorkspacePanel'

const mockedUseWorkspaceStatus = vi.mocked(useWorkspaceStatus)

function mockWorkspaceStatus(data: WorkspaceStatus | null | undefined, isLoading: boolean) {
  mockedUseWorkspaceStatus.mockReturnValue({
    data,
    isLoading,
    isPending: isLoading,
    isError: false,
    isSuccess: !isLoading && data !== undefined,
    isFetching: false,
    isLoadingError: false,
    isRefetchError: false,
    isPlaceholderData: false,
    dataUpdatedAt: Date.now(),
    error: null,
    errorUpdatedAt: 0,
    failureCount: 0,
    failureReason: null,
    errorUpdateCount: 0,
    isFetched: !isLoading,
    isFetchedAfterMount: !isLoading,
    isRefetching: false,
    isLoadingLoading: isLoading,
    status: isLoading ? 'pending' : 'success',
    fetchStatus: isLoading ? 'fetching' : 'idle',
    refetch: vi.fn(),
  } as unknown as ReturnType<typeof useWorkspaceStatus>)
}

describe('WorkspacePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('returns null when workspace does not exist', () => {
    mockWorkspaceStatus({ exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }, false)
    const { container } = render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('renders panel with workspace heading when workspace exists', () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Workspace')).toBeInTheDocument()
    expect(screen.queryByText(/^Worktree$/)).not.toBeInTheDocument()
    expect(screen.getByText('Up to date')).toBeInTheDocument()
    expect(screen.getByText('mo/issue-1')).toBeInTheDocument()
  })

  it('shows "Rebase onto master" when agent is idle', () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Rebase onto master')).toBeInTheDocument()
  })

  it('still allows queuing rebase when agent is running', () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={true} />)
    expect(screen.getByText('Rebase onto master')).toBeInTheDocument()
  })

  it('returns null while loading', () => {
    mockWorkspaceStatus(undefined, true)
    const { container } = render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('shows behind indicator when behind master', () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 3, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText(/3 commits behind master/)).toBeInTheDocument()
  })

  it('shows unknown upstream state without stale up-to-date or rebase controls when fetch fails', () => {
    mockWorkspaceStatus({ exists: true, reason: 'fetch_failed', branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('未能检查上游')).toBeInTheDocument()
    expect(screen.queryByText('Up to date')).not.toBeInTheDocument()
    expect(screen.queryByText('Rebase onto master')).not.toBeInTheDocument()
  })

  it('keeps rebasing control visible above unknown upstream state when fetch fails during rebase', () => {
    mockWorkspaceStatus({ exists: true, reason: 'fetch_failed', branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: false, rebaseInProgress: true }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Rebasing...')).toBeInTheDocument()
    expect(screen.queryByText('未能检查上游')).not.toBeInTheDocument()
  })

  it('uses workspace wording for the Done cleanup button and removal copy', () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} isDone={true} />)
    expect(screen.getByText('Clean up workspace')).toBeInTheDocument()
    expect(screen.getByText(/Archiving also removes this workflow workspace/)).toBeInTheDocument()
    expect(screen.queryByText(/Remove worktree/i)).not.toBeInTheDocument()
  })

  it('uses deferred workspace cleanup copy when agent is still running', () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={true} isDone={true} />)
    expect(screen.getByText('Clean up after completion')).toBeInTheDocument()
  })
})
