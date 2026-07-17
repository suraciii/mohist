import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import {
  mockIssue,
  mockWorkspaceStatus,
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
    repository: {
      name: 'master',
      baseBranch: 'master',
      gitUrl: 'https://github.com/suraciii/mohist.git',
    },
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

describe('Drift recovery in the control region', () => {
  it('renders the in-surface drift-recovery block when base drift requires attention', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'interrupted',
      health: 'blocked',
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: null,
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run-wr-14',
      baseBranch: 'master',
      ahead: 0,
      behind: 12,
      rebaseInProgress: false,
      conflictingFiles: [],
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const recovery = await within(surface).findByTestId('runtime-drift-recovery')
    expect(recovery).toBeInTheDocument()
    expect(within(surface).getByTestId('runtime-drift-recovery-action')).toHaveTextContent(/Rebase onto master/i)

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(recovery)).toBe(true)
  })

  it('keeps the reference-rail IssueDriftCard available alongside the in-surface entry', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'interrupted',
      health: 'blocked',
      drift: {
        drifted: true,
        detectedAt: '2026-01-05T00:00:00Z',
        decision: 'needs-attention',
        observedBaseSha: 'abc1234abc',
        currentBaseSha: 'def5678def',
      },
      recovery: null,
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run-wr-14',
      baseBranch: 'master',
      ahead: 0,
      behind: 12,
    })

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-drift-recovery'))
    const railDriftCard = screen.getByTestId('reference-rail-drift')
    expect(railDriftCard).toBeInTheDocument()
    const summary = within(railDriftCard).getByTestId('reference-rail-drift-summary')
    expect(summary.textContent).toBe('needs-attention')

    fireEvent.click(screen.getByTestId('reference-rail-drift-toggle'))
    await waitFor(() => {
      expect(railDriftCard.dataset.collapsed).toBe('false')
    })
    expect(within(railDriftCard).getByText(/Rebase decision/i)).toBeInTheDocument()
    expect(within(railDriftCard).getByText(/abc1234/)).toBeInTheDocument()
    expect(within(railDriftCard).getByText(/def5678/)).toBeInTheDocument()
  })

  it('does not render the in-surface drift-recovery block when the issue is not drifted', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'running',
      health: 'active',
      drift: null,
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-drift-recovery')).toBeNull()
  })

  it('does not render the in-surface drift-recovery block when drift decision is "defer"', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'running',
      health: 'active',
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'defer' },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(screen.queryByTestId('runtime-drift-recovery')).toBeNull()
  })

  it('triggers the same mutation as BranchBar when the in-surface rebase action is clicked', async () => {
    let rebaseCalls = 0
    const { server } = await import('../../../../tests/support/msw')
    const { http, HttpResponse } = await import('msw')
    server.use(
      http.post('*/api/projects/:projectId/issues/:number/rebase', () => {
        rebaseCalls += 1
        return HttpResponse.json({
          success: true,
          data: { status: 'queued', message: 'Rebase task queued', rebased: false },
        })
      }),
    )

    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'interrupted',
      health: 'blocked',
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: null,
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run-wr-14',
      baseBranch: 'master',
      ahead: 0,
      behind: 12,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const recovery = await within(surface).findByTestId('runtime-drift-recovery')

    fireEvent.click(within(recovery).getByTestId('runtime-drift-recovery-action'))

    await waitFor(() => expect(rebaseCalls).toBe(1))
  })

  it('preserves the existing blocked recovery actions alongside the drift-recovery block', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'interrupted',
      health: 'blocked',
      blockedReason: 'A blocking check failed.',
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'blocked',
        allowedActions: ['retry', 'stop'],
      },
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run-wr-14',
      baseBranch: 'master',
      ahead: 0,
      behind: 12,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.dataset.summary).toBe('blocked')
    expect(within(surface).getByTestId('runtime-drift-recovery')).toBeInTheDocument()
    expect(within(surface).getByTestId('runtime-action-retry')).toBeInTheDocument()
    expect(within(surface).getByTestId('runtime-action-stop')).toBeInTheDocument()
  })

  it('does not add a runtime-action-rebase button to the surface action set', async () => {
    mockIssue(baseIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowStatus: 'interrupted',
      health: 'blocked',
      drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
      recovery: null,
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run-wr-14',
      baseBranch: 'master',
      ahead: 0,
      behind: 12,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await within(surface).findByTestId('runtime-drift-recovery')
    expect(within(surface).queryByTestId('runtime-action-rebase')).toBeNull()
  })
})
