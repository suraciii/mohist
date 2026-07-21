import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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

function compositeParent(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Composite parent',
    body: '',
    status: 'in_progress',
    workflowStage: 'build',
    workflowStatus: 'running',
    workflowRunId: null,
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
    children: [
      { number: 12, title: 'child A', status: 'in_progress', health: 'active', repositoryName: null },
      { number: 13, title: 'child B', status: 'in_progress', health: 'active', repositoryName: null },
    ],
    childIssuesSummary: {
      hasChildren: true,
      count: 2,
      backlogCount: 0,
      inProgressCount: 2,
      doneCount: 0,
      cancelledCount: 0,
      blockedCount: 0,
    },
    ...overrides,
  }
}

mountIssueDetail({ issue: compositeParent() })

beforeEach(() => {
  mockMatchMedia(false)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('IssueDecisionSurface — composite parent (no workflow decision)', () => {
  it('renders the issue decision surface with applicable lifecycle actions on desktop', async () => {
    mockIssue(compositeParent())

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(within(surface).getByTestId('decision-action-close')).toBeInTheDocument()
    expect(within(surface).getByTestId('decision-action-ask-agent')).toBeInTheDocument()
    expect(within(surface).queryByTestId('decision-action-start')).toBeNull()
    expect(within(surface).queryByTestId('decision-action-stop')).toBeNull()
    expect(within(surface).queryByTestId('decision-action-approve')).toBeNull()
    expect(within(surface).queryByTestId('decision-action-send-back')).toBeNull()
  })

  it('does not offer a transcript action when the composite parent has no session', async () => {
    mockIssue(compositeParent({ workflowRunId: null }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    expect(within(surface).queryByTestId('decision-action-view-transcript')).toBeNull()
  })

  it('omits close for a cancelled or archived composite parent', async () => {
    mockIssue(compositeParent({ status: 'cancelled', health: 'cancelled' }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('status-headline')).toBeTruthy())
    expect(screen.queryByTestId('issue-decision-surface')).toBeNull()
  })

  it('exposes the same decision surface actions on mobile via the sheet', async () => {
    mockMatchMedia(true)
    mockIssue(compositeParent())

    renderPage()

    const launcher = await waitFor(() => screen.getByTestId('mobile-action-sheet-launcher'))
    fireEvent.click(launcher)
    const sheet = await screen.findByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-sheet-action-close')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-sheet-action-ask-agent')).toBeInTheDocument()
    expect(within(sheet).queryByTestId('mobile-sheet-action-start')).toBeNull()
  })
})