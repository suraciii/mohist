// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BranchBar } from './BranchBar'
import { WorkflowStage } from '../../../entities/issue'
import { useWorkspaceStatus } from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkspaceStatus: vi.fn(),
  useLiveTask: () => ({}),
}))

describe('BranchBar', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('shows retained Done workflow workspace archive-removal copy when a Done issue has a workspace', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        branch: 'mo/issue-146',
        baseBranch: 'main',
        ahead: 0,
        behind: 0,
      },
      isLoading: false,
    } as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={146} stage={WorkflowStage.Done} isAgentRunning={false} />
      </QueryClientProvider>
    )

    expect(screen.getByText(/retained for review, traceability, diff inspection, and debugging/i)).toBeTruthy()
    expect(screen.getByText(/Archiving will remove the retained workspace/i)).toBeTruthy()
  })

  it('shows unknown upstream state without stale numbers or rebase action when fetch fails', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        reason: 'fetch_failed',
        branch: 'mohist/run-wr-216',
        baseBranch: 'main',
      },
      isLoading: false,
    } as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={216} stage={WorkflowStage.Check} isAgentRunning={false} />
      </QueryClientProvider>
    )

    expect(screen.getByText('未能检查上游')).toBeTruthy()
    expect(screen.queryByText(/up to date/i)).toBeNull()
    expect(screen.queryByRole('button', { name: /rebase onto main/i })).toBeNull()
    expect(screen.queryByText(/behind/i)).toBeNull()
  })

  it('keeps rebasing state above unknown upstream state when fetch fails during rebase', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        reason: 'fetch_failed',
        branch: 'mohist/run-wr-216',
        baseBranch: 'main',
        rebaseInProgress: true,
        conflictingFiles: ['packages/runner/src/server/runner-signalr.ts'],
      },
      isLoading: false,
    } as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={216} stage={WorkflowStage.Check} isAgentRunning={false} />
      </QueryClientProvider>
    )

    expect(screen.getByText('Rebasing...')).toBeTruthy()
    expect(screen.getByText('packages/runner/src/server/runner-signalr.ts')).toBeTruthy()
    expect(screen.queryByText('未能检查上游')).toBeNull()
  })
})
