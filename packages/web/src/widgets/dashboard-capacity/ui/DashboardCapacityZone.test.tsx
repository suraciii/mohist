import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { deriveRunnerSummary, type RunnerStatusEntry, type RunnerStatusSummary } from '../../../entities/runner'
import { DashboardCapacityZone } from './DashboardCapacityZone'

function makeRunner(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-1',
      hostname: 'host-1',
      kind: 'external',
      component: null,
      sourceRevision: null,
      releaseId: null,
      generation: null,
    },
    presence: { state: 'online', lastObservedAt: null },
    control: { state: 'connected', generation: null },
    admission: { state: 'ready', reasonCodes: [] },
    capabilities: [],
    runtimes: [],
    capacity: { used: 0, total: 8 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

function renderZone(summary?: RunnerStatusSummary) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <DashboardCapacityZone runnerSummaryHook={() => summary ?? deriveRunnerSummary([])} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(cleanup)

describe('DashboardCapacityZone', () => {
  it('renders global Runner capacity with usage and link', async () => {
    renderZone(deriveRunnerSummary([makeRunner({ capacity: { used: 4, total: 8 } })]))

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
    expect(screen.getByTestId('dashboard-zone-capacity-link')).toHaveAttribute('href', '/runners')
  })

  it('marks the strip as saturated when global capacity is full', async () => {
    renderZone(deriveRunnerSummary([makeRunner({ capacity: { used: 8, total: 8 } })]))

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-zone-capacity')).toHaveAttribute('data-state', 'saturated')
    })
    expect(screen.getByTestId('dashboard-zone-capacity-count')).toHaveTextContent('8/8')
  })

  it('preserves unknown used capacity instead of implying free slots', async () => {
    renderZone(deriveRunnerSummary([makeRunner({ capacity: { used: null, total: 8 } })]))

    await waitFor(() => {
      expect(screen.getByTestId('dashboard-zone-capacity')).toHaveAttribute('data-state', 'unknown')
    })
    expect(screen.getByTestId('dashboard-zone-capacity-count')).toHaveTextContent('unknown/8')
  })

  it('renders nothing when there are no Runner definitions or configured slots', async () => {
    const { container } = renderZone()

    await waitFor(() => {
      expect(container.firstChild).toBeNull()
    })
  })
})
