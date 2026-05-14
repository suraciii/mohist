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

const SAMPLE_ISSUE = {
  id: '1',
  number: 123,
  title: 'Test Issue',
  body: 'Description',
  status: 'active' as const,
  stage: 'backlog' as const,
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

describe('IssueDetailPage - Start Prerequisites', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    setupDefaultMocks()
  })

  afterEach(() => {
    cleanup()
  })

  describe('prerequisite display', () => {
    it('should show prerequisite issues with delivery state', () => {
      const issueWithPrereqs = {
        ...SAMPLE_ISSUE,
        prerequisites: [
          { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          { number: 199, title: 'Issue #199', delivered: true, stage: 'done', status: 'completed', mergeState: 'merged' },
        ],
        startEligibility: {
          startable: false,
          reason: 'waiting-for-delivery',
          waitingForDelivery: [
            { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          ],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: issueWithPrereqs,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.getByText('Start Prerequisites')).toBeTruthy()
      expect(screen.getByText(/#200 Issue #200/)).toBeTruthy()
      expect(screen.getByText('Waiting')).toBeTruthy()
      expect(screen.getByText(/#199 Issue #199/)).toBeTruthy()
      expect(screen.getByText('Delivered')).toBeTruthy()
    })

    it('should not parse issue body for prerequisites', () => {
      const issueWithBodyPrereq = {
        ...SAMPLE_ISSUE,
        body: 'This issue depends on #200 and #199 in the body text',
        prerequisites: [
          { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
        ],
        startEligibility: {
          startable: false,
          reason: 'waiting-for-delivery',
          waitingForDelivery: [
            { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          ],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: issueWithBodyPrereq,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.getByText('Start Prerequisites')).toBeTruthy()
      expect(screen.getByText(/#200 Issue #200/)).toBeTruthy()
    })

    it('should indicate delivered state for prerequisites', () => {
      const deliveredPrereq = {
        ...SAMPLE_ISSUE,
        prerequisites: [
          { number: 200, title: 'Issue #200', delivered: true, stage: 'done', status: 'completed', mergeState: 'merged' },
        ],
        startEligibility: {
          startable: true,
          reason: 'ready',
          waitingForDelivery: [],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: deliveredPrereq,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.getByText(/#200 Issue #200/)).toBeTruthy()
      expect(screen.getByText('Delivered')).toBeTruthy()
    })
  })

  describe('waiting for delivery display', () => {
    it('should show waiting reason when issue is waiting for prerequisites', () => {
      const waitingIssue = {
        ...SAMPLE_ISSUE,
        startEligibility: {
          startable: false,
          reason: 'waiting-for-delivery',
          message: 'Issue #123 is waiting for prerequisite #200 to be delivered.',
          waitingForDelivery: [
            { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          ],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: waitingIssue,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.getByText(/waiting for/i)).toBeTruthy()
    })

    it('should not show waiting reason when issue is startable', () => {
      const startableIssue = {
        ...SAMPLE_ISSUE,
        startEligibility: {
          startable: true,
          reason: 'ready',
          waitingForDelivery: [],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: startableIssue,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.queryByText(/waiting for/i)).toBeNull()
    })
  })

  describe('start control behavior', () => {
    it('should disable or prevent start when waiting for delivery', () => {
      const waitingIssue = {
        ...SAMPLE_ISSUE,
        startEligibility: {
          startable: false,
          reason: 'waiting-for-delivery',
          message: 'Issue #123 is waiting for prerequisite #200 to be delivered.',
          waitingForDelivery: [
            { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          ],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: waitingIssue,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.queryAllByText(/^Start$/)).toHaveLength(1)
      expect(screen.getByText(/waiting for/i)).toBeTruthy()
    })

    it('should enable start when all prerequisites are delivered', () => {
      const readyIssue = {
        ...SAMPLE_ISSUE,
        prerequisites: [
          { number: 200, title: 'Issue #200', delivered: true, stage: 'done', status: 'completed', mergeState: 'merged' },
        ],
        startEligibility: {
          startable: true,
          reason: 'ready',
          waitingForDelivery: [],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: readyIssue,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.queryAllByText(/^Start$/).length).toBeGreaterThan(0)
    })
  })

  describe('circular prerequisite error handling', () => {
    it('should show validation message for circular declaration error', () => {
      const issueWithPrereq = {
        ...SAMPLE_ISSUE,
        prerequisites: [
          { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
        ],
        startEligibility: {
          startable: false,
          reason: 'waiting-for-delivery',
          waitingForDelivery: [
            { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          ],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: issueWithPrereq,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.getByText('Start Prerequisites')).toBeTruthy()
      expect(screen.queryByText(/circular/i)).toBeNull()
    })

    it('should render prerequisites outside backlog when API includes them', () => {
      const issueInBuild = {
        ...SAMPLE_ISSUE,
        stage: 'build' as const,
        prerequisites: [
          { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
        ],
        startEligibility: {
          startable: false,
          reason: 'waiting-for-delivery',
          waitingForDelivery: [
            { number: 200, title: 'Issue #200', delivered: false, stage: 'backlog', status: 'active' },
          ],
        },
      }
      mocks.useIssue.mockReturnValue({
        data: issueInBuild,
        isLoading: false,
        isError: false,
      })

      renderPage('/issue/123')

      expect(screen.getByText('Start Prerequisites')).toBeTruthy()
      expect(screen.getByText(/#200 Issue #200/)).toBeTruthy()
    })
  })
})
