import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from './test-utils'

type WorktreeStatus = {
  exists: boolean
  branch: string
  ahead: number
  behind: number
  canFastForward: boolean
  isRebaseInProgress: boolean
}

vi.mock('../src/entities/issue/api/queries', async () => {
  return {
    useWorktreeStatus: vi.fn(),
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

import { useWorktreeStatus } from '../src/entities/issue/api/queries'
import { WorktreePanel } from '../src/widgets/worktree/ui/WorktreePanel'

const mockedUseWorktreeStatus = vi.mocked(useWorktreeStatus)

function mockWorktreeStatus(data: WorktreeStatus | null | undefined, isLoading: boolean) {
  mockedUseWorktreeStatus.mockReturnValue({
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
  } as unknown as ReturnType<typeof useWorktreeStatus>)
}

describe('WorktreePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('returns null when worktree does not exist', () => {
    mockWorktreeStatus({ exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }, false)
    const { container } = render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('renders panel when worktree exists', () => {
    mockWorktreeStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Worktree')).toBeInTheDocument()
    expect(screen.getByText('Up to date')).toBeInTheDocument()
    expect(screen.getByText('mo/issue-1')).toBeInTheDocument()
  })

  it('shows "Rebase onto master" when agent is idle', () => {
    mockWorktreeStatus({ exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Rebase onto master')).toBeInTheDocument()
  })

  it('shows "Rebase after completion" when agent is running', () => {
    mockWorktreeStatus({ exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorktreePanel issueNumber={1} isAgentRunning={true} />)
    expect(screen.getByText('Rebase after completion')).toBeInTheDocument()
  })

  it('returns null while loading', () => {
    mockWorktreeStatus(undefined, true)
    const { container } = render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('shows behind indicator when behind master', () => {
    mockWorktreeStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 3, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText(/3 commits behind master/)).toBeInTheDocument()
  })
})
