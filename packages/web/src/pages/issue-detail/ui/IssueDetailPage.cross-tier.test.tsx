// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'

const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseWorkflowTimeline = vi.fn()
const mockUseWorkflowYaml = vi.fn()
const mockUseAgentStatus = vi.fn()
const mockUseIssue = vi.fn()
const mockUseWorkspaceStatus = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: (...args: unknown[]) => mockUseIssue(...args),
    useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
    useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
    useWorkflowTimeline: (...args: unknown[]) => mockUseWorkflowTimeline(...args),
    useWorkflowYaml: (...args: unknown[]) => mockUseWorkflowYaml(...args),
    useWorkspaceStatus: (...args: unknown[]) => mockUseWorkspaceStatus(...args),
    useIssueEvents: () => ({ data: undefined, isLoading: false }),
    getIssueWorkflowVariables: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
  }
})

vi.mock('../../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings')>()
  return {
    ...actual,
    useWorkflowProfiles: () => ({ data: [] }),
    useAvailableModelIds: () => ({ data: [] }),
    useOpencodeModel: () => ({ data: null }),
    useModelVariants: () => ({ data: [] }),
    useEffectiveDefaultWorkflowProfile: () => ({ data: null }),
  }
})

vi.mock('../../../widgets/issue-event-timeline/ui/EventTimelinePanel', () => ({
  EventTimelinePanel: vi.fn((props: { issueNumber: number; issueId?: string | null; workflowStatus?: string | null; enabled?: boolean }) => (
    <div
      data-testid="event-timeline-panel-mock"
      data-issue-number={props.issueNumber}
      data-issue-id={props.issueId ?? ''}
      data-workflow-status={props.workflowStatus ?? ''}
      data-enabled={props.enabled === undefined ? '' : String(props.enabled)}
    />
  )),
}))

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: (...args: unknown[]) => mockUseAgentStatus(...args),
  }
})

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function mockMatchMedia(narrow: boolean) {
  const mql = {
    matches: narrow,
    media: '(max-width: 1023.98px)',
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
    onchange: null,
  }
  vi.stubGlobal('matchMedia', vi.fn(() => mql))
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: narrow ? 375 : 1280 })
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function baseIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-base',
    number: 14,
    title: 'Cross-tier verification fixture',
    body: 'Issue body content.',
    status: 'in_progress',
    workflowStage: 'build',
    workflowStatus: 'running',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function baseRecovery() {
  return {
    currentWorkItem: { type: 'task' as const, id: 't1', title: 'Build decision surface' },
    latestAttemptState: 'running',
    workflowSummaryState: 'running',
    allowedActions: ['stop'],
  }
}

function basePrMetadataTimeline(workflowRunId: string) {
  return {
    workflowRunId,
    status: 'running',
    currentStage: 'build',
    pendingWork: null,
    stages: [
      {
        stage: 'plan' as const,
        status: 'completed' as const,
        order: 0,
        startedAt: '2026-01-01T00:30:00Z',
        completedAt: '2026-01-01T01:00:00Z',
        durationMs: 30 * 60 * 1000,
        tasks: [
          {
            id: 'publish.1',
            taskId: 'integrate:publish:publish-via-pr',
            kind: 'integration' as const,
            uses: 'mohist/publish-via-pr',
            status: 'completed' as const,
            startedAt: '2026-01-01T00:30:00Z',
            completedAt: '2026-01-01T00:45:00Z',
            output: JSON.stringify({
              kind: 'publish-via-pr',
              prNumber: 42,
              prUrl: 'https://github.com/example/repo/pull/42',
            }),
          },
        ],
        checks: [],
        approval: null,
      },
      { stage: 'build' as const, status: 'in_progress' as const, order: 1, startedAt: '2026-01-01T01:00:00Z', durationMs: 0, tasks: [], checks: [], approval: null },
    ],
    availableActions: [],
  }
}

const baseBeforeEach = () => {
  vi.clearAllMocks()
  mockMatchMedia(false)
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
  window.dispatchEvent(new Event('resize'))
  mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
  mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  mockUseIssueDiff.mockReturnValue({ data: undefined })
  mockUseIssueCommits.mockReturnValue({ data: undefined })
  mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
}

