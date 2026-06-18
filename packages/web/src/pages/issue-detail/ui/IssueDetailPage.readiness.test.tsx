// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'

const mockUseNavigate = vi.fn()

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockUseNavigate,
  }
})

const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseWorkflowTimeline = vi.fn()
const mockUseWorkflowYaml = vi.fn()
const mockUseAgentStatus = vi.fn()
const mockUseIssue = vi.fn()
const mockStartIssue = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: (...args: unknown[]) => mockUseIssue(...args),
    useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
    useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
    useWorkflowTimeline: (...args: unknown[]) => mockUseWorkflowTimeline(...args),
    useWorkflowYaml: (...args: unknown[]) => mockUseWorkflowYaml(...args),
    startIssue: (...args: unknown[]) => mockStartIssue(...args),
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: (...args: unknown[]) => mockUseAgentStatus(...args),
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
    labels: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

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

describe('IssueDetailPage - draft indicator and Start control', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockStartIssue.mockResolvedValue({})
  })

  afterEach(() => {
    cleanup()
  })

  it('renders a Draft pill on the Issue Detail header for a draft issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getAllByTestId('draft-pill').length).toBeGreaterThan(0))
    expect(screen.getAllByTestId('draft-pill')[0]).toHaveTextContent('Draft')
  })

  it('does not render a Draft pill when isDraft is false', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: false, canStart: true, blocker: null }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('start-button')).toBeInTheDocument())
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
  })

  it('disables the Start control with a "still a draft" reason for a draft issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('start-readiness')).toBeInTheDocument())
    const readiness = screen.getByTestId('start-readiness')
    expect(readiness).toHaveAttribute('data-blocker', 'draft')
    const startButton = screen.getByTestId('start-button')
    expect(startButton).toBeDisabled()
    expect(readiness.textContent).toMatch(/still a draft/i)
  })

  it('disables the Start control with a "waiting for #N" reason for a WaitingFor issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        isDraft: false,
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('start-readiness')).toBeInTheDocument())
    const readiness = screen.getByTestId('start-readiness')
    expect(readiness).toHaveAttribute('data-blocker', 'waiting-for')
    expect(readiness).toHaveAttribute('data-waiting-for', '200')
    const startButton = screen.getByTestId('start-button')
    expect(startButton).toBeDisabled()
    expect(readiness.textContent).toMatch(/waiting for #200/i)
  })

  it('enables the Start control for a ready, unblocked backlog issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: false, canStart: true, blocker: null }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('start-button')).toBeInTheDocument())
    const startButton = screen.getByTestId('start-button')
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('surfaces the actionable server rejection when start is attempted', async () => {
    mockStartIssue.mockRejectedValue(new Error('Issue #201 is still a draft; mark it ready before starting.'))
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('start-button')).toBeInTheDocument())
    expect(screen.getByTestId('start-button')).toBeDisabled()
  })
})

describe('IssueDetailPage - Readiness panel from isDraft/canStart/blocker', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the Readiness panel for a backlog draft', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('Yes')
    expect(screen.getByTestId('readiness-can-start')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-blocker')).toHaveTextContent('Still a draft')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'draft')
  })

  it('renders the Readiness panel for a backlog waiting issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        isDraft: false,
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-can-start')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-blocker')).toHaveTextContent('Waiting for #200')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'waiting-for')
  })

  it('renders the Readiness panel for a backlog ready issue with no blocker', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: false, canStart: true, blocker: null }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('No')
    expect(screen.getByTestId('readiness-can-start')).toHaveTextContent('Yes')
    expect(screen.getByTestId('readiness-blocker')).toHaveTextContent('None')
    expect(screen.getByTestId('readiness-blocker')).toHaveAttribute('data-blocker-kind', 'none')
  })
})

describe('IssueDetailPage - no legacy startEligibility fields rendered', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  })

  afterEach(() => {
    cleanup()
  })

  it('does not render startEligibility or waitingForDelivery anywhere on the page for a draft issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getAllByTestId('draft-pill').length).toBeGreaterThan(0))
    const html = container.innerHTML
    expect(html).not.toMatch(/startEligibility/i)
    expect(html).not.toMatch(/waitingForDelivery/i)
  })

  it('does not render startEligibility or waitingForDelivery for a waiting issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        isDraft: false,
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
      }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('start-readiness')).toBeInTheDocument())
    const html = container.innerHTML
    expect(html).not.toMatch(/startEligibility/i)
    expect(html).not.toMatch(/waitingForDelivery/i)
  })

  it('does not parse the issue body to determine readiness state', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        isDraft: false,
        canStart: true,
        blocker: null,
        body: 'TODO: still a draft\nPlease mark ready before starting',
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('readiness-panel')).toBeInTheDocument())
    expect(screen.getByTestId('readiness-is-draft')).toHaveTextContent('No')
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
  })
})
