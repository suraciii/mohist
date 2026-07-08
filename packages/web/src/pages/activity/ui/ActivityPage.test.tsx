// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { ActivityPage } from './ActivityPage'
import type { RunnerStatusSummary } from '../../../entities/runner/model/types'

const mocks = vi.hoisted(() => ({
  summary: {
    connectedIdleCount: 0,
    connectedBusyCount: 0,
    hasConnectedCapacity: false,
    rows: [],
  } as RunnerStatusSummary,
  agentActivity: {
    data: {
      summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 0 } },
      sessions: [],
      waiting: [],
    },
    isLoading: false,
  },
}))

vi.mock('../../../entities/runner', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/runner')>()
  return {
    ...actual,
    useRunnerSummary: () => mocks.summary,
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentActivity: () => mocks.agentActivity,
  }
})

const TEST_PROJECT = {
  id: 'proj-test',
  name: 'TestProject',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

function LocationProbe({ testId }: { testId: string }) {
  const location = useLocation()
  return <div data-testid={testId}>{location.pathname}</div>
}

function renderWith({
  initialProjectId = TEST_PROJECT.id,
  initialProjects = [TEST_PROJECT],
  initialRoute = `/${TEST_PROJECT.name}/activity`,
}: {
  initialProjectId?: string | null
  initialProjects?: typeof TEST_PROJECT[]
  initialRoute?: string
} = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={initialProjectId} initialProjects={initialProjects}>
        <MemoryRouter initialEntries={[initialRoute]}>
          <LocationProbe testId="route-pathname" />
          <ActivityPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function makeRow(overrides: Partial<RunnerStatusSummary['rows'][number]> = {}): RunnerStatusSummary['rows'][number] {
  return {
    id: 'runner-r1',
    kind: 'external',
    hostname: 'test-host',
    scope: { type: 'global' },
    status: 'idle',
    capabilities: [],
    coderModels: [],
    coderModelCount: 0,
    registeredAt: '2026-01-01T00:00:00Z',
    lastHeartbeatAt: '2026-01-01T12:00:00Z',
    connectionState: 'connected',
    activeWorks: [],
    ...overrides,
  }
}

function getRunnerBadgeButton(): HTMLElement {
  return screen.getByRole('button')
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.summary = {
    connectedIdleCount: 0,
    connectedBusyCount: 0,
    hasConnectedCapacity: false,
    rows: [],
  }
  mocks.agentActivity = {
    data: {
      summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 0 } },
      sessions: [],
      waiting: [],
    },
    isLoading: false,
  }
})

afterEach(() => {
  cleanup()
  window.localStorage.clear()
})