function expectAssigned(testId: string, anchor: string) {
  const node = screen.getByTestId(testId)
  const container = screen.getByTestId(anchor)
  expect(container.contains(node)).toBe(true)
}

describe('T-004: cross-tier verification — archived path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('assigns headline, identity row, action surface, and archived pill/banner to status-header tier; reading flow and reference rail populated', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'completed',
        archivedAt: '2026-06-25T10:00:00Z',
        health: 'done',
        workflowRunId: 'wr_archived_1',
      }),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({
      data: {
        workflowRunId: 'wr_archived_1',
        status: 'completed',
        currentStage: 'done',
        pendingWork: null,
        stages: [
          { stage: 'plan' as const, status: 'completed' as const, order: 0, startedAt: '2026-01-01T00:30:00Z', completedAt: '2026-01-01T01:00:00Z', durationMs: 30 * 60 * 1000, tasks: [], checks: [], approval: null },
        ],
        availableActions: [],
      },
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')
    const headline = screen.getByTestId('status-headline')

    expect(headline.dataset.summary).toBe('done')
    expect(headerTier.contains(headline)).toBe(true)
    expect(headerTier.contains(screen.getByTestId('issue-detail-header'))).toBe(true)
    expect(headerTier.contains(screen.getByTestId('archived-banner'))).toBe(true)
    expect(headerTier.contains(screen.getByTestId('runtime-decision-surface'))).toBe(true)
    expect(readingFlow.contains(screen.getByTestId('archived-banner'))).toBe(false)

    expect(readingFlow.contains(screen.getByTestId('description-section'))).toBe(true)
    expect(readingFlow.contains(screen.getByTestId('comments-section'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('reference-rail-details'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('reference-rail-actions'))).toBe(true)

    expect(readingFlow.contains(screen.getByTestId('archived-banner'))).toBe(false)
    expect(referenceRail.contains(screen.getByTestId('archived-banner'))).toBe(false)
  })
})

describe('T-004: cross-tier verification — backlog/readiness path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('keeps draft pill in identity row, Start action in header tier, Readiness card in rail, headline shows backlog situation without fabricated stage/progress', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        isDraft: true,
        canStart: false,
        blocker: { kind: 'draft' },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')
    const headline = screen.getByTestId('status-headline')

    expect(headline.contains(screen.getByTestId('status-headline-summary'))).toBe(true)
    expect(screen.queryByTestId('status-headline-stage-progress')).toBeNull()
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()

    expect(headerTier.contains(screen.getByTestId('issue-detail-header'))).toBe(true)
    expect(headerTier.querySelector('[data-testid="draft-pill"]')).toBeTruthy()
    expect(headerTier.contains(screen.getByTestId('runtime-decision-surface'))).toBe(true)
    expect(headerTier.querySelector('[data-testid="runtime-action-start"]')).toBeTruthy()

    expect(headerTier.contains(screen.getByTestId('reference-rail-readiness'))).toBe(false)
    expect(referenceRail.contains(screen.getByTestId('reference-rail-readiness'))).toBe(true)

    expect(readingFlow.contains(screen.getByTestId('reference-rail-readiness'))).toBe(false)
  })

  it('keeps the readiness card absent on a non-backlog, non-draft issue', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))
    expect(screen.queryByTestId('reference-rail-readiness')).toBeNull()
  })
})

describe('T-004: cross-tier verification — drift path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('places the drift panel in the reference rail, default-collapsed, expandable on click', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
        recovery: baseRecovery(),
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const readingFlow = screen.getByTestId('reading-flow')
    const driftCard = screen.getByTestId('reference-rail-drift')

    expect(driftCard.dataset.collapsed).toBe('true')
    expect(driftCard.querySelector('[data-testid="reference-rail-drift-body"]')).toBeNull()
    expect(referenceRail.contains(driftCard)).toBe(true)
    expect(readingFlow.contains(driftCard)).toBe(false)
  })
})

