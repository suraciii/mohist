// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BranchBar } from './BranchBar'
import { WorkflowStage } from '../../../entities/issue'
import { useWorkspaceStatus } from '../../../entities/issue'

const rebaseIssueMock = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useWorkspaceStatus: vi.fn(),
  useLiveTask: () => ({}),
  rebaseIssue: (...args: unknown[]) => rebaseIssueMock(...args),
}))

describe('BranchBar', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
    rebaseIssueMock.mockResolvedValue({ status: 'queued', message: 'Rebase task queued', rebased: false })
  })

  it('shows the rebase action whenever a workspace exists, even without a workflow stage', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        branch: 'mohist/run-wr-161',
        baseBranch: 'master',
        ahead: 11,
        behind: 80,
        rebaseInProgress: false,
        conflictingFiles: [],
      },
      isLoading: false,
    } as unknown as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={161} stage={null} isAgentRunning={true} baseBranch="master" allowRebase />
      </QueryClientProvider>
    )

    expect(screen.getByRole('button', { name: /rebase onto master/i })).not.toBeDisabled()
    expect(screen.getByText(/80 behind/i)).toBeTruthy()
  })

  it('shows a stable rebase action while workspace status is loading when a run exists', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: undefined,
      isLoading: true,
    } as unknown as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={161} stage={null} isAgentRunning={true} baseBranch="master" allowRebase />
      </QueryClientProvider>
    )

    expect(screen.getByText('Checking upstream...')).toBeTruthy()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
  })

  it('does not claim ahead/behind status until the runner returns numeric counts', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        branch: 'mohist/run-wr-161',
        baseBranch: 'master',
      },
      isLoading: false,
    } as unknown as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={161} stage={WorkflowStage.Build} isAgentRunning={false} baseBranch="master" allowRebase />
      </QueryClientProvider>
    )

    expect(screen.getByText('Checking upstream...')).toBeTruthy()
    expect(screen.queryByText('workspace available')).toBeNull()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
  })

  it('does not treat error status default 0/0 counts as real workspace progress', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: false,
        reason: 'git_error',
        ahead: 0,
        behind: 0,
        rebaseInProgress: false,
        conflictingFiles: [],
      },
      isLoading: false,
    } as unknown as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={161} stage={WorkflowStage.Build} isAgentRunning={false} baseBranch="master" allowRebase />
      </QueryClientProvider>
    )

    expect(screen.getByText('未能检查上游')).toBeTruthy()
    expect(screen.queryByText('up to date')).toBeNull()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
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

  it('keeps the rebase action visible when upstream check fails for a workflow run', () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        reason: 'fetch_failed',
        branch: 'mohist/run-wr-161',
        baseBranch: 'master',
        ahead: 0,
        behind: 0,
      },
      isLoading: false,
    } as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={161} stage={WorkflowStage.Build} isAgentRunning={false} baseBranch="master" allowRebase />
      </QueryClientProvider>
    )

    expect(screen.getByText('未能检查上游')).toBeTruthy()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
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

  it('disables repeat rebase after a rebase task is queued', async () => {
    vi.mocked(useWorkspaceStatus).mockReturnValue({
      data: {
        exists: true,
        branch: 'mohist/run-wr-161',
        baseBranch: 'master',
        ahead: 11,
        behind: 80,
        rebaseInProgress: false,
        conflictingFiles: [],
      },
      isLoading: false,
    } as unknown as ReturnType<typeof useWorkspaceStatus>)

    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <BranchBar issueNumber={161} stage={WorkflowStage.Build} isAgentRunning={false} baseBranch="master" allowRebase />
      </QueryClientProvider>
    )

    fireEvent.click(screen.getByRole('button', { name: /rebase onto master/i }))

    await waitFor(() => expect(screen.getByText('Rebase queued')).toBeTruthy())
    expect(screen.queryByRole('button', { name: /rebase onto master/i })).toBeNull()
    expect(rebaseIssueMock).toHaveBeenCalledTimes(1)
  })
})
