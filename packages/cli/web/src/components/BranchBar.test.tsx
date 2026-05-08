// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BranchBar } from './BranchBar'
import { Stage } from '../lib/types'
import { useWorktreeStatus } from '../hooks/useQueries'

vi.mock('../hooks/useQueries', () => ({
  useWorktreeStatus: vi.fn(),
}))

vi.mock('../hooks/useSSE', () => ({
  useLiveTask: () => ({}),
}))

describe('BranchBar', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('shows retained Done worktree archive-removal copy when a Done issue has a worktree', () => {
    vi.mocked(useWorktreeStatus).mockReturnValue({
      data: {
        exists: true,
        branch: 'mo/issue-146',
        baseBranch: 'main',
        ahead: 0,
        behind: 0,
      },
      isLoading: false,
    } as ReturnType<typeof useWorktreeStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={146} stage={Stage.Done} isAgentRunning={false} />
      </QueryClientProvider>
    )

    expect(screen.getByText(/retained for review, traceability, diff inspection, and debugging/i)).toBeTruthy()
    expect(screen.getByText(/Archiving will remove the retained worktree/i)).toBeTruthy()
  })
})