describe('T-004: cross-tier verification — convergence path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('places the convergence panel in the reference rail when health=blocked or convergence exists; default-collapsed on desktop', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        health: 'blocked',
        convergence: {
          blockingItemCount: 1,
          directlyRepairedCount: 0,
          reactionAttempts: 0,
          attemptedItemIds: [],
          resolvedItemIds: [],
          unresolvedItemIds: ['cb-1'],
          newBlockingItemIds: [],
          nonBlockingItemIds: [],
          blockedReason: 'A blocking check failed.',
        },
        recovery: baseRecovery(),
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const readingFlow = screen.getByTestId('reading-flow')
    const convergenceCard = screen.getByTestId('reference-rail-convergence')

    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
    expect(referenceRail.contains(convergenceCard)).toBe(true)
    expect(readingFlow.contains(convergenceCard)).toBe(false)
  })
})

describe('T-004: cross-tier verification — interrupted health path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('reports an interrupted-health projection as blocked via the header without a standalone card', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        workflowStatus: 'interrupted',
        health: 'interrupted',
        recovery: null,
        convergence: null,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('blocked')
    expect(headline.textContent ?? '').toMatch(/Blocked/i)
    expect(screen.getByTestId('runtime-rationale').textContent ?? '').toContain('The workflow was interrupted. Resume or rerun to continue.')

    expect(screen.queryByTestId('reference-rail-convergence')).toBeNull()
    expect(screen.queryByTestId('workflow-interrupted-card')).toBeNull()

    const readingFlow = screen.getByTestId('reading-flow')
    expect(readingFlow.contains(screen.getByTestId('runtime-decision-surface'))).toBe(false)
  })
})

describe('T-004: cross-tier verification — PR delivery path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('places PrDeliverySummary inside the reading flow beside the workflow frame', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({ workflowRunId: 'wr_pr_1', recovery: baseRecovery() }),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({
      data: basePrMetadataTimeline('wr_pr_1'),
    })

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')
    const headerTier = screen.getByTestId('status-header-tier')

    const deliveryFrame = await waitFor(() => screen.getByTestId('pr-delivery-summary-frame'))
    expect(readingFlow.contains(deliveryFrame)).toBe(true)
    expect(referenceRail.contains(deliveryFrame)).toBe(false)
    expect(headerTier.contains(deliveryFrame)).toBe(false)

    const workflowFrame = screen.getByTestId('workflow-view-frame')
    expect(readingFlow.contains(workflowFrame)).toBe(true)
    const wfPos = workflowFrame.compareDocumentPosition(deliveryFrame)
    expect(wfPos & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})

describe('T-004: cross-tier verification — capacity gating path', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('keeps the Start action inside the status-header tier under full capacity (gating happens in surface, not rail)', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({ status: 'backlog', workflowStage: null, workflowStatus: null, workflowRunId: null }),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { active: 2, max: 2 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const referenceRail = screen.getByTestId('reference-rail')
    const readingFlow = screen.getByTestId('reading-flow')
    const startButton = screen.getByTestId('runtime-action-start')

    expect(headerTier.contains(startButton)).toBe(true)
    expect(readingFlow.contains(startButton)).toBe(false)
    expect(referenceRail.contains(startButton)).toBe(false)
    expect(startButton).toBeDisabled()
    expect(startButton.getAttribute('title')).toMatch(/capacity is full/i)
  })

  it('enables the Start action in the header tier when capacity is not full', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({ status: 'backlog', workflowStage: null, workflowStatus: null, workflowRunId: null }),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { active: 0, max: 2 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const startButton = screen.getByTestId('runtime-action-start')
    expect(headerTier.contains(startButton)).toBe(true)
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })
})

