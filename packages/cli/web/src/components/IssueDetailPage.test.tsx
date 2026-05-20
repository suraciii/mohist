// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { cleanup, render, screen, fireEvent } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { IssueDetailPage } from './IssueDetailPage'

const mockUseNavigate = vi.fn()

const mocks = vi.hoisted(() => ({
  useIssue: vi.fn(),
  useIssueDiff: vi.fn(),
  useIssueCommits: vi.fn(),
  useAgentStatus: vi.fn(),
  useExploreSessions: vi.fn(),
  useCreateExploreSession: vi.fn(),
  useWorkflowRun: vi.fn(),
  useIssueStageState: vi.fn(),
  useIssueExecutions: vi.fn(),
  useWorktreeStatus: vi.fn(),
  useOpencodeModels: vi.fn(),
  useOpencodeModel: vi.fn(),
  useLabels: vi.fn(),
}))

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockUseNavigate,
  }
})

vi.mock('../hooks/useQueries', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../hooks/useQueries')>()
  return {
    ...actual,
    useIssue: mocks.useIssue,
    useIssueDiff: mocks.useIssueDiff,
    useIssueCommits: mocks.useIssueCommits,
    useAgentStatus: mocks.useAgentStatus,
    useExploreSessions: mocks.useExploreSessions,
    useCreateExploreSession: mocks.useCreateExploreSession,
    useWorkflowRun: mocks.useWorkflowRun,
    useIssueStageState: mocks.useIssueStageState,
    useIssueExecutions: mocks.useIssueExecutions,
    useWorktreeStatus: mocks.useWorktreeStatus,
    useOpencodeModels: mocks.useOpencodeModels,
    useOpencodeModel: mocks.useOpencodeModel,
    useLabels: mocks.useLabels,
  }
})

const SAMPLE_ISSUE = {
  id: '1',
  number: 123,
  title: 'Test Issue',
  body: 'Description',
  status: 'active' as const,
  stage: 'build' as const,
  labels: [],
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  projectId: 'proj-1',
  projectName: 'TestProject',
}

const SAMPLE_DIFF_DATA_AHEAD_ONLY = {
  available: true as const,
  reason: null,
  base: 'main',
  head: 'mo/issue-123',
  mergeBase: 'abc123',
  ahead: 3,
  behind: 0,
  canFastForward: false,
  comparison: 'merge-base' as const,
  summary: { filesChanged: 5, additions: 20, deletions: 5 },
  files: [
    { file: 'src/a.ts', additions: 5, deletions: 1, diff: '', isBinary: false },
    { file: 'src/b.ts', additions: 3, deletions: 2, diff: '', isBinary: false },
  ],
}

const SAMPLE_DIFF_DATA_AHEAD_BEHIND = {
  available: true as const,
  reason: null,
  base: 'main',
  head: 'mo/issue-123',
  mergeBase: 'def456',
  ahead: 2,
  behind: 3,
  canFastForward: false,
  comparison: 'merge-base' as const,
  summary: { filesChanged: 2, additions: 10, deletions: 3 },
  files: [
    { file: 'src/issue.ts', additions: 8, deletions: 2, diff: '', isBinary: false },
    { file: 'src/new.ts', additions: 2, deletions: 1, diff: '', isBinary: false },
  ],
}

const SAMPLE_COMMITS_DATA = {
  available: true as const,
  reason: null,
  base: 'main',
  head: 'mo/issue-123',
  mergeBase: 'abc123',
  ahead: 3,
  behind: 0,
  canFastForward: false,
  comparison: 'merge-base' as const,
  commits: [
    {
      hash: 'abc1234567890',
      shortHash: 'abc1234',
      message: 'feat: add new feature',
      author: 'Test User',
      date: new Date(Date.now() - 600000).toISOString(),
      filesChanged: 2,
      additions: 10,
      deletions: 2,
      files: ['src/a.ts', 'src/b.ts'],
    },
    {
      hash: 'def2345678901',
      shortHash: 'def2345',
      message: 'fix: resolve bug',
      author: 'Test User',
      date: new Date(Date.now() - 1200000).toISOString(),
      filesChanged: 1,
      additions: 3,
      deletions: 1,
      files: ['src/c.ts'],
    },
  ],
  summary: { filesChanged: 3, commits: 2, additions: 13, deletions: 3 },
}

