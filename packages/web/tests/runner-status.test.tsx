// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import React from 'react'
import type { RunnerStatusRow, RunnerStatusSummary } from '../src/entities/runner/model/types'
import { RunnerSummary } from '../src/widgets/runner-status/ui/RunnerSummary'
import { RunnerList, RunnerListCard } from '../src/widgets/runner-status/ui/RunnerList'

vi.mock('../src/entities/runner', () => ({
  useRunners: vi.fn(),
}))

const { useRunners } = await import('../src/entities/runner')

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

beforeEach(() => {
  cleanup()
  ;(useRunners as ReturnType<typeof vi.fn>).mockReturnValue({
    data: [],
    isLoading: false,
  })
})

afterEach(() => {
  cleanup()
})

describe('RunnerSummary UI', () => {
  describe('empty state', () => {
    it('shows no runner message when rows are empty', () => {
      const summary = makeSummary({ rows: [] })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText('No runner')).toBeInTheDocument()
    })

    it('shows startup command hint when no runners connected', () => {
      const summary = makeSummary({ rows: [] })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
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
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText('Runner stale/offline')).toBeInTheDocument()
    })

    it('shows startup hint for stale/offline state', () => {
      const rows = [makeRow({ status: 'offline', connectionState: 'disconnected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: false,
      })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText(RUNNER_START_HINT)).toBeInTheDocument()
    })

    it('links to activity page in stale/offline state', () => {
      const rows = [makeRow({ status: 'stale', connectionState: 'disconnected' })]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: false,
      })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
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
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
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
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
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
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText('2 runners ready')).toBeInTheDocument()
    })
  })

  describe('connected busy state', () => {
    it('shows runner busy badge when connected busy', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf1', workType: 'workflow' },
        }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 0,
        connectedBusyCount: 1,
      })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText('Runner busy')).toBeInTheDocument()
    })

    it('shows running workflow count for busy runners', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf1', workType: 'workflow' },
        }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 0,
        connectedBusyCount: 1,
      })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText('1 running workflow')).toBeInTheDocument()
    })

    it('shows running workflows text for multiple busy runners', () => {
      const rows = [
        makeRow({
          id: 'r1',
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf1', workType: 'workflow' },
        }),
        makeRow({
          id: 'r2',
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w2', workflowRunId: 'wf2', workType: 'workflow' },
        }),
      ]
      const summary = makeSummary({
        rows,
        hasConnectedCapacity: true,
        connectedIdleCount: 0,
        connectedBusyCount: 2,
      })
      render(
        <MemoryRouter>
          <RunnerSummary summary={summary} />
        </MemoryRouter>
      )
      expect(screen.getByText('2 running workflows')).toBeInTheDocument()
    })
  })
})

