import { describe, it, expect, vi, beforeEach } from 'vitest'
import { http, HttpResponse } from 'msw'
import { render, screen } from '../../../../tests/test-utils'
import { server } from '../../../../tests/support/msw'

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

vi.mock('../../../entities/issue/model/rebase-events', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue/model/rebase-events')>()),
  onRebaseEvent: vi.fn(() => () => {}),
}))

import { WorkspacePanel } from './WorkspacePanel'

let _workspaceData: WorkspaceStatus | null | undefined = undefined
let _isLoading = false

function setupHandler() {
  server.use(
    http.get('*/api/projects/:projectId/issues/:number/workspace-status', () => {
      if (_isLoading) return new Promise(() => {})
      if (!_workspaceData) {
        return HttpResponse.json({ success: true, data: _workspaceData })
      }
      return HttpResponse.json({ success: true, data: _workspaceData })
    }),
  )
}

function mockWorkspaceStatus(data: WorkspaceStatus | null | undefined, isLoading: boolean) {
  _workspaceData = data
  _isLoading = isLoading
  setupHandler()
}

describe('WorkspacePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('returns null when workspace does not exist', async () => {
    mockWorkspaceStatus({ exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }, false)
    const { container } = render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('renders panel with workspace heading when workspace exists', async () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(await screen.findByText('Workspace')).toBeInTheDocument()
    expect(screen.queryByText(/^Worktree$/)).not.toBeInTheDocument()
    expect(screen.getByText('Up to date')).toBeInTheDocument()
    expect(screen.getByText('mo/issue-1')).toBeInTheDocument()
  })

  it('shows "Rebase onto master" when agent is idle', async () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(await screen.findByText('Rebase onto master')).toBeInTheDocument()
  })

  it('still allows queuing rebase when agent is running', async () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 2, behind: 1, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={true} />)
    expect(await screen.findByText('Rebase onto master')).toBeInTheDocument()
  })

  it('returns null while loading', () => {
    mockWorkspaceStatus(undefined, true)
    const { container } = render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(container.innerHTML).toBe('')
  })

  it('shows behind indicator when behind master', async () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 3, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(await screen.findByText(/3 commits behind master/)).toBeInTheDocument()
  })

  it('shows unknown upstream state without stale up-to-date or rebase controls when fetch fails', async () => {
    mockWorkspaceStatus({ exists: true, reason: 'fetch_failed', branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(await screen.findByText('Unable to check upstream')).toBeInTheDocument()
    expect(screen.queryByText('Up to date')).not.toBeInTheDocument()
    expect(screen.queryByText('Rebase onto master')).not.toBeInTheDocument()
  })

  it('keeps rebasing control visible above unknown upstream state when fetch fails during rebase', async () => {
    mockWorkspaceStatus({ exists: true, reason: 'fetch_failed', branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: false, rebaseInProgress: true }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} />)
    expect(await screen.findByText('Rebasing...')).toBeInTheDocument()
    expect(screen.queryByText('Unable to check upstream')).not.toBeInTheDocument()
  })

  it('uses workspace wording for the Done cleanup button and removal copy', async () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={false} isDone={true} />)
    expect(await screen.findByText('Clean up workspace')).toBeInTheDocument()
    expect(screen.getByText(/Archiving also removes this workflow workspace/)).toBeInTheDocument()
    expect(screen.queryByText(/Remove worktree/i)).not.toBeInTheDocument()
  })

  it('uses deferred workspace cleanup copy when agent is still running', async () => {
    mockWorkspaceStatus({ exists: true, branch: 'mo/issue-1', ahead: 0, behind: 0, canFastForward: true, isRebaseInProgress: false }, false)
    render(<WorkspacePanel issueNumber={1} isAgentRunning={true} isDone={true} />)
    expect(await screen.findByText('Clean up after completion')).toBeInTheDocument()
  })
})
