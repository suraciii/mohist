import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockAgentStatus, mockIssue, mountIssueDetail } from './_issueDetailMsw'
import { setScopedValue } from '../../../../tests/support/scoped-property'


const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    repositories: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  },
]

function mockMatchMedia(narrow: boolean, width = narrow ? 375 : 1280) {
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
  setScopedValue(window, 'innerWidth', width)
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
    title: 'Test issue',
    body: '',
    status: 'backlog',
    workflowStage: null,
    workflowStatus: null,
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

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage narrow-viewport decision surface reachability', () => {
  beforeEach(() => {
    mockMatchMedia(true)
  })

  it('running surfaces the decision sheet launcher with the primary label and opens the full action list', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())

    const launcher = await waitFor(() => screen.getByTestId('mobile-action-sheet-launcher'))
    expect(launcher).toHaveTextContent(/Stop/i)
    expect(launcher).toHaveAttribute('data-action-kind', 'stop')

    fireEvent.click(launcher)
    const sheet = screen.getByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-sheet-action-stop')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-sheet-action-ask-agent')).toBeInTheDocument()
  })

  it('approval-required surfaces both approve and send-back in the mobile sheet', async () => {
    mockIssue(baseIssue({
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

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const sheet = screen.getByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-sheet-action-approve')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-sheet-action-send-back')).toBeInTheDocument()
  })

  it('failed state exposes retry, resume, rerun, and ask agent in the mobile sheet', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'failed',
      health: 'active',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'failed',
        workflowSummaryState: 'failed',
        allowedActions: ['retry', 'resume', 'rerun', 'start', 'stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const sheet = screen.getByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-sheet-action-retry')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-sheet-action-resume')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-sheet-action-rerun')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-sheet-action-ask-agent')).toBeInTheDocument()
  })

  it('queued backlog (ready to start) exposes Start in the mobile sheet', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      canStart: true,
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const sheet = screen.getByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-sheet-action-start')).toBeInTheDocument()
  })

  it('draft backlog surfaces disabled Start with the draft blocker reason and keeps the launcher enabled', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      isDraft: true,
      canStart: true,
      blocker: { kind: 'draft' },
    }))

    renderPage()

    const launcher = await waitFor(() => screen.getByTestId('mobile-action-sheet-launcher'))
    expect(launcher).not.toBeDisabled()

    fireEvent.click(launcher)
    const start = await screen.getByTestId('mobile-sheet-action-start')
    expect(start).toBeDisabled()
    expect(start).toHaveTextContent('Start')
    const reason = await screen.getByTestId('mobile-sheet-action-start-reason')
    expect(reason.textContent ?? '').toMatch(/draft|mark it ready|mark the issue ready/i)
  })

  it('prerequisite-blocked backlog exposes a visible Start reason with the prerequisite name', async () => {
    mockIssue(baseIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      canStart: true,
      blocker: {
        kind: 'waiting-for',
        issue: { number: 9, title: 'Prepare spec' },
      },
    }))

    renderPage()

    fireEvent.click(await screen.findByTestId('mobile-action-sheet-launcher'))
    const start = await screen.findByTestId('mobile-sheet-action-start')
    expect(start).toBeDisabled()
    const reason = await screen.findByTestId('mobile-sheet-action-start-reason')
    expect(reason.textContent ?? '').toMatch(/Waiting for #9 Prepare spec/)
  })

  it('runner-unavailable backlog shows the runner gating reason in Start', async () => {
    mockAgentStatus({
      activeAgents: [],
      capacity: { max: 1 },
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

    fireEvent.click(await screen.findByTestId('mobile-action-sheet-launcher'))
    const start = await screen.findByTestId('mobile-sheet-action-start')
    expect(start).toBeDisabled()
    const reason = await screen.findByTestId('mobile-sheet-action-start-reason')
    expect(reason.textContent ?? '').toMatch(/runner is not available|no runner is connected/i)
  })

  it('done state keeps the no-action decision context reachable on mobile', async () => {
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
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    expect(screen.getByTestId('mobile-action-sheet-rationale')).toHaveTextContent(/completed/i)
    expect(screen.getByTestId('mobile-action-sheet-next-action')).toHaveTextContent(/no further action required/i)
    expect(screen.getByTestId('mobile-sheet-no-action')).toBeInTheDocument()
  })

  it('archived state keeps the no-action decision context reachable on mobile', async () => {
    mockIssue(baseIssue({
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'done',
      health: 'done',
      archivedAt: '2026-01-02T00:00:00Z',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: [],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    expect(screen.getByTestId('mobile-action-sheet-rationale')).toHaveTextContent(/completed/i)
    expect(screen.getByTestId('mobile-sheet-no-action')).toBeInTheDocument()
  })

  it('narrow viewport reserves bottom padding only when a decision surface exists', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('true')
    expect(column.className).toContain('pb-[calc(8rem')
  })

  it('stop on narrow opens the bottom-sliding sheet with confirmation and the sticky StatusHeadline remains visible', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'force-stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    fireEvent.click(await screen.findByTestId('mobile-sheet-action-stop'))

    const sheet = await screen.findByTestId('mobile-action-sheet')
    expect(sheet).toHaveAttribute('role', 'dialog')
    expect(sheet).toHaveAttribute('aria-modal', 'true')
    expect(sheet.className).toMatch(/bottom-0/)
    expect(screen.getByTestId('mobile-stop-confirmation')).toBeInTheDocument()
    expect(screen.getByTestId('mobile-stop-confirmation-body')).toHaveTextContent(/preserve progress/)

    const headline = screen.getByTestId('status-headline')
    expect(headline).toHaveAttribute('data-sticky', 'true')
    expect(headline.className).toMatch(/\bz-20\b/)
    expect(sheet.className).toMatch(/\bz-50\b/)

    fireEvent.keyDown(document, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByTestId('mobile-action-sheet')).toBeNull())
  })
})

