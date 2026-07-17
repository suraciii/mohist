import '@testing-library/jest-dom'
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { fireEvent, render, screen, cleanup, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'
import type { Project } from '../../../entities/project'
import { ProjectProvider } from '../../../entities/project'
import type { RunnerStatusRow, RunnerStatusSummary } from '../../../entities/runner/model/types'
import { RunnerSummary } from './RunnerSummary'
import { RunnerList, RunnerListCard } from './RunnerList'

const TEST_PROJECT: Project = {
  id: 'proj-1',
  name: 'mohist-local',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

function makeRow(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-test',
    kind: 'external',
    hostname: 'test-host',
    scope: { type: 'global' },
    status: 'idle',
    capabilities: ['workflow', 'workspace-query'],
    coderModels: ['openai/gpt-4.5'],
    coderModelCount: 1,
    registeredAt: '2026-01-01T00:00:00Z',
    lastHeartbeatAt: '2026-01-01T12:00:00Z',
    connectionState: 'connected',
    activeWorks: [],
    ...overrides,
  }
}

function makeSummary(overrides: Partial<RunnerStatusSummary> = {}): RunnerStatusSummary {
  return {
    connectedIdleCount: 0,
    connectedBusyCount: 0,
    hasConnectedCapacity: false,
    rows: [],
    ...overrides,
  }
}

const RUNNER_START_HINT = 'Start a runner with: npx mohist runner'
const RUNNER_START_HINT_LIST = 'npx mohist runner'

function renderInRouter(ui: React.ReactNode, { withProject = false, initialEntries }: { withProject?: boolean; initialEntries?: string[] } = {}) {
  const tree = (
    <MemoryRouter initialEntries={initialEntries}>
      {withProject ? <ProjectProvider initialProjects={[TEST_PROJECT]} initialProjectId={TEST_PROJECT.id}>{ui}</ProjectProvider> : ui}
    </MemoryRouter>
  )
  return render(tree)
}

function renderInProjectRoutes(ui: React.ReactNode) {
  return render(
    <MemoryRouter initialEntries={['/mohist-local/activity']}>
      <ProjectProvider initialProjects={[TEST_PROJECT]} initialProjectId={TEST_PROJECT.id}>
        <Routes>
          <Route path="/:projectName/activity" element={ui} />
          <Route path="/:projectName/issues/:number" element={<div data-testid="issue-route">Issue route</div>} />
          <Route path="/:projectName/runners/:runnerId" element={<div data-testid="runner-route">Runner route</div>} />
        </Routes>
      </ProjectProvider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  cleanup()
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

describe('RunnerSummary UI', () => {
  describe('empty state', () => {
    it('shows no runner message when rows are empty', () => {
      const summary = makeSummary({ rows: [] })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('No runner')).toBeInTheDocument()
    })

    it('shows startup command hint when no runners connected', () => {
      const summary = makeSummary({ rows: [] })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText(RUNNER_START_HINT)).toBeInTheDocument()
    })
  })

  describe('stale/offline state', () => {
    it('shows stale/offline badge when no connected capacity', () => {
      const rows = [makeRow({ status: 'stale', connectionState: 'disconnected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: false,
        connectedIdleCount: 0,
        connectedBusyCount: 0,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('Runner stale/offline')).toBeInTheDocument()
    })

    it('shows startup hint for stale/offline state', () => {
      const rows = [makeRow({ status: 'offline', connectionState: 'disconnected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: false,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText(RUNNER_START_HINT)).toBeInTheDocument()
    })

    it('links to activity page in stale/offline state', () => {
      const rows = [makeRow({ status: 'stale', connectionState: 'disconnected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: false,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      const button = screen.getByRole('button')
      expect(button).toBeInTheDocument()
    })
  })

  describe('connected idle state', () => {
    it('shows runner idle badge when connected idle', () => {
      const rows = [makeRow({ status: 'idle', connectionState: 'connected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 1,
        connectedBusyCount: 0,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('Runner idle')).toBeInTheDocument()
    })

    it('shows ready count for idle runners', () => {
      const rows = [makeRow({ id: 'r1', status: 'idle', connectionState: 'connected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 1,
        connectedBusyCount: 0,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('1 runner ready')).toBeInTheDocument()
    })

    it('shows runners ready text for multiple idle runners', () => {
      const rows = [
        makeRow({ id: 'r1', status: 'idle', connectionState: 'connected' }),
        makeRow({ id: 'r2', status: 'idle', connectionState: 'connected' }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 2,
        connectedBusyCount: 0,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('2 runners ready')).toBeInTheDocument()
    })
  })

  describe('connected busy state', () => {
    it('shows runner busy badge when connected busy', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 0,
        connectedBusyCount: 1,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('Runner busy')).toBeInTheDocument()
    })

    it('shows running workflow count for busy runners', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 0,
        connectedBusyCount: 1,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('1 running workflow')).toBeInTheDocument()
    })

    it('shows running workflows text for multiple busy runners', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
        makeRow({
          id: 'r2',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w2', ownerKind: 'workflow', ownerId: 'wf2', workType: 'workflow' }],
        }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 0,
        connectedBusyCount: 2,
      })
      renderInRouter(<RunnerSummary summary={summary} />)
      expect(screen.getByText('2 running workflows')).toBeInTheDocument()
    })
  })
})

describe('RunnerList UI', () => {
  describe('empty state', () => {
    it('shows no runners connected message', () => {
      renderInRouter(<RunnerList rows={[]} />)
      expect(screen.getByText('No runners connected')).toBeInTheDocument()
    })

    it('shows startup command hint in empty state', () => {
      renderInRouter(<RunnerList rows={[]} />)
      expect(screen.getByText(/Start a runner:/)).toBeInTheDocument()
      expect(screen.getByText(RUNNER_START_HINT_LIST)).toBeInTheDocument()
    })

    it('does not render a misleading settings manage action on the card', () => {
      render(
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter>
            <RunnerListCard />
          </MemoryRouter>
        </QueryClientProvider>,
      )

      expect(screen.getByText('Runners')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Manage' })).not.toBeInTheDocument()
    })
  })

  describe('idle runner rendering', () => {
    it('shows runner id', () => {
      const rows = [makeRow({ id: 'runner-455532', status: 'idle', connectionState: 'connected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('runner-455532')).toBeInTheDocument()
    })

    it('shows runner kind', () => {
      const rows = [makeRow({ kind: 'external', status: 'idle', connectionState: 'connected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('external')).toBeInTheDocument()
    })

    it('shows runner hostname', () => {
      const rows = [makeRow({ hostname: 'devbox', status: 'idle', connectionState: 'connected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('devbox')).toBeInTheDocument()
    })

    it('shows global scope badge', () => {
      const rows = [makeRow({ scope: { type: 'global' }, status: 'idle', connectionState: 'connected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('global')).toBeInTheDocument()
    })

    it('shows project scope badge with project name', () => {
      const rows = [
        makeRow({
          scope: { type: 'project', projectId: 'proj-1', projectName: 'My Project' },
          status: 'idle',
          connectionState: 'connected',
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('My Project')).toBeInTheDocument()
    })

    it('shows idle status badge', () => {
      const rows = [makeRow({ status: 'idle', connectionState: 'connected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('idle')).toBeInTheDocument()
    })

    it('shows heartbeat information when available', () => {
      vi.setSystemTime(new Date('2026-01-01T12:02:00Z'))
      const heartbeatTime = '2026-01-01T12:00:00Z'
      const rows = [makeRow({ lastHeartbeatAt: heartbeatTime, status: 'idle', connectionState: 'connected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('2m ago')).toBeInTheDocument()
    })

    it('shows connected connection state', () => {
      const rows = [makeRow({ connectionState: 'connected', status: 'idle' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('connected')).toBeInTheDocument()
    })

    it('shows coder model count and names', () => {
      const rows = [
        makeRow({
          coderModels: ['openai/gpt-4.5', 'anthropic/claude-3'],
          coderModelCount: 2,
          status: 'idle',
          connectionState: 'connected',
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('2 models')).toBeInTheDocument()
      expect(screen.getByText('openai/gpt-4.5')).toBeInTheDocument()
      expect(screen.getByText('anthropic/claude-3')).toBeInTheDocument()
    })

    it('shows runner capabilities', () => {
      const rows = [
        makeRow({
          capabilities: ['workflow', 'workspace-query'],
          status: 'idle',
          connectionState: 'connected',
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('workflow')).toBeInTheDocument()
      expect(screen.getByText('workspace-query')).toBeInTheDocument()
    })
  })

  describe('busy runner rendering', () => {
    it('shows busy status badge', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('busy')).toBeInTheDocument()
    })

    it('shows active work reference with workflow run id', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf-123', workType: 'workflow' }],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText(/wf-123/)).toBeInTheDocument()
    })

    it('shows active work title when available', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow', title: 'Fix login bug' }],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText(/Fix login bug/)).toBeInTheDocument()
    })

    it('shows work type when title is not available', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      const workRow = screen.getByTestId('active-work-row')
      expect(within(workRow).getByText('workflow')).toBeInTheDocument()
      expect(within(workRow).getByText('wf1')).toBeInTheDocument()
    })

    it('shows capacity slots when available', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          capacity: { usedSlots: 1, totalSlots: 2 },
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByTestId('runner-capacity')).toBeInTheDocument()
      expect(screen.getByText('1/2')).toBeInTheDocument()
      expect(screen.getByText('slots')).toBeInTheDocument()
    })

    it('renders every active work as an independent row (no collapse)', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [
            { workId: 'w1', ownerKind: 'workflow', ownerId: 'wf-a', workType: 'workflow', title: 'Work A' },
            { workId: 'w2', ownerKind: 'workflow', ownerId: 'wf-b', workType: 'workflow', title: 'Work B' },
            { workId: 'w3', ownerKind: 'workflow', ownerId: 'wf-c', workType: 'workflow', title: 'Work C' },
          ],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByTestId('runner-active-works')).toHaveAttribute('data-count', '3')
      expect(screen.getByText('Work A')).toBeInTheDocument()
      expect(screen.getByText('Work B')).toBeInTheDocument()
      expect(screen.getByText('Work C')).toBeInTheDocument()
    })

    it('renders an issue link when the work carries an issue ref', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [
            {
              workId: 'w1',
              ownerKind: 'workflow',
              ownerId: 'wf-1',
              workType: 'workflow',
              title: 'Add dark mode',
              issue: { projectId: 'proj-x', issueNumber: 42, },
            },
          ],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />, { withProject: true })
      const link = screen.getByTestId('active-work-issue-link')
      expect(link).toHaveAttribute('href', '/mohist-local/issues/42')
      expect(link).toHaveTextContent('#42')
    })

    it('opens the issue link without also navigating the runner row', () => {
      const rows = [
        makeRow({
          id: 'runner-with-issue-link',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [
            {
              workId: 'w1',
              ownerKind: 'workflow',
              ownerId: 'wf-1',
              workType: 'workflow',
              title: 'Add dark mode',
              issue: { projectId: 'proj-x', issueNumber: 42, },
            },
          ],
        }),
      ]
      renderInProjectRoutes(<RunnerList rows={rows} />)

      fireEvent.click(screen.getByTestId('active-work-issue-link'))

      expect(screen.getByTestId('issue-route')).toBeInTheDocument()
      expect(screen.queryByTestId('runner-route')).not.toBeInTheDocument()
    })

    it('omits the issue link cleanly when issue ref is absent', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [
            { workId: 'w1', ownerKind: 'workflow', ownerId: 'wf-1', workType: 'workflow', title: 'No-issue work' },
          ],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.queryByTestId('active-work-issue-link')).not.toBeInTheDocument()
      expect(screen.getByText('No-issue work')).toBeInTheDocument()
    })

    it('makes each runner row navigable to its detail page keyed by id', () => {
      const rows = [
        makeRow({ id: 'runner-9', status: 'idle', connectionState: 'connected' }),
      ]
      renderInRouter(<RunnerList rows={rows} />, { withProject: true })
      const row = screen.getByTestId('runner-row')
      expect(row).toHaveAttribute('data-href', '/mohist-local/runners/runner-9')
      expect(row).toHaveAttribute('role', 'link')
      expect(row).toHaveAttribute('data-runner-id', 'runner-9')
    })
  })

  describe('stale/offline runner rendering', () => {
    it('shows stale status badge', () => {
      const rows = [makeRow({ status: 'stale', connectionState: null })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('stale')).toBeInTheDocument()
    })

    it('shows offline status badge', () => {
      const rows = [makeRow({ status: 'offline', connectionState: 'disconnected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('offline')).toBeInTheDocument()
    })

    it('shows hostname for stale runner', () => {
      const rows = [makeRow({ hostname: 'old-host', status: 'stale', connectionState: 'disconnected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('old-host')).toBeInTheDocument()
    })

    it('shows last heartbeat for stale runner', () => {
      vi.setSystemTime(new Date('2026-01-01T12:10:00Z'))
      const rows = [makeRow({ lastHeartbeatAt: '2026-01-01T12:00:00Z', status: 'stale', connectionState: 'disconnected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('10m ago')).toBeInTheDocument()
    })

    it('shows explicit heartbeat diagnostic for offline runner', () => {
      vi.setSystemTime(new Date('2026-01-01T14:00:00Z'))
      const rows = [makeRow({ lastHeartbeatAt: '2026-01-01T12:00:00Z', status: 'offline', connectionState: 'disconnected' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('2h ago')).toBeInTheDocument()
    })

    it('shows disconnected connection state', () => {
      const rows = [makeRow({ connectionState: 'disconnected', status: 'stale' })]
      renderInRouter(<RunnerList rows={rows} />)
      expect(screen.getByText('disconnected')).toBeInTheDocument()
    })
  })

  describe('list summary preservation', () => {
    it('preserves summary content on busy runner rows (id, kind, scope, status)', () => {
      const rows = [
        makeRow({
          id: 'r1',
          kind: 'external',
          scope: { type: 'project', projectId: 'proj-1', projectName: 'My Project' },
          status: 'busy',
          connectionState: 'connected',
          activeWorks: [{ workId: 'w1', ownerKind: 'workflow', ownerId: 'wf1', workType: 'workflow' }],
        }),
      ]
      renderInRouter(<RunnerList rows={rows} />)
      const row = screen.getByTestId('runner-row')
      const content = within(row)
      expect(content.getByText('r1')).toBeInTheDocument()
      expect(content.getByText('external')).toBeInTheDocument()
      expect(content.getByText('My Project')).toBeInTheDocument()
      expect(content.getByText('busy')).toBeInTheDocument()
      expect(content.getByText('connected')).toBeInTheDocument()
    })
  })
})