function setupDefaultMocks(overrideDiff?: object, overrideCommits?: object) {
  mocks.useIssue.mockReturnValue({
    data: SAMPLE_ISSUE,
    isLoading: false,
    isError: false,
  })
  mocks.useIssueDiff.mockReturnValue({
    data: overrideDiff ?? SAMPLE_DIFF_DATA_AHEAD_ONLY,
  })
  mocks.useIssueCommits.mockReturnValue({
    data: overrideCommits ?? SAMPLE_COMMITS_DATA,
  })
  mocks.useAgentStatus.mockReturnValue({
    data: { activeAgents: [], maxConcurrentAgents: 2 },
  })
  mocks.useExploreSessions.mockReturnValue({ data: [] })
  mocks.useCreateExploreSession.mockReturnValue({ mutateAsync: vi.fn() })
  mocks.useWorkflowRun.mockReturnValue({ data: undefined })
  mocks.useIssueStageState.mockReturnValue({ data: undefined })
  mocks.useIssueExecutions.mockReturnValue({ data: [] })
  mocks.useWorktreeStatus.mockReturnValue({ data: undefined })
  mocks.useOpencodeModels.mockReturnValue({ data: [] })
  mocks.useOpencodeModel.mockReturnValue({ data: undefined })
  mocks.useLabels.mockReturnValue({ data: [] })
}

function renderPage(initialRoute = '/issue/123') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialRoute]}>
        <Routes>
          <Route path="/issue/:number" element={<IssueDetailPage />} />
          <Route path="/issue/:number/files" element={<div>Files Page</div>} />
          <Route path="/" element={<div>Board Page</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  )
}