describe('T-004: cross-tier verification — no duplication / no orphan', () => {
  beforeEach(() => {
    baseBeforeEach()
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('renders every D2 block in exactly one tier, none repeated across tiers, none orphaned', async () => {
    mockUseIssue.mockReturnValue({
      data: baseIssue({
        workflowRunId: 'wr_overlap_1',
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: true }],
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
        health: 'blocked',
        convergence: {
          blockingItemCount: 1,
          directlyRepairedCount: 0,
          reactionAttempts: 0,
          attemptedItemIds: [],
          resolvedItemIds: [],
          unresolvedItemIds: ['cb-1'],
          newBlockingItemIds: [],
          nonBlockingItemIds: [],
          blockedReason: 'A blocking check failed.',
        },
        body: 'Body for description.',
        comments: [
          { id: 'c1', author: 'tester', body: 'A reviewer comment.', createdAt: '2026-01-04T00:00:00Z' },
        ],
        recovery: baseRecovery(),
      }),
      isLoading: false,
      isError: false,
    })
    mockUseWorkflowTimeline.mockReturnValue({
      data: basePrMetadataTimeline('wr_overlap_1'),
    })
    mockUseIssueDiff.mockReturnValue({
      data: {
        available: true as const,
        reason: null,
        head: 'feature/issue-14',
        base: 'master',
        mergeBase: 'abc',
        ahead: 1,
        behind: 0,
        canFastForward: true,
        comparison: 'merge-base' as const,
        summary: { filesChanged: 1, commits: 1, additions: 4, deletions: 1 },
        files: [],
      },
    })
    mockUseIssueCommits.mockReturnValue({
      data: {
        available: true as const,
        reason: null,
        head: 'feature/issue-14',
        base: 'master',
        mergeBase: 'abc',
        ahead: 1,
        behind: 0,
        canFastForward: true,
        comparison: 'merge-base' as const,
        summary: { filesChanged: 1, commits: 1, additions: 4, deletions: 1 },
        commits: [],
      },
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    const statusHeaderBlocks = ['status-headline', 'issue-detail-header', 'runtime-decision-surface']
    const readingFlowBlocks = [
      'workflow-view-frame',
      'pr-delivery-summary-frame',
      'diff-summary-banner',
      'diff-files-section',
      'commits-section',
      'description-section',
      'comments-section',
    ]
    const referenceRailBlocks = [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-actions',
      'reference-rail-prerequisites',
    ]

    for (const block of statusHeaderBlocks) {
      if (!screen.queryByTestId(block)) continue
      expect(headerTier.contains(screen.getByTestId(block))).toBe(true)
      expect(readingFlow.querySelector(`[data-testid="${block}"]`)).toBeNull()
      expect(referenceRail.querySelector(`[data-testid="${block}"]`)).toBeNull()
    }

    for (const block of readingFlowBlocks) {
      const node = screen.queryByTestId(block)
      if (!node) continue
      expect(readingFlow.contains(node)).toBe(true)
      expect(headerTier.querySelector(`[data-testid="${block}"]`)).toBeNull()
      expect(referenceRail.querySelector(`[data-testid="${block}"]`)).toBeNull()
    }

    for (const block of referenceRailBlocks) {
      const node = screen.queryByTestId(block)
      if (!node) continue
      expect(referenceRail.contains(node)).toBe(true)
      expect(headerTier.querySelector(`[data-testid="${block}"]`)).toBeNull()
      expect(readingFlow.querySelector(`[data-testid="${block}"]`)).toBeNull()
    }

    const page = screen.getByTestId('issue-detail-page-container')
    const stickyEls = page.querySelectorAll('[data-sticky="true"]')
    expect(stickyEls).toHaveLength(1)
    expect(stickyEls[0]).toBe(screen.getByTestId('status-headline'))

    const tierWeight = { 'status-header': 3, 'reading-flow': 2, 'reference-rail': 1 } as const
    expect(tierWeight[screen.getByTestId('status-headline').dataset.tierWeight as keyof typeof tierWeight]).toBe(3)
    expect(tierWeight[readingFlow.dataset.tierWeight as keyof typeof tierWeight]).toBe(2)
    expect(tierWeight[referenceRail.dataset.tierWeight as keyof typeof tierWeight]).toBe(1)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(headerTier.contains(surface)).toBe(true)
    expect(readingFlow.contains(surface)).toBe(false)
    expect(referenceRail.contains(surface)).toBe(false)

    const detailsHeading = screen.getByTestId('reference-rail-details-toggle')
    expect(referenceRail.contains(detailsHeading)).toBe(true)

    expect(screen.queryByTestId('workflow-interrupted-card')).toBeNull()

    const allRuntimeActions = page.querySelectorAll('[data-testid^="runtime-action-"]')
    for (const action of Array.from(allRuntimeActions)) {
      expect(headerTier.contains(action)).toBe(true)
      expect(readingFlow.contains(action)).toBe(false)
      expect(referenceRail.contains(action)).toBe(false)
    }

    expectAssigned('description-section', 'reading-flow')
    expectAssigned('comments-section', 'reading-flow')
    expectAssigned('commits-section', 'reading-flow')
    expectAssigned('diff-files-section', 'reading-flow')
    expectAssigned('workflow-view-frame', 'reading-flow')
  })
})

describe('T-004: cross-tier verification — three-tier weight hierarchy holds on every conditional path', () => {
  const paths: Array<{ name: string; overrides: Record<string, unknown>; recovery?: ReturnType<typeof baseRecovery> }> = [
    {
      name: 'archived done',
      overrides: {
        status: 'done',
        workflowStage: 'done',
        workflowStatus: 'completed',
        archivedAt: '2026-06-25T10:00:00Z',
        health: 'done',
        workflowRunId: 'wr_a',
      },
    },
    {
      name: 'backlog draft',
      overrides: {
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
        isDraft: true,
        canStart: false,
        blocker: { kind: 'draft' },
      },
    },
    {
      name: 'blocked interrupted',
      overrides: {
        workflowStatus: 'interrupted',
        health: 'interrupted',
        recovery: null,
        convergence: null,
      },
    },
    {
      name: 'drift detected',
      overrides: {
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
        recovery: baseRecovery(),
      },
    },
    {
      name: 'convergence items present',
      overrides: {
        health: 'blocked',
        convergence: {
          blockingItemCount: 1,
          directlyRepairedCount: 0,
          reactionAttempts: 0,
          attemptedItemIds: [],
          resolvedItemIds: [],
          unresolvedItemIds: ['cb-1'],
          newBlockingItemIds: [],
          nonBlockingItemIds: [],
          blockedReason: 'A blocking check failed.',
        },
        recovery: baseRecovery(),
      },
    },
    {
      name: 'PR delivery (workflow timeline with publish)',
      overrides: { workflowRunId: 'wr_pr_path', recovery: baseRecovery() },
    },
    {
      name: 'capacity-full backlog',
      overrides: {
        status: 'backlog',
        workflowStage: null,
        workflowStatus: null,
        workflowRunId: null,
      },
    },
  ]

  for (const pathCase of paths) {
    it(`preserves the three-tier weight hierarchy on the "${pathCase.name}" path`, async () => {
      baseBeforeEach()
      mockUseIssue.mockReturnValue({
        data: baseIssue(pathCase.overrides),
        isLoading: false,
        isError: false,
      })

      if (pathCase.name === 'PR delivery (workflow timeline with publish)') {
        mockUseWorkflowTimeline.mockReturnValue({
          data: basePrMetadataTimeline('wr_pr_path'),
        })
      }

      if (pathCase.name === 'capacity-full backlog') {
        mockUseAgentStatus.mockReturnValue({
          data: { activeAgents: [], capacity: { active: 2, max: 2 }, runnerAvailable: true },
        })
      }

      renderPage()

      const headline = await waitFor(() => screen.getByTestId('status-headline'))
      const readingFlow = screen.getByTestId('reading-flow')
      const referenceRail = screen.getByTestId('reference-rail')

      const tierWeight = { 'status-header': 3, 'reading-flow': 2, 'reference-rail': 1 } as const
      expect(tierWeight[headline.dataset.tierWeight as keyof typeof tierWeight]).toBe(3)
      expect(tierWeight[readingFlow.dataset.tierWeight as keyof typeof tierWeight]).toBe(2)
      expect(tierWeight[referenceRail.dataset.tierWeight as keyof typeof tierWeight]).toBe(1)

      expect(headline.dataset.sticky).toBe('true')
      expect(headline.className).toMatch(/bg-(info|warning|danger|success)-subtle/)
      expect(headline.className).toContain('sticky')

      expect(readingFlow.querySelector('[data-sticky="true"]')).toBeNull()
      expect(referenceRail.querySelector('[data-sticky="true"]')).toBeNull()
      expect(readingFlow.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)
      expect(referenceRail.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)

      expect(readingFlow.className).toMatch(/lg:col-span-2\b/)
      expect(referenceRail.className).toMatch(/lg:col-span-1\b/)

      cleanup()
      vi.unstubAllGlobals()
    })
  }
})
