import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockAgentStatus, mockIssue, mountIssueDetail } from './_issueDetailMsw'

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

mountIssueDetail({ issue: makeIssue() })

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

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage - capacity-full gating uses server capacity.active/capacity.max (not activeAgents.length)', () => {
  it('disables Start when capacity.active >= capacity.max regardless of activeAgents.length', async () => {
    mockIssue(makeIssue())
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 2, max: 2 },
      runnerAvailable: true,
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).toBeDisabled()
    expect(startButton.getAttribute('title')).toMatch(/capacity is full/i)
  })

  it('enables Start when capacity.active < capacity.max even if activeAgents is empty', async () => {
    mockIssue(makeIssue())
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 0, max: 2 },
      runnerAvailable: true,
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('does not gate Start on activeAgents.length - Start stays enabled when activeAgents is long but capacity is not full', async () => {
    mockIssue(makeIssue())
    mockAgentStatus({
      activeAgents: [
        { issueNumber: 101, projectId: 'proj-1' },
        { issueNumber: 102, projectId: 'proj-1' },
        { issueNumber: 103, projectId: 'proj-1' },
      ],
      capacity: { active: 1, max: 4 },
      runnerAvailable: true,
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('gates Start on server capacity even when activeAgents is empty (capacity reflects runner works, not sessions)', async () => {
    mockIssue(makeIssue())
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 4, max: 4 },
      runnerAvailable: true,
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).toBeDisabled()
    expect(startButton.getAttribute('title')).toMatch(/capacity is full/i)
  })

  it('treats capacity.max === 0 as not-full (does not disable Start on a zero-max placeholder)', async () => {
    mockIssue(makeIssue())
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 0, max: 0 },
      runnerAvailable: true,
    })

    renderPage()

    const startButton = await waitFor(() => screen.getByTestId('runtime-action-start'))
    expect(startButton).not.toBeDisabled()
    expect(startButton).toHaveTextContent(/^Start$/)
  })

  it('keeps the other-issues running indicator visible from activeAgents when no agent is running on this issue', async () => {
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build' }))
    mockAgentStatus({
      activeAgents: [
        { issueNumber: 999, projectId: 'proj-1' },
      ],
      capacity: { active: 0, max: 4 },
      runnerAvailable: true,
    })

    renderPage()

    await waitFor(() => expect(screen.getByText(/1 agent running on other issues/i)).toBeInTheDocument())
  })
})
