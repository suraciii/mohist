import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import type { Issue } from '../../../entities/issue'
import { IssueDetailPage } from './IssueDetailPage'
import { getCurrentIssueFixture, mockIssue, mountIssueDetail } from './_issueDetailMsw'

const updateIssue = vi.fn(async () => makeIssue({ isDraft: false }) as Issue)

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
    number: 201,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
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

mountIssueDetail({ issue: makeIssue() })

beforeEach(() => updateIssue.mockClear())

afterEach(() => {
  cleanup()
})

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const issue = getCurrentIssueFixture()
  if (issue) {
    queryClient.setQueryDefaults(['issues', 201, 'proj-1'], { staleTime: Infinity })
    queryClient.setQueryData(['issues', 201, 'proj-1'], issue)
  }
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/201']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route
              path="/issues/:number"
              element={<IssueDetailPage mutationDependencies={{ updateIssue }} />}
            />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('IssueDetailPage - draft indicator and Start control', () => {
  it('renders a Draft pill on the Issue Detail header for a draft issue', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    renderPage()

    await waitFor(() => expect(screen.getAllByTestId('draft-pill').length).toBeGreaterThan(0))
    expect(screen.getAllByTestId('draft-pill')[0]).toHaveTextContent('Draft')
  })

  it('does not render a Draft pill when isDraft is false', async () => {
    mockIssue(makeIssue({ isDraft: false, canStart: true, blocker: null }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('decision-action-start')).toBeInTheDocument())
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
  })

  it('renders a Mark ready control for a draft issue', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-decision-surface')).toBeInTheDocument())
    const markReadyButton = await screen.findByTestId('decision-action-mark-ready')
    expect(markReadyButton).not.toBeDisabled()
    expect(markReadyButton).toHaveTextContent(/Mark ready/i)
  })

  it('disables the Start control with a "waiting for #N" reason for a WaitingFor issue', async () => {
    mockIssue(makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('decision-action-start')).toBeInTheDocument())
    const startButton = screen.getByTestId('decision-action-start')
    expect(startButton).toBeDisabled()
    const reason = await screen.findByTestId('decision-action-start-reason')
    expect(reason.textContent ?? '').toMatch(/waiting for #200/i)
  })

  it('enables the Start control for a ready, unblocked backlog issue', async () => {
    mockIssue(makeIssue({ isDraft: false, canStart: true, blocker: null }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('decision-action-start')).toBeInTheDocument())
    const startButton = screen.getByTestId('decision-action-start')
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('calls updateIssue to mark a draft issue ready when Mark ready is clicked', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    renderPage()

    const markReadyButton = await waitFor(() => screen.getByTestId('decision-action-mark-ready'))
    markReadyButton.click()
    await waitFor(() => expect(updateIssue).toHaveBeenCalledTimes(1))
    expect(updateIssue).toHaveBeenCalledWith(201, { isDraft: false }, 'proj-1')
  })
})

describe('IssueDetailPage - Readiness panel from isDraft/canStart/blocker', () => {
  it('renders the Readiness panel for a backlog draft', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('Yes')
    expect(screen.getByTestId('readiness-can-start')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-blocker')).toHaveTextContent('Still a draft')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'draft')
  })

  it('renders the Readiness panel for a backlog waiting issue', async () => {
    mockIssue(makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-can-start')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-blocker')).toHaveTextContent('Waiting for #200')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'waiting-for')
  })

  it('renders the Readiness panel for a backlog ready issue with no blocker', async () => {
    mockIssue(makeIssue({ isDraft: false, canStart: true, blocker: null }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-can-start')).toHaveTextContent('Yes')
    expect(screen.getByTestId('readiness-blocker')).toHaveTextContent('None')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'none')
  })
})

describe('IssueDetailPage - no legacy startEligibility fields rendered', () => {
  it('does not render startEligibility or waitingForDelivery anywhere on the page for a draft issue', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getAllByTestId('draft-pill').length).toBeGreaterThan(0))
    const html = container.innerHTML
    expect(html).not.toMatch(/startEligibility/i)
    expect(html).not.toMatch(/waitingForDelivery/i)
  })

  it('does not render startEligibility or waitingForDelivery for a waiting issue', async () => {
    mockIssue(makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    const html = container.innerHTML
    expect(html).not.toMatch(/startEligibility/i)
    expect(html).not.toMatch(/waitingForDelivery/i)
  })

  it('does not parse the issue body to determine readiness state', async () => {
    mockIssue(makeIssue({
      isDraft: false,
      canStart: true,
      blocker: null,
      body: 'TODO: still a draft\nPlease mark ready before starting',
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('No')
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
  })
})