describe('ActivityPage', () => {
  describe('delegation to the Runners page', () => {
    it('does not render the embedded runner list section', () => {
      mocks.summary = {
        connectedIdleCount: 1,
        connectedBusyCount: 0,
        hasConnectedCapacity: true,
        rows: [makeRow({ id: 'r1' })],
      }

      renderWith()

      expect(screen.queryByTestId('runners-empty-state')).not.toBeInTheDocument()
      expect(screen.queryByTestId('runners-summary-bar')).not.toBeInTheDocument()
      expect(screen.queryByTestId('runners-page')).not.toBeInTheDocument()
      expect(screen.queryByText('r1')).not.toBeInTheDocument()
      expect(screen.queryByText(/Loading\.\.\./)).not.toBeInTheDocument()
    })

    it('renders a link that navigates to the project-scoped /runners route', () => {
      renderWith()

      const link = screen.getByTestId('activity-runners-link')
      expect(link).toBeInTheDocument()
      expect(link.tagName.toLowerCase()).toBe('a')
      expect(link).toHaveAttribute('href', `/${TEST_PROJECT.name}/runners`)
    })

    it('keeps the runner overview badge in the status bar when runners are idle', () => {
      mocks.summary = {
        connectedIdleCount: 1,
        connectedBusyCount: 0,
        hasConnectedCapacity: true,
        rows: [makeRow({ status: 'idle' })],
      }

      renderWith()

      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner idle/)
      expect(badge.textContent).toMatch(/1 runner ready/)
    })

    it('keeps the runner overview badge when a runner is stale', () => {
      mocks.summary = {
        connectedIdleCount: 0,
        connectedBusyCount: 0,
        hasConnectedCapacity: false,
        rows: [makeRow({ status: 'stale' })],
      }

      renderWith()

      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner stale/)
    })

    it('keeps the runner overview badge when runners are offline', () => {
      mocks.summary = {
        connectedIdleCount: 0,
        connectedBusyCount: 0,
        hasConnectedCapacity: false,
        rows: [makeRow({ status: 'offline' })],
      }

      renderWith()

      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner offline/)
    })

    it('keeps the runner overview badge when runners are busy', () => {
      mocks.summary = {
        connectedIdleCount: 0,
        connectedBusyCount: 2,
        hasConnectedCapacity: true,
        rows: [makeRow({ status: 'busy' }), makeRow({ id: 'r2', status: 'busy' })],
      }

      renderWith()

      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner busy/)
      expect(badge.textContent).toMatch(/2 running workflows/)
    })
  })

  describe('runner overview badge navigation target', () => {
    it('navigates to /runners when the idle badge is activated', () => {
      mocks.summary = {
        connectedIdleCount: 1,
        connectedBusyCount: 0,
        hasConnectedCapacity: true,
        rows: [makeRow({ status: 'idle' })],
      }

      renderWith()
      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner idle/)

      fireEvent.click(badge)

      expect(screen.getByTestId('route-pathname').textContent).toBe('/TestProject/runners')
    })

    it('navigates to /runners when the busy badge is activated', () => {
      mocks.summary = {
        connectedIdleCount: 0,
        connectedBusyCount: 1,
        hasConnectedCapacity: true,
        rows: [makeRow({ status: 'busy' })],
      }

      renderWith()
      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner busy/)

      fireEvent.click(badge)

      expect(screen.getByTestId('route-pathname').textContent).toBe('/TestProject/runners')
    })

    it('navigates to /runners when the stale badge is activated', () => {
      mocks.summary = {
        connectedIdleCount: 0,
        connectedBusyCount: 0,
        hasConnectedCapacity: false,
        rows: [makeRow({ status: 'stale' })],
      }

      renderWith()
      const badge = getRunnerBadgeButton()
      expect(badge.textContent).toMatch(/Runner stale/)

      fireEvent.click(badge)

      expect(screen.getByTestId('route-pathname').textContent).toBe('/TestProject/runners')
    })

    it('navigates to /runners (not /activity) when any badge is activated', () => {
      mocks.summary = {
        connectedIdleCount: 1,
        connectedBusyCount: 0,
        hasConnectedCapacity: true,
        rows: [makeRow({ status: 'idle' })],
      }

      renderWith()
      fireEvent.click(getRunnerBadgeButton())

      const pathname = screen.getByTestId('route-pathname').textContent
      expect(pathname).not.toBe('/TestProject/activity')
      expect(pathname).toBe('/TestProject/runners')
    })
  })

  describe('session-only content', () => {
    it('still renders the Active / Waiting / Recent section headers', () => {
      renderWith()

      const activeSection = screen.getByRole('heading', { level: 3, name: 'Active' }).closest('section')
      const waitingSection = screen.getByRole('heading', { level: 3, name: 'Waiting' }).closest('section')
      const recentSection = screen.getByRole('heading', { level: 3, name: 'Recent' }).closest('section')

      expect(activeSection).not.toBeNull()
      expect(waitingSection).not.toBeNull()
      expect(recentSection).not.toBeNull()

      expect(within(activeSection!).getByText('No active sessions')).toBeInTheDocument()
      expect(within(waitingSection!).getByText('No issues waiting for action')).toBeInTheDocument()
      expect(within(recentSection!).getByText('No recent sessions')).toBeInTheDocument()
    })

    it('does not render an embedded "Runners" Card header', () => {
      renderWith()

      expect(screen.queryByRole('heading', { name: 'Runners' })).not.toBeInTheDocument()
    })
  })
})
