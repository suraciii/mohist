import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import {
  mockAgentStatus,
  mockIssue,
  mockWorkflowRunSessions,
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
      <MemoryRouter initialEntries={['/issues/401']}>
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
    number: 401,
    title: 'Execution signal test issue',
    body: '',
    status: 'in_progress',
    workflowStage: 'build',
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

function makeActiveSession(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'sess-1',
    workflowRunId: 'wr-1',
    sessionName: 'review-repair',
    runtimeSessionId: 'runtime-1',
    projectId: 'proj-1',
    issueNumber: 401,
    runnerId: 'runner-1',
    status: 'active',
    stage: 'check',
    model: 'minimax/MiniMax-M3',
    workDir: null,
    processPid: null,
    createdAt: '2026-01-01T00:00:00.000Z',
    startedAt: '2026-01-01T00:00:01.000Z',
    completedAt: null,
    lastDataAt: '2026-01-01T00:05:00.000Z',
    failureReason: null,
    exitCode: null,
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

describe('Execution signal: active session', () => {
  it('renders a compact session signal inside the surface when a coder session is active', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Run review' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockWorkflowRunSessions([makeActiveSession({ sessionName: 'review-repair' })])

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const signal = await within(surface).findByTestId('runtime-execution-signal')
    const session = within(signal).getByTestId('runtime-execution-signal-session')
    expect(session.dataset.sessionName).toBe('review-repair')

    const link = within(session).getByTestId('runtime-execution-signal-session-link')
    expect(link.getAttribute('href')).toMatch(/\/issues\/401\/workflow\/sessions\/review-repair$/)
    expect(link).toHaveTextContent('review-repair')

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(signal)).toBe(true)
    const readingFlow = screen.getByTestId('reading-flow')
    expect(readingFlow.contains(signal)).toBe(false)
  })

  it('keeps the full WorkflowSessionsPanel in the reading flow unchanged when the compact signal is rendered', async () => {
    mockIssue(baseIssue())
    mockWorkflowRunSessions([
      makeActiveSession({ sessionName: 'review-repair', status: 'active' }),
      makeActiveSession({ id: 'sess-2', sessionName: 'proposal-draft', status: 'completed' }),
    ])

    const { container } = renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const signal = await within(surface).findByTestId('runtime-execution-signal')

    const sessionsPanel = await waitFor(() =>
      screen.getByTestId('workflow-sessions-panel'),
    )
    const readingFlow = screen.getByTestId('reading-flow')
    expect(readingFlow.contains(sessionsPanel)).toBe(true)
    expect(readingFlow.contains(signal)).toBe(false)
    expect(container.contains(sessionsPanel)).toBe(true)
  })

  it('omits the compact signal when no session is active and the runner is not gating the decision (normal running)', async () => {
    mockIssue(baseIssue({
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Run review' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: true,
      capacity: { active: 0, max: 4 },
    })
    mockWorkflowRunSessions([
      makeActiveSession({ sessionName: 'proposal-draft', status: 'completed' }),
    ])

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-execution-signal')).toBeNull()
  })
})

describe('Execution signal: runner gating', () => {
  it('surfaces the runner-unavailable reason inside the surface when no runner is connected', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: null,
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: false,
      runnerMessage: 'No runner is connected. Start a runner before this issue can run.',
      capacity: { active: 0, max: 4 },
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('queued')

    const waitReason = await within(surface).findByTestId('runtime-wait-reason')
    expect(waitReason).toHaveTextContent('No runner is connected.')

    const signal = within(surface).getByTestId('runtime-execution-signal')
    const runner = within(signal).getByTestId('runtime-execution-signal-runner')
    expect(runner.dataset.gatingKind).toBe('runner-unavailable')
    expect(runner).toHaveTextContent('No runner is connected.')
    const waitReasonText = waitReason.textContent?.replace(/^Waiting on:\s*/, '')
    expect(waitReasonText).toBeTruthy()
    expect(runner.textContent).toContain(waitReasonText!)
  })

  it('surfaces the capacity-full reason inside the surface when runner capacity is full and the issue cannot start', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: null,
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: true,
      capacity: { active: 2, max: 2 },
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('queued')

    const waitReason = await within(surface).findByTestId('runtime-wait-reason')
    expect(waitReason).toHaveTextContent('Runner capacity is full (2/2).')

    const signal = within(surface).getByTestId('runtime-execution-signal')
    const runner = within(signal).getByTestId('runtime-execution-signal-runner')
    expect(runner.dataset.gatingKind).toBe('capacity-full')
    expect(runner).toHaveTextContent('Runner capacity is full (2/2).')
    const waitReasonText = waitReason.textContent?.replace(/^Waiting on:\s*/, '')
    expect(waitReasonText).toBeTruthy()
    expect(runner.textContent).toContain(waitReasonText!)
  })

  it('uses the runnerMessage from the agent status as the runner-unavailable reason', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: null,
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: false,
      runnerMessage: 'Runner has been offline for 12 minutes. Restart it from the runner settings.',
      capacity: { active: 0, max: 4 },
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const runner = await within(surface).findByTestId('runtime-execution-signal-runner')
    expect(runner).toHaveTextContent('Runner has been offline for 12 minutes.')
  })

  it('omits the runner-gating signal when the runner does not gate a current decision (backlog, no waitReason)', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      health: 'done',
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: false,
      capacity: { active: 0, max: 4 },
    })

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-execution-signal')).toBeNull()
  })

  it('omits the runner-gating signal when the waitReason is a non-runner blocker (e.g. waiting-for)', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: {
        kind: 'waiting-for',
        issue: { number: 7, title: 'Blocker issue', health: 'active', status: 'in_progress' },
      },
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: true,
      capacity: { active: 0, max: 4 },
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('queued')
    expect(within(surface).getByTestId('runtime-wait-reason')).toHaveTextContent('Waiting for #7')

    expect(screen.queryByTestId('runtime-execution-signal-runner')).toBeNull()
  })
})

describe('Execution signal: capacity gating consistency', () => {
  it('disables Start when capacity is full and matches the runtime-wait-reason text', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
    }))
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 2, max: 2 },
      runnerAvailable: true,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const waitReason = await within(surface).findByTestId('runtime-wait-reason')
    expect(waitReason).toHaveTextContent('Runner capacity is full (2/2).')

    const runner = within(surface).getByTestId('runtime-execution-signal-runner')
    const waitReasonText = waitReason.textContent?.replace(/^Waiting on:\s*/, '')
    expect(waitReasonText).toBeTruthy()
    expect(runner.textContent).toContain(waitReasonText!)

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).toBeDisabled()
    expect(startButton.getAttribute('title')).toMatch(/capacity is full/i)
  })

  it('enables Start when capacity is available and shows no execution signal', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
    }))
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 0, max: 2 },
      runnerAvailable: true,
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
    expect(screen.queryByTestId('runtime-execution-signal')).toBeNull()
  })
})

describe('Execution signal: terminal and running omissions', () => {
  it('does not render the execution signal during a normal running decision', async () => {
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
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: true,
      capacity: { active: 0, max: 4 },
    })
    mockWorkflowRunSessions([
      makeActiveSession({ sessionName: 'proposal-draft', status: 'completed' }),
    ])

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-execution-signal')).toBeNull()
  })

  it('does not render the execution signal during a done decision', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      health: 'done',
    }))
    mockAgentStatus({
      running: false,
      activeAgents: [],
      runnerAvailable: true,
      capacity: { active: 0, max: 4 },
    })

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-execution-signal')).toBeNull()
  })
})
