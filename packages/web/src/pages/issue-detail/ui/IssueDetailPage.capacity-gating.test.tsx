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
const mockUpdateIssue = vi.fn()

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
    updateIssue: (...args: unknown[]) => mockUpdateIssue(...args),
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
    number: 300,
    title: 'Capacity gating test issue',
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

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/300']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('IssueDetailPage - capacity-full gating uses server capacity.active/capacity.max (not activeAgents.length)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockStartIssue.mockResolvedValue({})
    mockUpdateIssue.mockResolvedValue({})
  })

  afterEach(() => {
    cleanup()
  })

  it('disables Start when capacity.active >= capacity.max regardless of activeAgents.length', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { active: 2, max: 2 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('start-button'))
    expect(startButton).toBeDisabled()
    expect(startButton).toHaveTextContent(/Capacity full/i)
  })

  it('enables Start when capacity.active < capacity.max even if activeAgents is empty', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { active: 0, max: 2 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('start-button'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('does not gate Start on activeAgents.length - Start stays enabled when activeAgents is long but capacity is not full', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [
          { issueId: 'i-1', issueNumber: 101, projectId: 'proj-1' },
          { issueId: 'i-2', issueNumber: 102, projectId: 'proj-1' },
          { issueId: 'i-3', issueNumber: 103, projectId: 'proj-1' },
        ],
        capacity: { active: 1, max: 4 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('start-button'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('gates Start on server capacity even when activeAgents is empty (capacity reflects runner works, not sessions)', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { active: 4, max: 4 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('start-button'))
    expect(startButton).toBeDisabled()
    expect(startButton).toHaveTextContent(/Capacity full/i)
  })

  it('treats capacity.max === 0 as not-full (does not disable Start on a zero-max placeholder)', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [],
        capacity: { active: 0, max: 0 },
        runnerAvailable: true,
      },
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('start-button'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('keeps the other-issues running indicator visible from activeAgents when no agent is running on this issue', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ status: 'in_progress', workflowStage: 'build' }),
      isLoading: false,
      isError: false,
    })
    mockUseAgentStatus.mockReturnValue({
      data: {
        activeAgents: [
          { issueId: 'i-other', issueNumber: 999, projectId: 'proj-1' },
        ],
        capacity: { active: 0, max: 4 },
        runnerAvailable: true,
      },
    })

    renderPage()

    await waitFor(() => expect(screen.getByText(/1 agent running on other issues/i)).toBeInTheDocument())
  })
})
