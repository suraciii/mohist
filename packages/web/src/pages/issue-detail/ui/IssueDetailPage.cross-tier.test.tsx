import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockAgentStatus, mockIssue, mockIssueCommits, mockIssueDiff, mockWorkflowTimeline, mountIssueDetail } from './_issueDetailMsw'
import { setScopedValue } from '../../../../tests/support/scoped-property'


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
  setScopedValue(window, 'innerWidth', narrow ? 375 : 1280)
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

mountIssueDetail({ issue: baseIssue() })

function resetViewport() {
  mockMatchMedia(false)
  setScopedValue(window, 'innerWidth', 1280)
  window.dispatchEvent(new Event('resize'))
}

beforeEach(() => {
  resetViewport()
})

afterEach(() => {
  cleanup()
})

function expectAssigned(testId: string, anchor: string) {
  const node = screen.getByTestId(testId)
  const container = screen.getByTestId(anchor)
  expect(container.contains(node)).toBe(true)
}

describe('IssueDetailPage cross-tier verification: archived path', () => {
  it('assigns headline, identity row, action surface, and archived pill/banner to status-header tier; reading flow and reference rail populated', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      archivedAt: '2026-06-25T10:00:00Z',
      health: 'done',
      workflowRunId: 'wr_archived_1',
    }))
    mockWorkflowTimeline({
      workflowRunId: 'wr_archived_1',
      status: 'completed',
      currentStage: 'done',
      pendingWork: null,
      stages: [
        { stage: 'plan' as const, status: 'completed' as const, order: 0, startedAt: '2026-01-01T00:30:00Z', completedAt: '2026-01-01T01:00:00Z', durationMs: 30 * 60 * 1000, tasks: [], checks: [], approval: null },
      ],
      availableActions: [],
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

describe('IssueDetailPage cross-tier verification: backlog readiness path', () => {
  it('keeps draft pill in identity row, Start action in header tier, Readiness card in rail, headline shows backlog situation without fabricated stage/progress', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      isDraft: true,
      canStart: false,
      blocker: { kind: 'draft' },
    }))

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
    mockIssue(baseIssue())

    renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))
    expect(screen.queryByTestId('reference-rail-readiness')).toBeNull()
  })
})

describe('IssueDetailPage cross-tier verification: drift path', () => {
  it('places the drift panel in the reference rail, default-collapsed, expandable on click', async () => {
    mockIssue(baseIssue({
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: baseRecovery(),
    }))

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

describe('IssueDetailPage cross-tier verification: convergence path', () => {
  it('places the convergence panel in the reference rail when health=blocked or convergence exists; default-collapsed on desktop', async () => {
    mockIssue(baseIssue({
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
    }))

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

describe('IssueDetailPage cross-tier verification: blocked health path', () => {
  it('reports a blocked projection via the header without a standalone recovery card', async () => {
    mockIssue(baseIssue({
      workflowStatus: 'failed',
      health: 'blocked',
      recovery: null,
      convergence: null,
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('blocked')
    expect(headline.textContent ?? '').toMatch(/Blocked/i)
    expect(screen.getByTestId('runtime-rationale').textContent ?? '').toContain('The workflow is blocked and needs an action to continue.')

    expect(screen.queryByTestId('reference-rail-convergence')).toBeNull()

    const readingFlow = screen.getByTestId('reading-flow')
    expect(readingFlow.contains(screen.getByTestId('runtime-decision-surface'))).toBe(false)
  })
})

describe('IssueDetailPage cross-tier verification: PR delivery path', () => {
  it('places PrDeliverySummary inside the reading flow beside the workflow frame', async () => {
    mockIssue(baseIssue({ workflowRunId: 'wr_pr_1', recovery: baseRecovery() }))
    mockWorkflowTimeline(basePrMetadataTimeline('wr_pr_1'))

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

describe('IssueDetailPage cross-tier verification: capacity gating path', () => {
  it('keeps the Start action inside the status-header tier under full capacity (gating happens in surface, not rail)', async () => {
    mockIssue(baseIssue({ status: 'backlog', workflowStage: null, workflowStatus: null, workflowRunId: null }))
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 2, max: 2 },
      runnerAvailable: true,
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
    mockIssue(baseIssue({ status: 'backlog', workflowStage: null, workflowStatus: null, workflowRunId: null }))
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 0, max: 2 },
      runnerAvailable: true,
    })

    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const startButton = screen.getByTestId('runtime-action-start')
    expect(headerTier.contains(startButton)).toBe(true)
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })
})

describe('IssueDetailPage cross-tier verification: unique tier assignment', () => {
  it('renders every D2 block in exactly one tier, none repeated across tiers, none orphaned', async () => {
    mockIssue(baseIssue({
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
    }))
    mockWorkflowTimeline(basePrMetadataTimeline('wr_overlap_1'))
    const diffData = {
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
    }
    mockIssueDiff(diffData)
    mockIssueCommits({ ...diffData, commits: [] })

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

describe('IssueDetailPage cross-tier verification: tier hierarchy across conditional paths', () => {
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
      name: 'blocked recovery',
      overrides: {
        workflowStatus: 'failed',
        health: 'blocked',
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
      resetViewport()
      mockIssue(baseIssue(pathCase.overrides))

      if (pathCase.name === 'PR delivery (workflow timeline with publish)') {
        mockWorkflowTimeline(basePrMetadataTimeline('wr_pr_path'))
      }

      if (pathCase.name === 'capacity-full backlog') {
        mockAgentStatus({ activeAgents: [], capacity: { active: 2, max: 2 }, runnerAvailable: true })
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
    })
  }
})
