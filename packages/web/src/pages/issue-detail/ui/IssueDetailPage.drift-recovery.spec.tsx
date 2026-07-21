import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import { IssueDetailPage } from './IssueDetailPage'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'

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

describe('IssueDecisionSurface — no rebase action in the action set', () => {
  it('does not add a decision-action-rebase button to the surface action set', async () => {
    mockIssue(baseIssue({
      drift: {
        drifted: true,
        detectedAt: '2026-01-05T00:00:00Z',
        decision: 'needs-attention',
      },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(within(surface).queryByTestId('decision-action-rebase')).toBeNull()
  })

  it('keeps rebase surfaced through the dedicated BranchBar slot rather than a runtime-action button', async () => {
    mockIssue(baseIssue({
      drift: {
        drifted: true,
        detectedAt: '2026-01-05T00:00:00Z',
        decision: 'needs-attention',
      },
    }))

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    expect(readingFlow.contains(screen.getByTestId('branch-bar'))).toBe(true)
  })
})

describe('IssueDecisionSurface — workflow/lifecycle authorization', () => {
  it('does not enable an action the runtime decision does not authorize', async () => {
    mockIssue(baseIssue({
      status: 'cancelled',
      workflowStatus: 'cancelled',
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
    expect(screen.queryByTestId('issue-decision-surface')).toBeNull()
    for (const kind of ['start', 'stop', 'approve', 'retry', 'resume', 'rerun']) {
      const action = screen.queryByTestId(`decision-action-${kind}`)
      if (action) {
        expect(action).toBeDisabled()
      } else {
        expect(action).toBeNull()
      }
    }
  })

  it('never adds a lifecycle action that the runtime decision and existing lifecycle predicates do not authorize', async () => {
    mockIssue(baseIssue({
      status: 'done',
      archivedAt: '2026-06-25T10:00:00Z',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: [],
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    expect(screen.queryByTestId('issue-decision-surface')).toBeNull()
    for (const kind of ['mark-ready', 'close', 'mark-as-done', 'start', 'stop']) {
      expect(screen.queryByTestId(`decision-action-${kind}`)).toBeNull()
    }
  })

  it('preserves close/archive/mark-ready lifecycle actions only on a non-terminal non-archived active leaf issue', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStatus: 'stopped',
      health: 'active',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'stopped',
        workflowSummaryState: 'stopped',
        allowedActions: [],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(within(surface).getByTestId('decision-action-close')).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-action-mark-as-done')).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-action-ask-agent')).toBeInTheDocument()
  })
})