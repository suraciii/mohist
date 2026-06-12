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
})
