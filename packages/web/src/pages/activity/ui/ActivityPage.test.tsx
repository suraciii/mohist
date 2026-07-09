// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { server, useMswServer } from '../../../../tests/support/msw'
import { TEST_PROJECT } from '../../../../tests/test-utils'
import { ActivityPage } from './ActivityPage'
import type { RunnerStatusRow } from '../../../entities/runner/model/types'
import type { AgentActivity } from '../../../entities/agent/model/types'

useMswServer()

const PROJECT_SEGMENT = encodeURIComponent(TEST_PROJECT.name)
const RUNNERS_PATH = '*/api/projects/:projectId/runners'
const ACTIVITY_PATH = '*/api/projects/:projectId/agent/activity'

const EMPTY_AGENT_ACTIVITY: AgentActivity = {
  summary: { active: 0, waiting: 0, completed: 0, failed: 0, slots: { active: 0, max: 0 } },
  sessions: [],
  waiting: [],
}

function mockRunners(rows: RunnerStatusRow[]) {
  server.use(http.get(RUNNERS_PATH, () => HttpResponse.json({ success: true, data: { runners: rows } })))
}

function makeRow(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
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

beforeEach(() => {
  mockRunners([])
  server.use(http.get(ACTIVITY_PATH, () => HttpResponse.json({ success: true, data: EMPTY_AGENT_ACTIVITY })))
})

afterEach(() => {
  cleanup()
  window.localStorage.clear()
})

describe('ActivityPage', () => {
  describe('delegation to the Runners page', () => {
    it('does not render the embedded runner list section', async () => {
      mockRunners([makeRow({ id: 'r1', status: 'idle' })])

      renderWith()

      await waitFor(() => {
        expect(screen.queryByTestId('runners-empty-state')).not.toBeInTheDocument()
      })
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
      expect(link).toHaveAttribute('href', `/${PROJECT_SEGMENT}/runners`)
    })

    it('keeps the runner overview badge in the status bar when runners are idle', async () => {
      mockRunners([makeRow({ status: 'idle' })])

      renderWith()

      const badge = await waitFor(() => getRunnerBadgeButton())
      expect(badge.textContent).toMatch(/Runner idle/)
      expect(badge.textContent).toMatch(/1 runner ready/)
    })

    it('keeps the runner overview badge when capacity is missing (stale/offline)', async () => {
      mockRunners([makeRow({ status: 'stale' })])

      renderWith()

      const badge = await waitFor(() => getRunnerBadgeButton())
      expect(badge.textContent).toMatch(/Runner stale\/offline/)
    })

    it('keeps the runner overview badge when runners are busy', async () => {
      mockRunners([makeRow({ status: 'busy' }), makeRow({ id: 'r2', status: 'busy' })])

      renderWith()

      const badge = await waitFor(() => getRunnerBadgeButton())
      expect(badge.textContent).toMatch(/Runner busy/)
      expect(badge.textContent).toMatch(/2 running workflows/)
    })
  })

  describe('runner overview badge navigation target', () => {
    it('navigates to /runners when the idle badge is activated', async () => {
      mockRunners([makeRow({ status: 'idle' })])

      renderWith()
      const badge = await waitFor(() => getRunnerBadgeButton())
      expect(badge.textContent).toMatch(/Runner idle/)

      fireEvent.click(badge)

      expect(screen.getByTestId('route-pathname').textContent).toBe(`/${PROJECT_SEGMENT}/runners`)
    })

    it('navigates to /runners when the busy badge is activated', async () => {
      mockRunners([makeRow({ status: 'busy' })])

      renderWith()
      const badge = await waitFor(() => getRunnerBadgeButton())
      expect(badge.textContent).toMatch(/Runner busy/)

      fireEvent.click(badge)

      expect(screen.getByTestId('route-pathname').textContent).toBe(`/${PROJECT_SEGMENT}/runners`)
    })

    it('navigates to /runners when the stale/offline badge is activated', async () => {
      mockRunners([makeRow({ status: 'stale' })])

      renderWith()
      const badge = await waitFor(() => getRunnerBadgeButton())
      expect(badge.textContent).toMatch(/Runner stale\/offline/)

      fireEvent.click(badge)

      expect(screen.getByTestId('route-pathname').textContent).toBe(`/${PROJECT_SEGMENT}/runners`)
    })

    it('navigates to /runners (not /activity) when any badge is activated', async () => {
      mockRunners([makeRow({ status: 'idle' })])

      renderWith()
      fireEvent.click(await waitFor(() => getRunnerBadgeButton()))

      const pathname = screen.getByTestId('route-pathname').textContent
      expect(pathname).not.toBe(`/${PROJECT_SEGMENT}/activity`)
      expect(pathname).toBe(`/${PROJECT_SEGMENT}/runners`)
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
