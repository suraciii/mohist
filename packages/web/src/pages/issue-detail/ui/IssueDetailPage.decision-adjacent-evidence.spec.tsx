import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import {
  mockArtifacts,
  mockIssue,
  mountIssueDetail,
} from './_issueDetailMsw'


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
    title: 'Test Issue',
    body: '',
    status: 'in_progress',
    workflowStage: 'check',
    workflowRunId: 'wr-1',
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

mountIssueDetail({ issue: baseIssue() })

beforeEach(() => {
  mockMatchMedia(false)
  setScopedValue(window, 'innerWidth', 1280)
  window.dispatchEvent(new Event('resize'))
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function makePlanArtifact(): Record<string, unknown> {
  return {
    artifactId: 'art-plan',
    workflowRunId: 'wr-1',
    taskRunId: 'plan.1',
    path: 'plan.md',
    kind: 'file',
    contentType: 'text/markdown',
    size: 256,
    recordedAt: '2026-01-01T00:00:00.000Z',
    displayName: 'plan.md',
  }
}

function makeReviewArtifact(): Record<string, unknown> {
  return {
    artifactId: 'art-review',
    workflowRunId: 'wr-1',
    taskRunId: 'ai-review.1',
    path: 'review.md',
    kind: 'file',
    contentType: 'text/markdown',
    size: 384,
    recordedAt: '2026-01-01T01:00:00.000Z',
    displayName: 'review.md',
  }
}

function makeCheckLogArtifact(): Record<string, unknown> {
  return {
    artifactId: 'art-check',
    workflowRunId: 'wr-1',
    taskRunId: 'check.1',
    path: 'check.log',
    kind: 'file',
    contentType: 'text/plain',
    size: 1024,
    recordedAt: '2026-01-01T02:00:00.000Z',
    displayName: 'check.log',
  }
}

describe('Decision evidence: approval decision', () => {
  it('shows a compact plan/check evidence block inside the surface during an approval decision', async () => {
    mockIssue(baseIssue({
      workflowStage: 'check',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))
    mockArtifacts([makePlanArtifact(), makeReviewArtifact()])

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('approval-required')

    const evidence = await within(surface).findByTestId('runtime-evidence')
    expect(evidence.dataset.summary).toBe('approval-required')

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(evidence)).toBe(true)

    const list = within(evidence).getByTestId('runtime-evidence-list')
    expect(list.dataset.mode).toBe('compact')
    expect(within(list).getByText('plan.md')).toBeInTheDocument()
    expect(within(list).getByText('review.md')).toBeInTheDocument()
  })

  it('places the compact evidence items inside the status-header tier, not in the reading flow', async () => {
    mockIssue(baseIssue({
      workflowStage: 'check',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))
    mockArtifacts([makePlanArtifact()])

    renderPage()

    const evidence = await waitFor(() => screen.getByTestId('runtime-evidence'))
    const headerTier = screen.getByTestId('status-header-tier')
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(headerTier.contains(evidence)).toBe(true)
    expect(readingFlow.contains(evidence)).toBe(false)
    expect(referenceRail.contains(evidence)).toBe(false)
  })

  it('does not render the compact evidence block during a running decision', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockArtifacts([makePlanArtifact()])

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-evidence')).toBeNull()
  })

  it('does not render the compact evidence block during a queued/backlog decision', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: { kind: 'draft' },
    }))
    mockArtifacts([makePlanArtifact()])

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-evidence')).toBeNull()
  })

  it('does not render the compact evidence block during a done decision', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      health: 'done',
    }))
    mockArtifacts([makePlanArtifact()])

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-evidence')).toBeNull()
  })

  it('keeps the full LatestArtifactsPanel unchanged in the reading flow when control-region evidence is present', async () => {
    mockIssue(baseIssue({
      workflowStage: 'check',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))
    mockArtifacts([makePlanArtifact(), makeReviewArtifact()])

    const { container } = renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const evidence = await within(surface).findByTestId('runtime-evidence')

    const fullList = container.querySelector('[data-testid="latest-artifacts-list"]')
    expect(fullList).toBeTruthy()
    expect(within(fullList as HTMLElement).getByText('plan.md')).toBeInTheDocument()
    expect(within(fullList as HTMLElement).getByText('review.md')).toBeInTheDocument()

    expect(evidence.contains(fullList)).toBe(false)
    expect(within(fullList as HTMLElement).getAllByTestId('latest-artifact-item').length).toBe(2)
    expect(within(evidence).getAllByTestId('latest-artifact-item').length).toBe(2)
  })

  it('shares the artifact query between the full panel and the surface (single network call per query)', async () => {
    let artifactCallCount = 0
    const { server } = await import('../../../../tests/support/msw')
    server.use(
      ...[
        (await import('msw')).http.get('*/api/projects/:projectId/issues/:number/workflow/artifacts', async () => {
          artifactCallCount += 1
          return (await import('msw')).HttpResponse.json({
            success: true,
            data: [makePlanArtifact(), makeReviewArtifact()],
          })
        }),
      ],
    )

    mockIssue(baseIssue({
      workflowStage: 'check',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-evidence'))
    await waitFor(() => {
      expect(screen.getByTestId('latest-artifacts-list')).toBeInTheDocument()
    })

    await new Promise((resolve) => setTimeout(resolve, 50))

    expect(artifactCallCount).toBeLessThanOrEqual(2)
  })
})

describe('Decision evidence: blocked and failed recovery', () => {
  it('shows the compact evidence block during a blocked decision', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'interrupted',
      health: 'blocked',
      recovery: null,
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
    }))
    mockArtifacts([makePlanArtifact(), makeCheckLogArtifact()])

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('blocked')

    const evidence = await within(surface).findByTestId('runtime-evidence')
    expect(evidence.dataset.summary).toBe('blocked')
    expect(within(evidence).getByText('plan.md')).toBeInTheDocument()
    expect(within(evidence).getByText('check.log')).toBeInTheDocument()

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(evidence)).toBe(true)
  })

  it('shows the compact evidence block during a failed decision', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'failed',
      health: 'blocked',
      blockedReason: 'A check failed.',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'failed',
        workflowSummaryState: 'failed',
        allowedActions: ['retry'],
      },
    }))
    mockArtifacts([makeReviewArtifact()])

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('failed')

    const evidence = await within(surface).findByTestId('runtime-evidence')
    expect(evidence.dataset.summary).toBe('failed')
    expect(within(evidence).getByText('review.md')).toBeInTheDocument()
  })

  it('opens the ArtifactContentViewer when a compact evidence item is clicked', async () => {
    mockIssue(baseIssue({
      workflowStage: 'check',
      health: 'paused',
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))
    mockArtifacts([makePlanArtifact()])

    const { server } = await import('../../../../tests/support/msw')
    const { http, HttpResponse } = await import('msw')
    server.use(
      http.get(
        '*/api/projects/:projectId/issues/:number/workflow/artifacts/:artifactId/content',
        () => HttpResponse.json({
          success: true,
          data: { kind: 'text', content: '# Plan content', contentType: 'text/markdown' },
        }),
      ),
    )

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const evidence = await within(surface).findByTestId('runtime-evidence')
    const planButton = within(evidence).getByText('plan.md')

    fireEvent.click(planButton)

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: /Plan content|Plan|Artifact|plan\.md/i })).toBeInTheDocument()
    })
  })
})

describe('Decision evidence: reading-flow artifacts', () => {
  it('keeps the same latest-artifacts-list testid and full card chrome in the reading flow', async () => {
    mockIssue(baseIssue({
      workflowStage: 'check',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockArtifacts([makePlanArtifact(), makeReviewArtifact(), makeCheckLogArtifact()])

    const { container } = renderPage()

    const fullList = await waitFor(() => screen.getByTestId('latest-artifacts-list'))
    const readingFlow = screen.getByTestId('reading-flow')

    expect(readingFlow.contains(fullList)).toBe(true)

    const cardWrapper = fullList.parentElement
    expect(cardWrapper?.className).toContain('rounded-lg')
    expect(cardWrapper?.className).toContain('border')
    expect(cardWrapper?.className).toContain('bg-card')
    expect(cardWrapper?.querySelector('h3')?.textContent).toContain('Latest Artifacts')

    expect(within(fullList).getAllByTestId('latest-artifact-item')).toHaveLength(3)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()

    expect(container.querySelector('[data-testid="runtime-evidence-list"]')).toBeNull()
  })
})
