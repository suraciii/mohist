// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { DashboardCapacityZone } from './DashboardCapacityZone'

const mocks = vi.hoisted(() => ({
  agentStatus: undefined as AgentStatus | undefined,
  useAgentStatus: vi.fn(() => ({ data: mocks.agentStatus })),
}))

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: () => mocks.useAgentStatus(),
  }
})

const demoProject = {
  id: 'proj-1',
  name: 'demo',
  createdAt: '',
  updatedAt: '',
  repositories: [],
}

function makeAgentStatus(overrides: Partial<AgentStatus> = {}): AgentStatus {
  return {
    running: false,
    issueId: null,
    issueNumber: null,
    activeAgents: [],
    capacity: { active: 0, max: 8 },
    runnerAvailable: true,
    ...overrides,
  }
}

function renderZone() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[demoProject]}>
        <MemoryRouter initialEntries={['/demo']}>
          <DashboardCapacityZone />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  mocks.agentStatus = undefined
  mocks.useAgentStatus.mockClear()
  mocks.useAgentStatus.mockImplementation(() => ({ data: mocks.agentStatus }))
})

afterEach(() => {
  cleanup()
})

describe('DashboardCapacityZone', () => {
  it('renders the dashboard-zone-capacity strip with usage and link when capacity data is present', () => {
    mocks.agentStatus = makeAgentStatus({ capacity: { active: 4, max: 8 } })

    renderZone()

    const zone = screen.getByTestId('dashboard-zone-capacity')
    expect(zone).toBeInTheDocument()
    expect(zone).toHaveAttribute('data-zone', 'capacity')
    expect(zone).toHaveAttribute('data-active', '4')
    expect(zone).toHaveAttribute('data-max', '8')
    expect(zone).toHaveAttribute('data-state', 'available')
    expect(screen.getByTestId('dashboard-zone-capacity-label')).toHaveTextContent('Runner capacity')
    expect(screen.getByTestId('dashboard-zone-capacity-count')).toHaveTextContent('4/8')
    expect(screen.getByTestId('dashboard-zone-capacity-bar')).toBeInTheDocument()
    expect(screen.getByTestId('dashboard-zone-capacity-usage')).toBeInTheDocument()
    expect(screen.getByTestId('dashboard-zone-capacity-link')).toBeInTheDocument()
  })

  it('marks the strip as saturated when active equals or exceeds max', () => {
    mocks.agentStatus = makeAgentStatus({ capacity: { active: 8, max: 8 } })

    renderZone()

    const zone = screen.getByTestId('dashboard-zone-capacity')
    expect(zone).toHaveAttribute('data-state', 'saturated')
    expect(screen.getByTestId('dashboard-zone-capacity-count')).toHaveTextContent('8/8')
  })

  it('collapses (renders nothing) when capacity data is absent', () => {
    mocks.agentStatus = undefined

    const { container } = renderZone()

    expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
    expect(container.firstChild).toBeNull()
  })

  it('collapses (renders nothing) when capacity.max is zero (unconfigured runner)', () => {
    mocks.agentStatus = makeAgentStatus({ capacity: { active: 0, max: 0 } })

    const { container } = renderZone()

    expect(screen.queryByTestId('dashboard-zone-capacity')).not.toBeInTheDocument()
    expect(container.firstChild).toBeNull()
  })

  it('links to the runner settings page using the project-scoped path', () => {
    mocks.agentStatus = makeAgentStatus({ capacity: { active: 2, max: 4 } })

    renderZone()

    const link = screen.getByTestId('dashboard-zone-capacity-link')
    expect(link.getAttribute('href')).toMatch(/\/runners$/)
  })
})