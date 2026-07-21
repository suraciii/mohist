import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'


const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

mountIssueDetail({ issue: makeIssue() })

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

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage status-header — three-tier anchors', () => {
  it('renders the three tier containers with stable anchors', async () => {
    renderPage()

    const headerTier = await waitFor(() => screen.getByTestId('status-header-tier'))
    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    expect(headerTier.contains(screen.getByTestId('status-headline'))).toBe(true)
    expect(headerTier.contains(screen.getByTestId('issue-decision-surface'))).toBe(true)

    expect(referenceRail.className).toContain('space-y-6')
    expect(readingFlow.className).toContain('space-y-8')
  })
})

describe('IssueDetailPage status-header — stickiness of StatusHeadline', () => {
  it('pins the StatusHeadline as the only sticky element at the top of the scroll container', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'inspect'],
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.sticky).toBe('true')
    expect(headline.className).toContain('sticky')
    expect(headline.className).toContain('top-0')

    const scrollContainer = screen.getByTestId('issue-detail-page-container')
    const maxWidthShell = scrollContainer.firstElementChild
    expect(maxWidthShell).toBeTruthy()
    const headerTier = screen.getByTestId('status-header-tier')
    expect(maxWidthShell?.firstElementChild).toBe(headerTier)
    expect(headerTier.firstElementChild).toBe(headline)

    const backButton = screen.getByTestId('back-to-board')
    const headlinePosition = headline.compareDocumentPosition(backButton)
    expect(headlinePosition & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)

    const stickyElements = Array.from(scrollContainer.querySelectorAll('[data-sticky="true"]'))
    expect(stickyElements).toHaveLength(1)
    expect(stickyElements[0]).toBe(headline)
  })

  it('does not pin the runtime decision surface (only the headline is sticky)', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'inspect'],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(surface.dataset.sticky ?? 'false').toBe('false')
    expect(surface.className).not.toContain('sticky')
  })
})

describe('IssueDetailPage status-header — single glanceable region', () => {
  it('aggregates situation, stage, and progress in one region with the current task embedded in the headline text', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowStageProgress: {
        stage: 'build',
        total: 5,
        completed: 2,
        running: 1,
        failed: 0,
        currentTaskTitle: 'Build decision surface',
      },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.contains(screen.getByTestId('status-headline-summary'))).toBe(true)
    expect(headline.contains(screen.getByTestId('status-headline-stage-progress'))).toBe(true)
    expect(headline.dataset.hasCurrentTask).toBe('true')
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()

    const stage = screen.getByTestId('status-headline-stage-progress')
    expect(stage.dataset.stage).toBe('build')
    expect(stage.textContent).toMatch(/2\s*\/\s*5/)

    const text = headline.textContent ?? ''
    expect(text).toContain('Build decision surface')
  })

  it('shows the situation alone without fabricating a stage, progress, or current task when none exist (backlog)', async () => {
    mockIssue(makeIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: { kind: 'draft' },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.contains(screen.getByTestId('status-headline-summary'))).toBe(true)
    expect(screen.queryByTestId('status-headline-stage-progress')).toBeNull()
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()
    expect(headline.dataset.hasCurrentTask).toBe('false')
  })

  it('reflects the done situation with no active workflow controls for an archived done issue', async () => {
    mockIssue(makeIssue({
      number: 264,
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      archivedAt: '2026-06-25T10:00:00Z',
      health: 'done',
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('done')
    expect(headline.textContent ?? '').toMatch(/Done/i)

    expect(screen.queryByTestId('issue-decision-surface')).toBeNull()
    expect(screen.queryByTestId('decision-action-start')).toBeNull()
    expect(screen.queryByTestId('decision-action-stop')).toBeNull()
    expect(screen.queryByTestId('decision-action-approve')).toBeNull()
    expect(screen.queryByTestId('decision-action-retry')).toBeNull()
    expect(screen.queryByTestId('decision-action-resume')).toBeNull()
    expect(screen.queryByTestId('decision-action-rerun')).toBeNull()
  })
})

describe('IssueDetailPage status-header — adjudicated situation variants', () => {
  it('reflects the running situation in the headline when the workflow is running', async () => {
    mockIssue(makeIssue({
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

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('running')
    expect(headline.textContent ?? '').toMatch(/Running/i)
  })

  it('reflects the approval-required situation in the headline when approval is awaiting', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
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

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('approval-required')
    expect(headline.textContent ?? '').toMatch(/Approval required/i)
  })

  it('reflects the done situation for an archived done issue', async () => {
    mockIssue(makeIssue({
      number: 264,
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      archivedAt: '2026-06-25T10:00:00Z',
      health: 'done',
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('done')
  })
})

describe('IssueDetailPage status-header — single-badge invariant', () => {
  it('does not render a separate runtime status pill in the identity row (status is the headline)', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      priority: 'p1',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('status-headline'))
    const page = screen.getByTestId('issue-detail-page-container')
    const allPills = page.querySelectorAll('[data-testid="runtime-status-pill"]')
    expect(allPills).toHaveLength(0)
    expect(screen.queryByTestId('status-badges-runtime')).toBeNull()
  })

  it('does not render a duplicate runtime summary label or icon row inside the runtime decision surface', async () => {
    mockIssue(makeIssue({
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

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(within(surface).queryByTestId('runtime-summary-label')).toBeNull()
    expect(within(surface).queryByTestId('runtime-summary-running')).toBeNull()
    expect(within(surface).queryByTestId('runtime-current-task')).toBeNull()
  })
})

describe('IssueDetailPage status-header — action surface anchoring', () => {
  it('anchors all seven runtime actions inside the status-header tier, not the reading flow or reference rail', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'retry', 'rerun', 'resume'],
      },
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(surface.contains(screen.getByTestId('decision-actions'))).toBe(true)

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(surface)).toBe(true)

    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.contains(surface)).toBe(false)
    expect(referenceRail.contains(surface)).toBe(false)

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'stop', 'start']) {
      const actionIds = Array.from(screen.getByTestId('issue-detail-page-container').querySelectorAll(`[data-testid="decision-action-${kind}"]`))
      expect(actionIds.length).toBeLessThanOrEqual(1)
      for (const node of actionIds) {
        expect(headerTier.contains(node)).toBe(true)
        expect(readingFlow.contains(node)).toBe(false)
        expect(referenceRail.contains(node)).toBe(false)
      }
    }
  })
})

describe('IssueDetailPage status-header — heaviest visual weight', () => {
  it('carries the sticky + fill + icon + border combination uniquely in the headline', async () => {
    mockIssue(makeIssue({
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

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.sticky).toBe('true')
    expect(headline.className).toContain('sticky')
    expect(headline.className).toMatch(/bg-(info|warning|danger|success)-subtle/)
    expect(headline.querySelector('svg')).toBeTruthy()
    expect(headline.className).toMatch(/border-/)

    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.querySelector('[data-sticky="true"]')).toBeNull()
    expect(referenceRail.querySelector('[data-sticky="true"]')).toBeNull()
  })
})
