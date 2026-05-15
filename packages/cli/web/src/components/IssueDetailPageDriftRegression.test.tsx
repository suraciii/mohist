// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
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

interface SampleIssue {
  id: string
  number: number
  title: string
  body: string
  status: 'active'
  stage: 'check' | 'build'
  labels: string[]
  createdAt: string
  updatedAt: string
  projectId: string
  projectName: string
  approvalState?: { status: 'awaiting'; output?: { summary: string }; requestedAt: string; respondedAt: string | null }
  drift?: {
    drifted: boolean
    decision: string | null
    safeWindow: boolean | null
    deferReason: string | null
    staleEvidence: { review: boolean; mergeReady: boolean; approval: boolean } | null
    observedBaseSha: string | null
    currentBaseSha: string | null
    candidateHeadSha: string | null
    mergeBaseSha: string | null
    conflicts: string[] | null
    nextAction: string | null
  } | null
}

const SAMPLE_ISSUE: SampleIssue = {
  id: '1',
  number: 123,
  title: 'Test Issue',
  body: 'Description',
  status: 'active',
  stage: 'check',
  labels: [],
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  projectId: 'proj-1',
  projectName: 'TestProject',
}

const SAMPLE_DIFF_DATA = {
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
  commits: [],
  summary: { filesChanged: 3, commits: 2, additions: 13, deletions: 3 },
}

function setupDefaultMocks(overrideIssue?: Record<string, unknown>, overrideDiff?: object, overrideCommits?: object) {
  mocks.useIssue.mockReturnValue({
    data: { ...SAMPLE_ISSUE, ...overrideIssue },
    isLoading: false,
    isError: false,
  })
  mocks.useIssueDiff.mockReturnValue({
    data: overrideDiff ?? SAMPLE_DIFF_DATA,
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

describe('IssueDetailPage - drift regression: stale Check approval suppressed after drift projection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  describe('stale approval not shown as approvable after drift projection', () => {
    it('shows Base Drift Detected panel when issue has drifted=true with stale approval', () => {
      setupDefaultMocks({
        stage: 'check',
        status: 'active',
        approvalState: {
          status: 'awaiting',
          output: { summary: 'All checks passed, ready for approval' },
          requestedAt: new Date().toISOString(),
          respondedAt: null,
        },
        drift: {
          drifted: true,
          decision: 'needs-attention',
          safeWindow: true,
          deferReason: null,
          staleEvidence: { review: false, mergeReady: true, approval: true },
          observedBaseSha: 'abc123',
          currentBaseSha: 'def456',
          candidateHeadSha: 'head-sha',
          mergeBaseSha: 'mb-sha',
          conflicts: null,
          nextAction: 'Stale approval detected. Rebase or rerun checks before approving.',
        },
      })

      renderPage()

      expect(screen.getByText(/Base Drift Detected/i)).toBeTruthy()
      expect(screen.getAllByText(/stale evidence/i).length).toBeGreaterThan(0)
      expect(screen.getAllByText(/Stale approval detected/i).length).toBeGreaterThan(0)
    })

    it('shows the deferred rebase reason when drift decision is defer', () => {
      setupDefaultMocks({
        stage: 'build',
        status: 'active',
        drift: {
          drifted: true,
          decision: 'defer',
          safeWindow: false,
          deferReason: 'task-running',
          staleEvidence: null,
          observedBaseSha: 'abc123',
          currentBaseSha: 'def456',
          candidateHeadSha: 'head-sha',
          mergeBaseSha: 'mb-sha',
          conflicts: null,
          nextAction: 'Rebase deferred until safe window (task-running).',
        },
      })

      renderPage()

      expect(screen.getByText(/Base Drift Detected/i)).toBeTruthy()
      expect(screen.getAllByText(/Deferred/i).length).toBeGreaterThan(0)
      expect(screen.getAllByText(/Task running/i).length).toBeGreaterThan(0)
    })

    it('shows conflict files when drift has conflicts from failed rebase', () => {
      setupDefaultMocks({
        stage: 'check',
        status: 'active',
        drift: {
          drifted: true,
          decision: 'needs-attention',
          safeWindow: true,
          deferReason: null,
          staleEvidence: { review: false, mergeReady: true, approval: true },
          observedBaseSha: 'abc123',
          currentBaseSha: 'def456',
          candidateHeadSha: 'head-sha',
          mergeBaseSha: 'mb-sha',
          conflicts: ['src/foo.ts', 'src/bar.ts'],
          nextAction: 'Rebase failed with conflicts. Resolve conflicts and rerun checks.',
        },
      })

      renderPage()

      expect(screen.getByText(/Base Drift Detected/i)).toBeTruthy()
      expect(screen.getAllByText(/src\/foo\.ts/i).length).toBeGreaterThan(0)
      expect(screen.getAllByText(/src\/bar\.ts/i).length).toBeGreaterThan(0)
    })

    it('shows nextAction text as guidance when drift is projected', () => {
      setupDefaultMocks({
        stage: 'check',
        status: 'active',
        drift: {
          drifted: true,
          decision: 'suggest',
          safeWindow: true,
          deferReason: null,
          staleEvidence: { review: false, mergeReady: true, approval: false },
          observedBaseSha: 'abc123',
          currentBaseSha: 'def456',
          candidateHeadSha: 'head-sha',
          mergeBaseSha: 'mb-sha',
          conflicts: null,
          nextAction: 'Rebase recommended; run "mo issue rebase main" when ready.',
        },
      })

      renderPage()

      expect(screen.getByText(/Base Drift Detected/i)).toBeTruthy()
      expect(screen.getByText(/Rebase recommended.*mo issue rebase main/i)).toBeTruthy()
    })
  })
})