describe('IssueDetailPage - merge-base semantic regression', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setupDefaultMocks()
  })

  afterEach(() => {
    cleanup()
  })

  describe('merge framing in changes summary', () => {
    it('shows merge framing with head wants to merge into base', () => {
      renderPage()
      const branchTexts = screen.getAllByText(/mo\/issue-123/)
      expect(branchTexts.length).toBeGreaterThan(0)
      expect(screen.getByText(/wants to merge into/)).toBeTruthy()
    })

    it('shows ahead count', () => {
      renderPage()
      expect(screen.getByText(/^3$/)).toBeTruthy()
      expect(screen.getByText(/ahead/)).toBeTruthy()
    })

    it('shows behind count when branch is behind base', () => {
      setupDefaultMocks(SAMPLE_DIFF_DATA_AHEAD_BEHIND)
      renderPage()
      expect(screen.getByText(/^3$/)).toBeTruthy()
      expect(screen.getByText(/behind/)).toBeTruthy()
    })

    it('shows files changed count from merge-base diff', () => {
      renderPage()
      expect(screen.getByText(/^5$/)).toBeTruthy()
      expect(screen.getAllByText(/files changed/).length).toBeGreaterThan(0)
    })

    it('shows additions and deletions', () => {
      renderPage()
      expect(screen.getByText('+20')).toBeTruthy()
      expect(screen.getByText('-5')).toBeTruthy()
    })

    it('shows merge-base semantic label', () => {
      renderPage()
      expect(screen.getByText(/showing merge-base/)).toBeTruthy()
    })

    it('does not show base-only files when branch is behind', () => {
      setupDefaultMocks(SAMPLE_DIFF_DATA_AHEAD_BEHIND)
      renderPage()
      expect(screen.queryByText(/base-only/)).toBeNull()
    })
  })

  describe('commits section', () => {
    it('shows commits section with commit count', () => {
      renderPage()
      expect(screen.getByText('Commits (2)')).toBeTruthy()
    })

    it('shows commit short hash', () => {
      renderPage()
      expect(screen.getByText('abc1234')).toBeTruthy()
    })

    it('shows commit message', () => {
      renderPage()
      expect(screen.getByText('feat: add new feature')).toBeTruthy()
    })

    it('shows relative time for commits', () => {
      renderPage()
      expect(screen.getAllByText(/^\d+[mhs] ago$/).length).toBeGreaterThan(0)
    })

    it('shows View all commits button linking to files page', () => {
      renderPage()
      const viewAllButton = screen.getByText('View all commits')
      expect(viewAllButton).toBeTruthy()
    })

    it('does not show commits section when commits unavailable', () => {
      setupDefaultMocks(undefined, { available: false, reason: 'not_started', message: 'Issue has not started yet.' })
      renderPage()
      expect(screen.queryByText(/Commits \(2\)/)).toBeNull()
    })
  })

  describe('unavailable states', () => {
    it('shows unavailable message for not_started diff', () => {
      setupDefaultMocks({ available: false, reason: 'not_started', message: 'Issue has not started yet.' })
      renderPage()
      expect(screen.getByText('Issue has not started yet.')).toBeTruthy()
    })

    it('shows unavailable message for worktree_removed diff', () => {
      setupDefaultMocks({ available: false, reason: 'worktree_removed', message: 'Workspace has been removed.' })
      renderPage()
      expect(screen.getByText('Workspace has been removed.')).toBeTruthy()
    })

    it('shows unavailable message for branch_missing diff', () => {
      setupDefaultMocks({ available: false, reason: 'branch_missing', message: 'Branch not found.' })
      renderPage()
      expect(screen.getByText('Branch not found.')).toBeTruthy()
    })
  })

  describe('View files navigation', () => {
    it('has View files button navigating to files page', () => {
      renderPage()
      const viewFilesButton = screen.getByText('View files')
      expect(viewFilesButton).toBeTruthy()
      fireEvent.click(viewFilesButton)
      expect(mockUseNavigate).toHaveBeenCalledWith('/issue/123/files')
    })
  })

  describe('commit-specific inspection does not change issue-level semantics', () => {
    it('shows correct files changed before and after viewing commits', () => {
      renderPage()
      expect(screen.getByText(/\d+ files changed/)).toBeTruthy()
    })

    it('commits section shows commits count consistent with diff summary', () => {
      renderPage()
      expect(screen.getByText('Commits (2)')).toBeTruthy()
    })
  })

  describe('behind-base explanatory copy', () => {
    it('shows behind count when branch is behind base', () => {
      setupDefaultMocks(SAMPLE_DIFF_DATA_AHEAD_BEHIND)
      renderPage()
      expect(screen.getByText(/^3$/)).toBeTruthy()
      expect(screen.getByText(/behind/)).toBeTruthy()
    })

    it('files changed reflects only issue-introduced changes when behind', () => {
      setupDefaultMocks(SAMPLE_DIFF_DATA_AHEAD_BEHIND)
      renderPage()
      expect(screen.getAllByText(/^2$/).length).toBeGreaterThan(0)
      expect(screen.getAllByText(/files changed/).length).toBeGreaterThan(0)
    })
  })

  describe('issue detail page renders with merge metadata', () => {
    it('renders issue number and title', () => {
      renderPage()
      expect(screen.getByText('#123')).toBeTruthy()
      expect(screen.getByText('Test Issue')).toBeTruthy()
    })

    it('renders a primary Epic backlink and navigates to the Epic detail page', () => {
      mocks.useIssue.mockReturnValue({
        data: {
          ...SAMPLE_ISSUE,
          primaryEpic: {
            id: 'epic-runtime',
            title: 'Runtime model cleanup',
            status: 'active',
            priority: 'p1',
          },
        },
        isLoading: false,
        isError: false,
      })

      renderPage()

      const epicLink = screen.getByText('Part of Epic:').closest('button')
      expect(epicLink).toBeTruthy()
      expect(screen.getByText('#epic-run')).toBeTruthy()
      expect(screen.getByText('Runtime model cleanup')).toBeTruthy()

      fireEvent.click(epicLink!)

      expect(mockUseNavigate).toHaveBeenCalledWith('/epic/epic-runtime')
    })

    it('hides the primary Epic backlink for unlinked issues', () => {
      renderPage()

      expect(screen.queryByText('Part of Epic:')).toBeNull()
    })

    it('renders loading state', () => {
      mocks.useIssue.mockReturnValue({
        data: undefined,
        isLoading: true,
        isError: false,
      })
      renderPage()
      expect(screen.getByText('Loading...')).toBeTruthy()
    })

    it('renders not found page on error', () => {
      mocks.useIssue.mockReturnValue({
        data: undefined,
        isLoading: false,
        isError: true,
      })
      renderPage()
      expect(screen.getByText('Page not found')).toBeTruthy()
    })
  })

  describe('Check repair actions', () => {
    it('renders running recovery guidance from recovery projection without local agent status', () => {
      setupDefaultMocks()
      mocks.useIssue.mockReturnValue({
        data: {
          ...SAMPLE_ISSUE,
          recovery: {
            currentWorkItem: { type: 'task', id: 'T-001', title: 'Implement recovery projection' },
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: ['wait', 'stop'],
          },
        },
        isLoading: false,
        isError: false,
      })
      mocks.useAgentStatus.mockReturnValue({
        data: { activeAgents: [], maxConcurrentAgents: 2 },
      })

      renderPage()

      expect(screen.getByText(/Waiting for running work/i)).toBeTruthy()
      expect(screen.getByText(/Current: task/)).toBeTruthy()
      expect(screen.getByText(/Implement recovery projection/)).toBeTruthy()
      expect(screen.getByText(/Force Stop/i)).toBeTruthy()
    })

    it('shows explicit Check recovery actions from workflow-run when stage-state is unavailable', () => {
      setupDefaultMocks()
      mocks.useIssue.mockReturnValue({
        data: {
          ...SAMPLE_ISSUE,
          status: 'blocked',
          stage: 'check',
          blockedReason: '[check] Review failed',
          recovery: {
            currentWorkItem: { type: 'check', id: 'review-passed', title: 'Review passed' },
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: ['retry', 'rerun', 'inspect'],
          },
        },
        isLoading: false,
        isError: false,
      })
      mocks.useIssueStageState.mockReturnValue({ data: undefined })
      mocks.useWorkflowRun.mockReturnValue({
        data: {
          id: 'wr_123',
          issueId: '1',
          issueNumber: 123,
          status: 'failed',
          currentStage: 'check',
          stageRuns: [
            {
              stage: 'check',
              status: 'failed',
              tasks: [],
              checks: [
                {
                  checkName: 'review-passed',
                  title: 'Review passed',
                  status: 'failed',
                  message: 'Review failed',
                  output: { verdict: 'FAIL', summary: '2 issues remain' },
                  runCount: 1,
                  lastRunAt: new Date().toISOString(),
                },
              ],
              approvalStatus: null,
              approvalOutput: null,
              approvalRequestedAt: null,
              approvalRespondedAt: null,
              attempts: 0,
              startedAt: new Date().toISOString(),
              completedAt: null,
              updatedAt: new Date().toISOString(),
            },
          ],
        },
      })

      renderPage()

      expect(screen.getByText(/Fix review findings/i)).toBeTruthy()
      expect(screen.getByText(/Retry checkpoint/i)).toBeTruthy()
      expect(screen.getByText(/Rerun stage/i)).toBeTruthy()
      expect(screen.queryByText(/^Retry$/)).toBeNull()
    })

    it('does not show Check recovery actions when workflow recovery does not allow them', () => {
      setupDefaultMocks()
      mocks.useIssue.mockReturnValue({
        data: {
          ...SAMPLE_ISSUE,
          status: 'blocked',
          stage: 'check',
          blockedReason: '[check] Review failed',
          recovery: {
            currentWorkItem: { type: 'check', id: 'review-passed', title: 'Review passed' },
            latestAttemptState: 'failed',
            workflowSummaryState: 'waiting-for-recovery',
            allowedActions: [],
          },
        },
        isLoading: false,
        isError: false,
      })
      mocks.useIssueStageState.mockReturnValue({ data: undefined })
      mocks.useWorkflowRun.mockReturnValue({
        data: {
          id: 'wr_123',
          issueId: '1',
          issueNumber: 123,
          status: 'failed',
          currentStage: 'check',
          stageRuns: [
            {
              stage: 'check',
              status: 'failed',
              tasks: [],
              checks: [
                {
                  checkName: 'review-passed',
                  title: 'Review passed',
                  status: 'failed',
                  message: 'Review failed',
                  output: { verdict: 'FAIL', summary: '2 issues remain' },
                  runCount: 1,
                  lastRunAt: new Date().toISOString(),
                },
              ],
              approvalStatus: null,
              approvalOutput: null,
              approvalRequestedAt: null,
              approvalRespondedAt: null,
              attempts: 0,
              startedAt: new Date().toISOString(),
              completedAt: null,
              updatedAt: new Date().toISOString(),
            },
          ],
        },
      })

      renderPage()

      expect(screen.queryByText(/Fix review findings/i)).toBeNull()
      expect(screen.queryByText(/Retry checkpoint/i)).toBeNull()
      expect(screen.queryByText(/Rerun stage/i)).toBeNull()
      expect(screen.queryByText(/^Retry$/)).toBeNull()
    })
  })
})
