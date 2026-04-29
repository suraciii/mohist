import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from './test-utils'

vi.mock('../src/hooks/useQueries', async () => {
  return {
    useWorktreeStatus: vi.fn(),
  }
})

vi.mock('../src/lib/rebase-events', async () => ({
  onRebaseEvent: vi.fn(() => () => {}),
}))

vi.mock('../src/lib/api', async () => ({
  api: {
    rebaseIssue: vi.fn(),
  },
  ApiError: class ApiError extends Error {
    data: unknown
    constructor(message: string, data: unknown) {
      super(message)
      this.data = data
    }
  },
}))

import { useWorktreeStatus } from '../src/hooks/useQueries'
import { WorktreePanel } from '../src/components/WorktreePanel'

const mockedUseWorktreeStatus = vi.mocked(useWorktreeStatus)

describe('WorktreePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('returns null when worktree does not exist', () => {
    mockedUseWorktreeStatus.mockReturnValue({
      data: { exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false },
      isLoading: false,
    } as ReturnType<typeof useWorktreeStatus>)
    const { container } = render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('renders panel when worktree exists', () => {
    mockedUseWorktreeStatus.mockReturnValue({
      data: { exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false },
      isLoading: false,
    } as ReturnType<typeof useWorktreeStatus>)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Worktree')).toBeInTheDocument()
    expect(screen.getByText('Up to date')).toBeInTheDocument()
    expect(screen.getByText('mo/issue-1')).toBeInTheDocument()
  })

  it('shows "Rebase onto master" when agent is idle', () => {
    mockedUseWorktreeStatus.mockReturnValue({
      data: { exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false },
      isLoading: false,
    } as ReturnType<typeof useWorktreeStatus>)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Rebase onto master')).toBeInTheDocument()
  })

  it('shows "Rebase after completion" when agent is running', () => {
    mockedUseWorktreeStatus.mockReturnValue({
      data: { exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false },
      isLoading: false,
    } as ReturnType<typeof useWorktreeStatus>)
    render(<WorktreePanel issueNumber={1} isAgentRunning={true} />)
    expect(screen.getByText('Rebase after completion')).toBeInTheDocument()
  })

  it('shows loading skeleton while loading', () => {
    mockedUseWorktreeStatus.mockReturnValue({
      data: undefined,
      isLoading: true,
    } as unknown as ReturnType<typeof useWorktreeStatus>)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText('Worktree')).toBeInTheDocument()
    const skeletons = document.querySelectorAll('.animate-pulse')
    expect(skeletons.length).toBeGreaterThan(0)
  })

  it('shows behind indicator when behind master', () => {
    mockedUseWorktreeStatus.mockReturnValue({
      data: { exists: true, branch: 'mo/issue-1', ahead: 0, behind: 3, canFastForward: false, isRebaseInProgress: false },
      isLoading: false,
    } as ReturnType<typeof useWorktreeStatus>)
    render(<WorktreePanel issueNumber={1} isAgentRunning={false} />)
    expect(screen.getByText(/3 commits behind master/)).toBeInTheDocument()
  })
})