describe('RunnerList UI', () => {
  describe('empty state', () => {
    it('shows no runners connected message', () => {
      render(<RunnerList rows={[]} />)
      expect(screen.getByText('No runners connected')).toBeInTheDocument()
    })

    it('shows startup command hint in empty state', () => {
      render(<RunnerList rows={[]} />)
      expect(screen.getByText(/Start a runner:/)).toBeInTheDocument()
      expect(screen.getByText(RUNNER_START_HINT_LIST)).toBeInTheDocument()
    })

    it('does not render a misleading settings manage action on the card', () => {
      render(
        <MemoryRouter>
          <RunnerListCard />
        </MemoryRouter>
      )

      expect(screen.getByText('Runners')).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Manage' })).not.toBeInTheDocument()
    })
  })

  describe('idle runner rendering', () => {
    it('shows runner id', () => {
      const rows = [makeRow({ id: 'runner-455532', status: 'idle', connectionState: 'connected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('runner-455532')).toBeInTheDocument()
    })

    it('shows runner kind', () => {
      const rows = [makeRow({ kind: 'external', status: 'idle', connectionState: 'connected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('external')).toBeInTheDocument()
    })

    it('shows runner hostname', () => {
      const rows = [makeRow({ hostname: 'devbox', status: 'idle', connectionState: 'connected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('devbox')).toBeInTheDocument()
    })

    it('shows global scope badge', () => {
      const rows = [makeRow({ scope: { type: 'global' }, status: 'idle', connectionState: 'connected' })]
      render(<RunnerList rows={rows} />)
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
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('My Project')).toBeInTheDocument()
    })

    it('shows idle status badge', () => {
      const rows = [makeRow({ status: 'idle', connectionState: 'connected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('idle')).toBeInTheDocument()
    })

    it('shows heartbeat information when available', () => {
      vi.setSystemTime(new Date('2026-01-01T12:02:00Z'))
      const heartbeatTime = '2026-01-01T12:00:00Z'
      const rows = [makeRow({ lastHeartbeatAt: heartbeatTime, status: 'idle', connectionState: 'connected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('heartbeat fresh: 2m ago')).toBeInTheDocument()
      expect(screen.getByText(/last heartbeat:/)).toBeInTheDocument()
      vi.useRealTimers()
    })

    it('shows connected connection state', () => {
      const rows = [makeRow({ connectionState: 'connected', status: 'idle' })]
      render(<RunnerList rows={rows} />)
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
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('2 models: openai/gpt-4.5, anthropic/claude-3')).toBeInTheDocument()
    })

    it('shows runner capabilities', () => {
      const rows = [
        makeRow({
          capabilities: ['workflow', 'workspace-query'],
          status: 'idle',
          connectionState: 'connected',
        }),
      ]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('capabilities: workflow, workspace-query')).toBeInTheDocument()
    })
  })

  describe('busy runner rendering', () => {
    it('shows busy status badge', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf1' },
        }),
      ]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('busy')).toBeInTheDocument()
    })

    it('shows active work reference with workflow run id', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf-123' },
        }),
      ]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText(/wf-123/)).toBeInTheDocument()
    })

    it('shows active work title when available', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf1', title: 'Fix login bug' },
        }),
      ]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText(/Fix login bug/)).toBeInTheDocument()
    })

    it('shows work type when title is not available', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          activeWork: { workId: 'w1', workflowRunId: 'wf1', workType: 'workflow' },
        }),
      ]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('workflow (wf1)')).toBeInTheDocument()
    })

    it('shows capacity slots when available', () => {
      const rows = [
        makeRow({
          status: 'busy',
          connectionState: 'connected',
          capacity: { usedSlots: 1, totalSlots: 2 },
          activeWork: { workId: 'w1', workflowRunId: 'wf1' },
        }),
      ]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('1/2 slots')).toBeInTheDocument()
    })
  })

  describe('stale/offline runner rendering', () => {
    it('shows stale status badge', () => {
      const rows = [makeRow({ status: 'stale', connectionState: null })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('stale')).toBeInTheDocument()
    })

    it('shows offline status badge', () => {
      const rows = [makeRow({ status: 'offline', connectionState: 'disconnected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('offline')).toBeInTheDocument()
    })

    it('shows hostname for stale runner', () => {
      const rows = [makeRow({ hostname: 'old-host', status: 'stale', connectionState: 'disconnected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('old-host')).toBeInTheDocument()
    })

    it('shows last heartbeat for stale runner', () => {
      vi.setSystemTime(new Date('2026-01-01T12:10:00Z'))
      const rows = [makeRow({ lastHeartbeatAt: '2026-01-01T12:00:00Z', status: 'stale', connectionState: 'disconnected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('heartbeat stale: 10m ago')).toBeInTheDocument()
      expect(screen.getByText(/last heartbeat:/)).toBeInTheDocument()
      vi.useRealTimers()
    })

    it('shows explicit heartbeat diagnostic for offline runner', () => {
      vi.setSystemTime(new Date('2026-01-01T14:00:00Z'))
      const rows = [makeRow({ lastHeartbeatAt: '2026-01-01T12:00:00Z', status: 'offline', connectionState: 'disconnected' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('heartbeat offline: 2h ago')).toBeInTheDocument()
      expect(screen.getByText(/last heartbeat:/)).toBeInTheDocument()
      vi.useRealTimers()
    })

    it('shows disconnected connection state', () => {
      const rows = [makeRow({ connectionState: 'disconnected', status: 'stale' })]
      render(<RunnerList rows={rows} />)
      expect(screen.getByText('disconnected')).toBeInTheDocument()
    })
  })
})
