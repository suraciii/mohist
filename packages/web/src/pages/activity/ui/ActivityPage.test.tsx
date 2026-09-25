import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { RunnerSummary } from '../../../widgets/runner-status'
import { ActivityPage, type ActivityPageDependencies } from './ActivityPage'
import { deriveRunnerSummary, type RunnerStatusEntry } from '../../../entities/runner'

const PROJECT = { id: 'project-1', name: 'Payments', createdAt: '', updatedAt: '', repositories: [] }

function makeRunner(overrides: Partial<RunnerStatusEntry> = {}): RunnerStatusEntry {
  return {
    identity: {
      id: 'runner-1',
      hostname: 'host',
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
    capacity: { used: 0, total: 2 },
    activeWorks: [],
    drain: null,
    nextActions: [],
    ...overrides,
  }
}

const dependencies: ActivityPageDependencies = {
  activityEventsHook: () => ({ events: [], isLoading: false, isError: false }),
  activityCardsHook: () => ({
    activeCards: [],
    activeCardByIssueNumber: new Map(),
    recentCards: [],
    waitingCards: [],
    statusCounts: { active: 0, waiting: 0, completed: 0, failed: 0 },
    slotUsage: { active: 0, max: 2 },
    isLoading: false,
    isError: false,
  }),
  activityUsageSnapshotHook: () => ({
    inputTokens: 0,
    outputTokens: 0,
    totalTokens: 0,
    costAmount: 0,
    costCurrency: null,
  }),
  RunnerSummaryBadge: ({ targetPath }) => (
    <RunnerSummary
      summary={{
        ...deriveRunnerSummary([makeRunner()]),
        inventory: { state: 'ready', nextActions: [] },
      }}
      targetPath={targetPath}
    />
  ),
}

afterEach(cleanup)

describe('Activity Runner navigation', () => {
  it('uses the global Runner route from Activity', () => {
    render(
      <ProjectProvider initialProjectId={PROJECT.id} initialProjects={[PROJECT]}>
        <MemoryRouter initialEntries={['/Payments/activity']}>
          <Routes>
            <Route
              path="/:projectName/activity"
              element={<ActivityPage dependencies={dependencies} now={Date.parse('2026-01-01T00:00:00Z')} />}
            />
            <Route path="/runners" element={<div>Runners</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>,
    )
    expect(screen.getByTestId('activity-runners-link')).toHaveAttribute('href', '/runners?from=activity')
    expect(screen.getByTestId('runner-summary-button')).toBeInTheDocument()
    expect(screen.getByTestId('runner-summary-button')).not.toHaveTextContent(/idle|busy/i)
  })
})

describe('Activity status summary', () => {
  it('counts queued work apart from confirmed running work', () => {
    render(
      <ProjectProvider initialProjectId={PROJECT.id} initialProjects={[PROJECT]}>
        <MemoryRouter initialEntries={['/Payments/activity']}>
          <Routes>
            <Route
              path="/:projectName/activity"
              element={
                <ActivityPage
                  dependencies={{
                    ...dependencies,
                    activityCardsHook: () => ({
                      ...dependencies.activityCardsHook(),
                      statusCounts: { active: 1, queued: 2, waiting: 0, completed: 0, failed: 0 },
                    }),
                  }}
                  now={Date.parse('2026-01-01T00:00:00Z')}
                />
              }
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>,
    )

    expect(screen.getByTestId('status-bar-active')).toHaveTextContent('Active:1')
    expect(screen.getByTestId('status-bar-queued')).toHaveTextContent('Queued:2')
  })
})
