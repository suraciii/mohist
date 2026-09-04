import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { RunnersPage } from './RunnersPage'
import type { RunnerStatusRow } from '../../../entities/runner/model/types'
import { useRunners } from '../../../entities/runner'

let currentRunners: RunnerStatusRow[] = []

const runnersHook: typeof useRunners = () => ({
  data: currentRunners,
}) as ReturnType<typeof useRunners>

function makeRow(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-test',
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

const TEST_PROJECT = {
  id: 'proj-test',
  name: 'Test Project',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

function mockRunners(rows: RunnerStatusRow[]) {
  currentRunners = rows
}

function renderWith({
  initialProjectId = TEST_PROJECT.id,
  initialProjects = [TEST_PROJECT],
}: {
  initialProjectId?: string | null
  initialProjects?: typeof TEST_PROJECT[]
} = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={initialProjectId} initialProjects={initialProjects}>
        <MemoryRouter>
          <RunnersPage runnersHook={runnersHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  currentRunners = []
})

describe('RunnersPage', () => {
  describe('routing and nav', () => {
    it('renders the page with a stable test id', async () => {
      mockRunners([makeRow({ id: 'r1' })])
      renderWith()
      await waitFor(() => {
        expect(screen.getByTestId('runners-page')).toBeInTheDocument()
      })
    })
  })

  describe('no project', () => {
    it('renders a non-runner state when no project is selected', () => {
      renderWith({ initialProjectId: null, initialProjects: [] })
      expect(screen.getByTestId('runners-no-project-state')).toBeInTheDocument()
      expect(screen.queryByTestId('runners-empty-state')).not.toBeInTheDocument()
      expect(screen.queryByTestId('runners-summary-bar')).not.toBeInTheDocument()
    })

    it('does not render the runner list when no project is selected', () => {
      renderWith({ initialProjectId: null, initialProjects: [] })
      expect(screen.queryByText('r1')).not.toBeInTheDocument()
    })

    it('does not issue the project-scoped runner query when no project is selected', () => {
      renderWith({ initialProjectId: null, initialProjects: [] })
      expect(screen.queryByText('runner-test')).not.toBeInTheDocument()
    })
  })

  describe('empty state', () => {
    it('shows first-install and later-start commands when there are no eligible runners', async () => {
      mockRunners([])
      renderWith()
      await waitFor(() => {
        expect(screen.getByTestId('runners-empty-state')).toBeInTheDocument()
      })
      expect(screen.getByText('No runners connected')).toBeInTheDocument()
      expect(screen.getByText('mo install runner --repo-root <path>')).toBeInTheDocument()
      expect(screen.getByText('mo service start runner')).toBeInTheDocument()
    })

    it('does not show the empty state when runners exist', async () => {
      mockRunners([makeRow({ id: 'r1' })])
      renderWith()
      await waitFor(() => {
        expect(screen.queryByTestId('runners-empty-state')).not.toBeInTheDocument()
      })
    })
  })

  describe('scope filter', () => {
    it('defaults to "all" so global and project-scoped runners are both listed', async () => {
      mockRunners([
        makeRow({ id: 'global-1', scope: { type: 'global' } }),
        makeRow({
          id: 'project-1',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
        }),
      ])
      renderWith()

      await waitFor(() => {
        expect(screen.getByText('global-1')).toBeInTheDocument()
        expect(screen.getByText('project-1')).toBeInTheDocument()
      })
      const page = screen.getByTestId('runners-page')
      expect(page).toHaveAttribute('data-scope-filter', 'all')
    })

    it('global filter shows only global runners', async () => {
      mockRunners([
        makeRow({ id: 'global-1', scope: { type: 'global' } }),
        makeRow({
          id: 'project-1',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
        }),
      ])
      renderWith()
      await waitFor(() => {
        expect(screen.getByText('global-1')).toBeInTheDocument()
      })
      act(() => {
        fireEvent.click(screen.getByTestId('runners-scope-global'))
      })
      expect(screen.getByText('global-1')).toBeInTheDocument()
      expect(screen.queryByText('project-1')).not.toBeInTheDocument()
    })

    it('project filter shows only project-scoped runners', async () => {
      mockRunners([
        makeRow({ id: 'global-1', scope: { type: 'global' } }),
        makeRow({
          id: 'project-1',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
        }),
      ])
      renderWith()
      await waitFor(() => {
        expect(screen.getByText('project-1')).toBeInTheDocument()
      })
      act(() => {
        fireEvent.click(screen.getByTestId('runners-scope-project'))
      })
      expect(screen.queryByText('global-1')).not.toBeInTheDocument()
      expect(screen.getByText('project-1')).toBeInTheDocument()
    })
  })

  describe('summary bar', () => {
    it('shows a count for each of idle, busy, stale, and offline', async () => {
      mockRunners([
        makeRow({ id: 'r1', status: 'idle' }),
        makeRow({ id: 'r2', status: 'busy' }),
        makeRow({ id: 'r3', status: 'stale', connectionState: 'disconnected' }),
        makeRow({ id: 'r4', status: 'offline', connectionState: 'disconnected' }),
      ])
      renderWith()

      const bar = await waitFor(() => screen.getByTestId('runners-summary-bar'))
      await waitFor(() => {
        expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
        expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('1')
        expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('1')
        expect(within(bar).getByTestId('runners-summary-offline-count')).toHaveTextContent('1')
      })
    })

    it('shows zero explicitly for empty status categories (never omits)', async () => {
      mockRunners([makeRow({ id: 'r1', status: 'idle' })])
      renderWith()

      await waitFor(() => {
        expect(screen.getByTestId('runners-summary-bar')).toBeInTheDocument()
      })
      const bar = screen.getByTestId('runners-summary-bar')
      await waitFor(() => {
        expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
        expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('0')
        expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('0')
        expect(within(bar).getByTestId('runners-summary-offline-count')).toHaveTextContent('0')
      })
    })

    it('updates counts to match the active scope filter', async () => {
      mockRunners([
        makeRow({ id: 'g1', status: 'idle', scope: { type: 'global' } }),
        makeRow({ id: 'g2', status: 'busy', scope: { type: 'global' } }),
        makeRow({
          id: 'p1',
          status: 'stale',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
          connectionState: 'disconnected',
        }),
      ])
      renderWith()

      await waitFor(() => {
        expect(screen.getByTestId('runners-summary-bar')).toBeInTheDocument()
      })
      const bar = screen.getByTestId('runners-summary-bar')
      await waitFor(() => {
        expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
        expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('1')
        expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('1')
      })

      act(() => {
        fireEvent.click(screen.getByTestId('runners-scope-global'))
      })
      expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('0')
      expect(within(bar).getByTestId('runners-summary-offline-count')).toHaveTextContent('0')

      act(() => {
        fireEvent.click(screen.getByTestId('runners-scope-project'))
      })
      expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('0')
      expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('0')
      expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-offline-count')).toHaveTextContent('0')
    })
  })

  describe('row rendering', () => {
    it('lists offline and stale runners (not hidden)', async () => {
      mockRunners([
        makeRow({ id: 'r-offline', status: 'offline', connectionState: 'disconnected' }),
        makeRow({ id: 'r-stale', status: 'stale', connectionState: 'disconnected' }),
      ])
      renderWith()

      await waitFor(() => {
        expect(screen.getByText('r-offline')).toBeInTheDocument()
      })
      expect(screen.getByText('r-stale')).toBeInTheDocument()
      expect(screen.getAllByText('offline').length).toBeGreaterThan(0)
      expect(screen.getAllByText('stale').length).toBeGreaterThan(0)
    })

    it('renders a runner with missing capacity without crashing and shows unavailable', async () => {
      mockRunners([
        makeRow({
          id: 'r-offline-no-capacity',
          status: 'offline',
          connectionState: 'disconnected',
          capacity: null,
        }),
      ])
      renderWith()

      await waitFor(() => {
        expect(screen.getByText('r-offline-no-capacity')).toBeInTheDocument()
      })
      expect(screen.queryByText('0/0 slots')).not.toBeInTheDocument()
    })

    it('shows hostname on each row', async () => {
      mockRunners([makeRow({ id: 'r1', hostname: 'box-1' })])
      renderWith()
      await waitFor(() => {
        expect(screen.getByText('box-1')).toBeInTheDocument()
      })
    })

    it('shows kind on each row', async () => {
      mockRunners([makeRow({ id: 'r1', kind: 'external' })])
      renderWith()
      await waitFor(() => {
        expect(screen.getByText('external')).toBeInTheDocument()
      })
    })

    it('shows scope badge', async () => {
      mockRunners([makeRow({ id: 'r1', scope: { type: 'global' } })])
      renderWith()
      await waitFor(() => {
        expect(screen.getAllByText('global').length).toBeGreaterThan(0)
      })
    })

    it('shows capacity as used/total when present', async () => {
      mockRunners([
        makeRow({
          id: 'r1',
          status: 'busy',
          capacity: { usedSlots: 2, totalSlots: 4 },
        }),
      ])
      renderWith()
      await waitFor(() => {
        expect(screen.getByTestId('runner-capacity')).toBeInTheDocument()
      })
      expect(screen.getByText('2/4')).toBeInTheDocument()
    })
  })
})
