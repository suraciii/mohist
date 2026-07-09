// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { type AgentStatus } from '../../../entities/agent'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { DashboardCapacityZone } from './DashboardCapacityZone'

useMswServer()

const STATUS_PATH = '*/api/projects/:projectId/agent/status'

function mockStatusResponse(data: AgentStatus) {
  server.use(http.get(STATUS_PATH, () => HttpResponse.json({ success: true, data })))
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
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter initialEntries={[`/${TEST_PROJECT.name}`]}>
          <DashboardCapacityZone />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  server.use(http.get(STATUS_PATH, () => HttpResponse.json({ success: false, error: 'unhandled' }, { status: 404 })))
})

afterEach(() => {
  cleanup()
})

describe('DashboardCapacityZone', () => {
  it('renders the dashboard-zone-capacity strip with usage and link when capacity data is present', async () => {
    mockStatusResponse(makeAgentStatus({ capacity: { active: 4, max: 8 } }))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-zone-capacity')).toBeInTheDocument()
    })

    const zone = screen.getByTestId('dashboard-zone-capacity')
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

  it('marks the strip as saturated when active equals or exceeds max', async () => {
    mockStatusResponse(makeAgentStatus({ capacity: { active: 8, max: 8 } }))

    renderZone()

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-zone-capacity')).toHaveAttribute('data-state', 'saturated')
    })
    expect(screen.getByTestId('dashboard-zone-capacity-count')).toHaveTextContent('8/8')
  })

  it('collapses (renders nothing) when capacity data is absent', async () => {
    const { container } = renderZone()

    await waitFor(() => {
      expect(container.firstChild).toBeNull()
    })
  })

  it('collapses (renders nothing) when capacity.max is zero (unconfigured runner)', async () => {
    mockStatusResponse(makeAgentStatus({ capacity: { active: 0, max: 0 } }))

    const { container } = renderZone()

    await waitFor(() => {
      expect(container.firstChild).toBeNull()
    })
  })

  it('links to runner management using the project-scoped path', async () => {
    mockStatusResponse(makeAgentStatus({ capacity: { active: 2, max: 4 } }))

    renderZone()

    const link = await waitFor(() => screen.getByTestId('dashboard-zone-capacity-link'))
    expect(link).toHaveAttribute('href', `/${encodeURIComponent(TEST_PROJECT.name)}/runners`)
  })
})
