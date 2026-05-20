// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { IssueDetailPage } from './IssueDetailPage'
import { CheckRepairState, Stage } from '../lib/types'

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
  status: 'blocked' as const,
  stage: 'check' as const,
  labels: [],
  createdAt: new Date().toISOString(),
  updatedAt: new Date().toISOString(),
  projectId: 'proj-1',
  projectName: 'TestProject',
}

const SAMPLE_DIFF_DATA = {
  available: false as const,
  reason: 'not_started' as const,
  message: 'Issue has not started yet.',
}

const SAMPLE_COMMITS_DATA = {
  available: false as const,
  reason: 'not_started' as const,
  message: 'Issue has not started yet.',
}

const CHECK_RECOVERY_ACTIONS = {
  recovery: {
    currentWorkItem: { type: 'check' as const, id: 'review-passed', title: 'Review passed' },
    latestAttemptState: 'failed' as const,
    workflowSummaryState: 'waiting-for-recovery' as const,
    allowedActions: ['retry', 'rerun', 'inspect'],
  },
}

function setupDefaultMocks(overrideStageState?: object, issueOverride: object = {}) {
  mocks.useIssue.mockReturnValue({
    data: { ...SAMPLE_ISSUE, ...issueOverride },
    isLoading: false,
    isError: false,
  })
  mocks.useIssueDiff.mockReturnValue({
    data: SAMPLE_DIFF_DATA,
  })
  mocks.useIssueCommits.mockReturnValue({
    data: SAMPLE_COMMITS_DATA,
  })
  mocks.useAgentStatus.mockReturnValue({
    data: { activeAgents: [], maxConcurrentAgents: 2 },
  })
  mocks.useExploreSessions.mockReturnValue({ data: [] })
  mocks.useCreateExploreSession.mockReturnValue({ mutateAsync: vi.fn() })
  mocks.useWorkflowRun.mockReturnValue({ data: undefined })
  mocks.useIssueStageState.mockReturnValue({
    data: overrideStageState ?? { stages: [] },
  })
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

function makeCheckRepairState(overrides: Partial<CheckRepairState> = {}): CheckRepairState {
  return {
    checkName: 'review-passed',
    fixTaskId: 'fix-review-findings',
    status: 'available',
    attemptsUsed: 0,
    attemptsMax: 1,
    attemptsRemaining: 1,
    repairAvailable: true,
    lastRepairTask: null,
    lastRepairStatus: null,
    followUpReviewStatus: null,
    stopReason: null,
    unresolvedSummary: null,
    ...overrides,
  }
}

function makeStageStateWithCheckRepair(checkRepair: CheckRepairState) {
  return {
    stages: [
      {
        stage: Stage.Check,
        status: 'failed' as const,
        tasks: [],
        checks: [
          {
            checkName: 'review-passed',
            status: 'failed' as const,
            message: 'Review found issues',
            output: { verdict: 'FAIL', summary: '2 issues remain' },
            runCount: 1,
            lastRunAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
          },
        ],
        approval: null,
        attempts: 0,
        startedAt: new Date().toISOString(),
        completedAt: null,
        updatedAt: new Date().toISOString(),
        checkRepair,
      },
    ],
  }
}

describe('Check repair display semantics', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  describe('repair task completed plus follow-up review failed', () => {
    it('shows both repair task completed and follow-up check failed together', () => {
      const checkRepair = makeCheckRepairState({
        status: 'exhausted',
        attemptsUsed: 1,
        attemptsMax: 1,
        attemptsRemaining: 0,
        repairAvailable: false,
        lastRepairTask: {
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed',
          source: 'dynamic',
          order: 100,
          attempts: 1,
          duration: 15000,
          artifacts: [],
          output: { summary: 'Fix completed' },
          startedAt: new Date().toISOString(),
          completedAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        },
        lastRepairStatus: 'completed',
        followUpReviewStatus: 'failed',
        stopReason: 'max-repair-attempts-reached',
        unresolvedSummary: '2 issues remain',
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair))
      renderPage()

      expect(screen.getByText(/follow-up check failed/i)).toBeTruthy()
    })

    it('does not present repair completion as review gate success', () => {
      const checkRepair = makeCheckRepairState({
        status: 'exhausted',
        attemptsUsed: 1,
        attemptsMax: 1,
        attemptsRemaining: 0,
        repairAvailable: false,
        lastRepairTask: {
          taskId: 'fix-review-findings',
          title: 'Fix review findings',
          status: 'completed',
          source: 'dynamic',
          order: 100,
          attempts: 1,
          duration: 15000,
          artifacts: [],
          output: { summary: 'Fix completed' },
          startedAt: new Date().toISOString(),
          completedAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        },
        lastRepairStatus: 'completed',
        followUpReviewStatus: 'failed',
        stopReason: 'max-repair-attempts-reached',
        unresolvedSummary: '2 issues remain',
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair))
      renderPage()

      expect(screen.queryByText(/Review passed/i)).toBeNull()
      const pageText = document.body.textContent ?? ''
      const hasFollowUpReviewFailed = /follow-up check failed/i.test(pageText)
      const hasRepairCompleted = /completed/i.test(pageText)
      expect(hasRepairCompleted && hasFollowUpReviewFailed).toBe(true)
    })
  })

  describe('exhausted repair budget guidance', () => {
    it('shows exhausted status when repair budget is depleted', () => {
      const checkRepair = makeCheckRepairState({
        status: 'exhausted',
        attemptsUsed: 1,
        attemptsMax: 1,
        attemptsRemaining: 0,
        repairAvailable: false,
        lastRepairStatus: 'completed',
        followUpReviewStatus: 'failed',
        stopReason: 'max-repair-attempts-reached',
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair))
      renderPage()

      expect(screen.getByText(/Auto-fix exhausted/i)).toBeTruthy()
    })

    it('shows non-automatic continuation guidance when repair is exhausted', () => {
      const checkRepair = makeCheckRepairState({
        status: 'exhausted',
        attemptsUsed: 1,
        attemptsMax: 1,
        attemptsRemaining: 0,
        repairAvailable: false,
        lastRepairStatus: 'completed',
        followUpReviewStatus: 'failed',
        stopReason: 'max-repair-attempts-reached',
        unresolvedSummary: '2 issues remain',
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair))
      renderPage()

      expect(screen.getByText(/Auto-fix will not continue automatically/i)).toBeTruthy()
    })

    it('does not show Fix review findings button when repair is exhausted', () => {
      const checkRepair = makeCheckRepairState({
        status: 'exhausted',
        attemptsUsed: 1,
        attemptsMax: 1,
        attemptsRemaining: 0,
        repairAvailable: false,
        lastRepairStatus: 'completed',
        followUpReviewStatus: 'failed',
        stopReason: 'max-repair-attempts-reached',
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair))
      renderPage()

      expect(screen.queryByText(/Fix review findings/i)).toBeNull()
    })
  })

  describe('intent-specific action labels', () => {
    it('shows Retry checkpoint instead of ambiguous Retry for blocked check issues with checkRepair', () => {
      const checkRepair = makeCheckRepairState({
        status: 'available',
        attemptsUsed: 0,
        attemptsMax: 1,
        attemptsRemaining: 1,
        repairAvailable: true,
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair), CHECK_RECOVERY_ACTIONS)
      renderPage()

      expect(screen.getByText(/Retry checkpoint/i)).toBeTruthy()
      expect(screen.queryByText(/^Retry$/)).toBeNull()
    })

    it('shows Rerun stage as a distinct action from Fix review findings', () => {
      const checkRepair = makeCheckRepairState({
        status: 'available',
        attemptsUsed: 0,
        attemptsMax: 1,
        attemptsRemaining: 1,
        repairAvailable: true,
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair), CHECK_RECOVERY_ACTIONS)
      renderPage()

      expect(screen.getByText(/Rerun stage/i)).toBeTruthy()
      expect(screen.getByText(/Fix review findings/i)).toBeTruthy()
    })

    it('does not show Check-only actions when current issue stage is not Check', () => {
      const checkRepair = makeCheckRepairState({
        status: 'available',
        attemptsUsed: 0,
        attemptsMax: 1,
        attemptsRemaining: 1,
        repairAvailable: true,
      })

      setupDefaultMocks(makeStageStateWithCheckRepair(checkRepair), { stage: Stage.Build })
      renderPage()

      expect(screen.queryByText(/Retry checkpoint/i)).toBeNull()
      expect(screen.queryByText(/Rerun stage/i)).toBeNull()
      expect(screen.queryByText(/Fix review findings/i)).toBeNull()
    })
  })
})