describe('IssueDetailPage narrow-viewport 768-1024px band (flush-bottom bar)', () => {
  it('at ~900px (narrow page, no global nav) the bar anchors flush to the bottom with no nav-offset', async () => {
    mockMatchMedia(true, 900)
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.className).toMatch(/\bbottom-\[calc\(/)

    expect(bar.className).toMatch(/\bmd:bottom-0\b/)
    expect(bar.className).not.toMatch(/md:bottom-\[calc\(/)

    const narrowMatch = /\bbottom-\[calc\(([\s\S]+?)\)\]/.exec(bar.className)
    expect(narrowMatch).toBeTruthy()
    expect(narrowMatch?.[1]).toMatch(/3\.5rem/)
    expect(narrowMatch?.[1]).toMatch(/env\(safe-area-inset-bottom\)/)

    expect(bar.className).toMatch(/\bfixed\b/)
    expect(bar.className).toMatch(/\binset-x-0\b/)
  })

  it('does not reserve padding for the global nav on the content column when the bar is present', async () => {
    mockMatchMedia(true, 900)
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mobile-action-bar')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('true')
    expect(column.className).toContain('pb-[calc(8rem')
  })
})

describe('IssueDetailPage desktop viewport restores IssueDecisionSurface and no mobile-only elements', () => {
  beforeEach(() => {
    mockMatchMedia(false)
  })

  it('desktop renders IssueDecisionSurface in the header tier and neither MobileActionBar nor ConfirmationDrawer in the DOM', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(surface).toBeInTheDocument()
    expect(screen.getByTestId('decision-action-stop')).toBeInTheDocument()

    expect(screen.queryByTestId('mobile-action-bar')).toBeNull()
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()

    fireEvent.click(screen.getByTestId('decision-action-stop'))

    await waitFor(() => expect(screen.getByTestId('decision-stop-confirmation')).toBeTruthy())
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('desktop does not reserve extra bottom padding for the bar', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-decision-surface')).toBeTruthy())

    const column = screen.getByTestId('issue-detail-content-column')
    expect(column.dataset.barReserved).toBe('false')
    expect(column.className).not.toContain('pb-[calc(8rem')
  })
})
