import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { RunnerStatusEntry, RunnerStatusListResponse } from '../../../entities/runner/model/types'
import { useRunners } from '../../../entities/runner'
import { RunnersPage } from './RunnersPage'

let currentSnapshot: RunnerStatusListResponse = {
  observedAt: '2026-01-01T00:00:00Z',
  inventory: { state: 'ready', nextActions: [] },
  runners: [],
}

const runnersHook: typeof useRunners = () =>
  ({
    data: currentSnapshot,
    isLoading: false,
    isError: false,
    error: null,
    refetch: async () => ({}) as never,
  }) as unknown as ReturnType<typeof useRunners>

function makeRow(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-test',
      hostname: 'test-host',
      kind: 'external',
      component: 'mohist-runner',
      sourceRevision: null,
      releaseId: null,
      generation: 1,
    },
    presence: { state: 'online', lastObservedAt: '2026-01-01T00:00:00Z' },
    control: { state: 'connected', generation: 'connection-1' },
    admission: { state: 'ready', reasonCodes: [] },
    capabilities: [],
    runtimes: [],
    capacity: { used: 0, total: 2 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <ProjectProvider initialProjects={[]} initialProjectId={null}>
        <MemoryRouter>
          <RunnersPage runnersHook={runnersHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  currentSnapshot = { observedAt: '2026-01-01T00:00:00Z', inventory: { state: 'ready', nextActions: [] }, runners: [] }
})

describe('RunnersPage', () => {
  it('loads globally with no selected Project or Projects', () => {
    currentSnapshot = {
      observedAt: '2026-01-01T00:00:00Z',
      inventory: { state: 'ready', nextActions: [] },
      runners: [makeRow({ identity: { ...makeRow().identity, id: 'global-runner' } })],
    }
    renderPage()
    expect(screen.getByTestId('runners-page')).toBeInTheDocument()
    expect(screen.getByText('global-runner')).toBeInTheDocument()
    expect(screen.queryByTestId('runners-no-project-state')).not.toBeInTheDocument()
    expect(screen.queryByText(/This project|Global|All/)).not.toBeInTheDocument()
  })

  it('renders first-install guidance from inventory and no unsupported start command', () => {
    currentSnapshot = {
      observedAt: '2026-01-01T00:00:00Z',
      inventory: {
        state: 'first-install',
        nextActions: [
          {
            code: 'install-runner',
            message: 'Install and start the first Runner.',
            command: 'mo install runner --repo-root <path>',
          },
        ],
      },
      runners: [],
    }
    renderPage()
    expect(screen.getByText('No Runner definitions')).toBeInTheDocument()
    expect(screen.getByText('Install and start the first Runner.')).toBeInTheDocument()
    expect(screen.getByText('mo install runner --repo-root <path>')).toBeInTheDocument()
    expect(screen.queryByText('mo service start runner')).not.toBeInTheDocument()
  })

  it('summarizes admission facts rather than idle/busy health', () => {
    currentSnapshot = {
      observedAt: '2026-01-01T00:00:00Z',
      inventory: { state: 'ready', nextActions: [] },
      runners: [
        makeRow(),
        makeRow({
          identity: { ...makeRow().identity, id: 'blocked' },
          admission: { state: 'blocked', reasonCodes: ['runtime-witness-missing'] },
          activeWorks: [
            {
              workId: 'w',
              ownerKind: 'agent-job',
              ownerId: 'job-1',
              workType: 'agent-job',
              stage: null,
              title: null,
              issue: null,
            },
          ],
        }),
      ],
    }
    renderPage()
    expect(screen.getByText('1 admission ready')).toBeInTheDocument()
    expect(screen.getByText('1 admission blocked')).toBeInTheDocument()
    expect(screen.getByText('1 active work')).toBeInTheDocument()
    expect(screen.queryByText(/idle|busy/i)).not.toBeInTheDocument()
  })
})
