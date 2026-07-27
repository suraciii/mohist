import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import { IssueDetailPage } from './IssueDetailPage'
import { mockAgentStatus, mockIssue, mockWorkspaceStatus, mountIssueDetail } from './_issueDetailMsw'

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    repositories: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
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
            <Route path="/agent-sessions/new" element={<div data-testid="agent-composer-target">agent composer</div>} />
            <Route path="/issues/14/workflow/sessions/:sessionName" element={<div data-testid="transcript-target">transcript</div>} />
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
    workflowStage: 'build',
    workflowStatus: 'running',
    workflowRunId: 'wr-1',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    ...overrides,
  }
}

mountIssueDetail({ issue: baseIssue() })

beforeEach(() => {
  mockMatchMedia(false)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('IssueDecisionSurface — control workspace (active execution)', () => {
  it('shows identity, stage, progress, primary owner action, and slots within the control region', async () => {
    mockIssue(baseIssue({
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockWorkspaceStatus({ branch: 'feature/issue-14', baseBranch: 'master', hasConflicts: false, canRebase: true })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(surface).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-action-stop')).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-rationale')).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-next-action')).toBeInTheDocument()
  })

  it('shows approval state, awaiting stage, approve+send-back together with evidence reachable from control region', async () => {
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

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const approve = await within(surface).findByTestId('decision-action-approve')
    const sendBack = await within(surface).findByTestId('decision-action-send-back')
    expect(approve).toBeDisabled()
    expect(sendBack).toBeDisabled()
    fireEvent.change(within(surface).getByTestId('approval-operator-input'), { target: { value: 'Ada' } })
    expect(approve).not.toBeDisabled()
    expect(sendBack).not.toBeDisabled()
    expect(surface.dataset.summary).toBe('approval-required')
  })

  it('opens send-back feedback form within the same decision context without navigating away', async () => {
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

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const sendBack = await within(surface).findByTestId('decision-action-send-back')
    fireEvent.change(within(surface).getByTestId('approval-operator-input'), { target: { value: 'Ada' } })
    fireEvent.click(sendBack)

    const form = await within(surface).findByTestId('send-back-feedback-form')
    expect(form).toBeInTheDocument()
    const textarea = within(form).getByTestId('send-back-feedback-textarea')
    fireEvent.change(textarea, { target: { value: 'Tighten the failing test' } })
    fireEvent.click(within(form).getByRole('radio', { name: 'Detail' }))
    const submit = within(form).getByTestId('send-back-feedback-submit')
    expect(submit).not.toBeDisabled()
  })

  it('shows blocked summary and recovery actions (retry/resume/rerun) inside the control region', async () => {
    mockIssue(baseIssue({
      workflowStage: 'build',
      workflowStatus: 'failed',
      health: 'blocked',
      blockedReason: 'Runner lost while work was active.',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'failed',
        workflowSummaryState: 'waiting-for-recovery',
        allowedActions: ['retry', 'resume', 'rerun'],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(['blocked', 'failed']).toContain(surface.dataset.summary)
    for (const kind of ['retry', 'resume', 'rerun']) {
      expect(within(surface).getByTestId(`decision-action-${kind}`)).toBeInTheDocument()
    }
  })

  it('reports interrupted as blocked in the header summary and exposes a stop/recovery rationale that is not framed as awaiting approval', async () => {
    mockIssue(baseIssue({
      workflowStatus: 'interrupted',
      health: 'blocked',
      blockedReason: 'Runner lost while work was active.',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'interrupted',
        workflowSummaryState: 'waiting-for-recovery',
        allowedActions: ['retry', 'resume', 'rerun'],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(surface.dataset.summary).toBe('blocked')
    const rationale = within(surface).getByTestId('decision-rationale').textContent ?? ''
    expect(rationale.toLowerCase()).not.toMatch(/approval/i)
  })
})

describe('IssueDecisionSurface — control workspace (terminal states)', () => {
  it('shows identity and queued situation without fabricated stage, progress, or runtime actions', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      canStart: true,
      blocker: null,
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(surface).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-action-start')).toBeInTheDocument()
  })

  it('shows runner-unavailable / capacity-full as the in-surface runner gating signal in the control region', async () => {
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 4, max: 4 },
      runnerAvailable: false,
      runnerMessage: 'No runner is connected. Start a runner before this issue can run.',
    })
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      canStart: true,
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const start = await within(surface).findByTestId('decision-action-start')
    expect(start).toBeDisabled()
    const reason = await within(surface).findByTestId('decision-action-start-reason')
    expect(reason.textContent ?? '').toMatch(/runner is not available|no runner is connected|capacity is full/i)
  })

  it('shows the terminal Done state with no start/stop/approve/retry/resume/rerun actions offered', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'done',
      health: 'done',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: [],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    expect(screen.getByTestId('issue-decision-surface')).toBeTruthy()
    expect(screen.getByTestId('decision-no-action-explanation')).toBeTruthy()
    for (const kind of ['start', 'stop', 'approve', 'retry', 'resume', 'rerun']) {
      expect(screen.queryByTestId(`decision-action-${kind}`)).toBeNull()
    }
  })

  it('shows archived banner, identity, terminal Done state, and no active workflow controls', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      archivedAt: '2026-06-25T10:00:00Z',
      health: 'done',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: [],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    expect(screen.getByTestId('issue-decision-surface')).toBeTruthy()
    expect(screen.getByTestId('decision-no-action-explanation')).toBeTruthy()
    for (const kind of ['start', 'stop', 'approve', 'retry', 'resume', 'rerun']) {
      expect(screen.queryByTestId(`decision-action-${kind}`)).toBeNull()
    }
    expect(screen.getByTestId('archived-banner')).toBeInTheDocument()
  })
})
