// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'

const { mockStartIssue, mockUpdateIssue } = vi.hoisted(() => ({
  mockStartIssue: vi.fn(() => Promise.resolve({})),
  mockUpdateIssue: vi.fn(() => Promise.resolve({})),
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    startIssue: mockStartIssue,
    updateIssue: mockUpdateIssue,
  }
})

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
    id: 'issue-1',
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

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/201']}>
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
  mockUpdateIssue.mockClear()
  mockStartIssue.mockClear()
})

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

    await waitFor(() => expect(screen.getByTestId('runtime-action-start')).toBeInTheDocument())
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
  })

  it('renders a Mark ready control for a draft issue', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('start-readiness')).toBeInTheDocument())
    const readiness = screen.getByTestId('start-readiness')
    expect(readiness).toHaveAttribute('data-blocker', 'draft')
    const markReadyButton = screen.getByTestId('mark-ready-button')
    expect(markReadyButton).not.toBeDisabled()
    expect(readiness.textContent).toMatch(/still a draft/i)
  })

  it('disables the Start control with a "waiting for #N" reason for a WaitingFor issue', async () => {
    mockIssue(makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-action-start')).toBeInTheDocument())
    const readiness = screen.getByTestId('readiness-panel')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'waiting-for')
    const startButton = screen.getByTestId('runtime-action-start')
    expect(startButton).toBeDisabled()
    expect(startButton.getAttribute('title')).toMatch(/waiting for #200/i)
    expect(readiness.textContent).toMatch(/waiting for #200/i)
  })

  it('enables the Start control for a ready, unblocked backlog issue', async () => {
    mockIssue(makeIssue({ isDraft: false, canStart: true, blocker: null }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-action-start')).toBeInTheDocument())
    const startButton = screen.getByTestId('runtime-action-start')
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('calls updateIssue to mark a draft issue ready when Mark ready is clicked', async () => {
    mockIssue(makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('mark-ready-button')).toBeInTheDocument())
    screen.getByTestId('mark-ready-button').click()
    await waitFor(() => expect(mockUpdateIssue).toHaveBeenCalledWith(201, { isDraft: false }, 'proj-1'))
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
