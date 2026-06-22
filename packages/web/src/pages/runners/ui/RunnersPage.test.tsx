// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { RunnersPage } from './RunnersPage'
import type { RunnerStatusRow } from '../../../entities/runner/model/types'

const mocks = vi.hoisted(() => ({
  rows: [] as RunnerStatusRow[],
  isLoading: false,
  queryFnCalls: 0,
}))

vi.mock('../../../entities/runner', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/runner')>()
  return {
    ...actual,
    useRunners: () => ({ data: mocks.rows, isLoading: mocks.isLoading }),
  }
})

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
          <RunnersPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.rows = []
  mocks.isLoading = false
  mocks.queryFnCalls = 0
})

afterEach(() => {
  cleanup()
})

describe('RunnersPage', () => {
  describe('routing and nav', () => {
    it('renders the page with a stable test id', () => {
      mocks.rows = [makeRow({ id: 'r1' })]
      renderWith()
      expect(screen.getByTestId('runners-page')).toBeInTheDocument()
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
      expect(mocks.rows).toHaveLength(0)
    })
  })

  describe('empty state', () => {
    it('shows the start-command empty state when there are no eligible runners', () => {
      mocks.rows = []
      renderWith()
      expect(screen.getByTestId('runners-empty-state')).toBeInTheDocument()
      expect(screen.getByText('No runners connected')).toBeInTheDocument()
      expect(screen.getByText('npx mohist runner')).toBeInTheDocument()
    })

    it('does not show the empty state when runners exist', () => {
      mocks.rows = [makeRow({ id: 'r1' })]
      renderWith()
      expect(screen.queryByTestId('runners-empty-state')).not.toBeInTheDocument()
    })
  })

  describe('scope filter', () => {
    it('defaults to "all" so global and project-scoped runners are both listed', () => {
      mocks.rows = [
        makeRow({ id: 'global-1', scope: { type: 'global' } }),
        makeRow({
          id: 'project-1',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
        }),
      ]
      renderWith()

      const page = screen.getByTestId('runners-page')
      expect(page).toHaveAttribute('data-scope-filter', 'all')
      expect(screen.getByText('global-1')).toBeInTheDocument()
      expect(screen.getByText('project-1')).toBeInTheDocument()
    })

    it('global filter shows only global runners', () => {
      mocks.rows = [
        makeRow({ id: 'global-1', scope: { type: 'global' } }),
        makeRow({
          id: 'project-1',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
        }),
      ]
      renderWith()
      act(() => {
        fireEvent.click(screen.getByTestId('runners-scope-global'))
      })
      expect(screen.getByText('global-1')).toBeInTheDocument()
      expect(screen.queryByText('project-1')).not.toBeInTheDocument()
    })

    it('project filter shows only project-scoped runners', () => {
      mocks.rows = [
        makeRow({ id: 'global-1', scope: { type: 'global' } }),
        makeRow({
          id: 'project-1',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
        }),
      ]
      renderWith()
      act(() => {
        fireEvent.click(screen.getByTestId('runners-scope-project'))
      })
      expect(screen.queryByText('global-1')).not.toBeInTheDocument()
      expect(screen.getByText('project-1')).toBeInTheDocument()
    })
  })

  describe('summary bar', () => {
    it('shows a count for each of idle, busy, stale, and offline', () => {
      mocks.rows = [
        makeRow({ id: 'r1', status: 'idle' }),
        makeRow({ id: 'r2', status: 'busy' }),
        makeRow({ id: 'r3', status: 'stale', connectionState: 'disconnected' }),
        makeRow({ id: 'r4', status: 'offline', connectionState: 'disconnected' }),
      ]
      renderWith()

      const bar = screen.getByTestId('runners-summary-bar')
      expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-offline-count')).toHaveTextContent('1')
    })

    it('shows zero explicitly for empty status categories (never omits)', () => {
      mocks.rows = [makeRow({ id: 'r1', status: 'idle' })]
      renderWith()

      const bar = screen.getByTestId('runners-summary-bar')
      expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('0')
      expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('0')
      expect(within(bar).getByTestId('runners-summary-offline-count')).toHaveTextContent('0')
    })

    it('updates counts to match the active scope filter', () => {
      mocks.rows = [
        makeRow({ id: 'g1', status: 'idle', scope: { type: 'global' } }),
        makeRow({ id: 'g2', status: 'busy', scope: { type: 'global' } }),
        makeRow({
          id: 'p1',
          status: 'stale',
          scope: { type: 'project', projectId: TEST_PROJECT.id, projectName: TEST_PROJECT.name },
          connectionState: 'disconnected',
        }),
      ]
      renderWith()

      const bar = screen.getByTestId('runners-summary-bar')
      expect(within(bar).getByTestId('runners-summary-idle-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-busy-count')).toHaveTextContent('1')
      expect(within(bar).getByTestId('runners-summary-stale-count')).toHaveTextContent('1')

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
    it('lists offline and stale runners (not hidden)', () => {
      mocks.rows = [
        makeRow({ id: 'r-offline', status: 'offline', connectionState: 'disconnected' }),
        makeRow({ id: 'r-stale', status: 'stale', connectionState: 'disconnected' }),
      ]
      renderWith()

      expect(screen.getByText('r-offline')).toBeInTheDocument()
      expect(screen.getByText('r-stale')).toBeInTheDocument()
      expect(screen.getAllByText('offline').length).toBeGreaterThan(0)
      expect(screen.getAllByText('stale').length).toBeGreaterThan(0)
    })

    it('renders a runner with missing capacity without crashing and shows unavailable', () => {
      mocks.rows = [
        makeRow({
          id: 'r-offline-no-capacity',
          status: 'offline',
          connectionState: 'disconnected',
          capacity: null,
        }),
      ]
      renderWith()

      expect(screen.getByText('r-offline-no-capacity')).toBeInTheDocument()
      expect(screen.queryByText('0/0 slots')).not.toBeInTheDocument()
    })

    it('shows hostname on each row', () => {
      mocks.rows = [makeRow({ id: 'r1', hostname: 'box-1' })]
      renderWith()
      expect(screen.getByText('box-1')).toBeInTheDocument()
    })

    it('shows kind on each row', () => {
      mocks.rows = [makeRow({ id: 'r1', kind: 'external' })]
      renderWith()
      expect(screen.getByText('external')).toBeInTheDocument()
    })

    it('shows scope badge', () => {
      mocks.rows = [makeRow({ id: 'r1', scope: { type: 'global' } })]
      renderWith()
      const badges = screen.getAllByText('global')
      expect(badges.length).toBeGreaterThan(0)
    })

    it('shows capacity as used/total when present', () => {
      mocks.rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          capacity: { usedSlots: 2, totalSlots: 4 },
        }),
      ]
      renderWith()
      expect(screen.getByTestId('runner-capacity')).toBeInTheDocument()
      expect(screen.getByText('2/4')).toBeInTheDocument()
    })
  })
})
