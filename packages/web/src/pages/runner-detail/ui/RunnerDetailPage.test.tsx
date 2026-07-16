import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { toast } from 'sonner'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import type { RunnerStatusRow } from '../../../entities/runner'
import { ApiError } from '../../../shared/api/client'
import {
  RunnerDetailPage,
  type RunnerDetailPageDependencies,
} from './RunnerDetailPage'

const TEST_PROJECT: Project = {
  id: 'proj-1',
  name: 'mohist-local',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

function makeRunner(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-7',
    kind: 'external',
    hostname: 'host-7',
    scope: { type: 'project', projectId: 'proj-1', projectName: 'mohist-local' },
    status: 'idle',
    registeredAt: '2026-01-01T00:00:00Z',
    lastHeartbeatAt: '2026-01-01T12:00:00Z',
    connectionState: 'connected',
    capabilities: ['workflow', 'workspace-query'],
    coderModels: ['openai/gpt-4.5'],
    coderModelCount: 1,
    maxWorkflowSlots: 2,
    buildGitHash: 'abc1234',
    activeWorks: [],
    ...overrides,
  }
}

let _runnerData: RunnerStatusRow | null = null
let _runnerLoading = false
let _runnerError: Error | null = null
const slotsHandler = vi.fn(async ({
  runnerId,
  slots,
}: {
  runnerId: string
  slots: number
}) => ({ runnerId, slots }))

const runnerHook: NonNullable<RunnerDetailPageDependencies['runnerHook']> = () => ({
  data: _runnerData ?? undefined,
  isLoading: _runnerLoading,
  error: _runnerError,
}) as never

const slotsMutationHook: NonNullable<RunnerDetailPageDependencies['slotsMutationHook']> = () =>
  useMutation({ mutationFn: (variables) => slotsHandler(variables) })

const dependencies: RunnerDetailPageDependencies = {
  runnerHook,
  slotsMutationHook,
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, retryDelay: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[TEST_PROJECT]} initialProjectId={TEST_PROJECT.id}>
        <MemoryRouter initialEntries={['/mohist-local/runners/runner-7']}>
          <Routes>
            <Route path="/:projectName/runners/:runnerId" element={<RunnerDetailPage dependencies={dependencies} />} />
            <Route path="/:projectName/activity" element={<div>Activity</div>} />
            <Route path="/:projectName/issues/:number" element={<div>Issue</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('RunnerDetailPage', () => {
  beforeEach(() => {
    _runnerData = null
    _runnerLoading = false
    _runnerError = null
    vi.clearAllMocks()
    slotsHandler.mockReset()
    slotsHandler.mockImplementation(async ({ runnerId, slots }) => ({ runnerId, slots }))
  })

  afterEach(() => {
    cleanup()
  })

  describe('loading state', () => {
    it('shows a loading state while the runner is being fetched', () => {
      _runnerLoading = true
      renderPage()
      expect(screen.getByTestId('runner-detail-loading')).toBeInTheDocument()
    })
  })

  describe('not-found state', () => {
    it('surfaces a clear not-found state when the runner id 404s', async () => {
      _runnerError = new ApiError("Runner 'runner-7' not found", 404)
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('runner-not-found')).toBeInTheDocument()
      })
      expect(screen.queryByTestId('runner-detail-id-cell')).not.toBeInTheDocument()
      expect(screen.queryByTestId('runner-detail-active-works-list')).not.toBeInTheDocument()
    })

    it('shows a generic error card for non-404 failures', async () => {
      _runnerError = new ApiError('boom', 500)
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('runner-detail-error')).toBeInTheDocument()
      })
      expect(screen.getByText('boom')).toBeInTheDocument()
    })
  })

  describe('full detail rendering', () => {
    it('renders identity, capabilities, active works, and health metrics', async () => {
      _runnerData = makeRunner({
        status: 'busy',
        capacity: { usedSlots: 1, totalSlots: 2 },
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-1',
            workType: 'workflow',
            stage: 'build',
            title: 'Add dark mode',
            issue: { projectId: 'proj-1', issueNumber: 42 },
          },
        ],
      })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('runner-detail-id')).toHaveTextContent('runner-7')
      })

      expect(screen.getByTestId('runner-detail-id-cell')).toHaveTextContent('runner-7')
      expect(screen.getByTestId('runner-detail-kind')).toHaveTextContent('external')
      expect(screen.getByTestId('runner-detail-hostname')).toHaveTextContent('host-7')
      expect(screen.getByTestId('runner-detail-registered-at')).toBeInTheDocument()
      expect(screen.getByTestId('runner-detail-build-git-hash')).toHaveTextContent('abc1234')

      expect(within(screen.getByTestId('runner-detail-capability-list')).getByText('workflow')).toBeInTheDocument()
      expect(within(screen.getByTestId('runner-detail-capability-list')).getByText('workspace-query')).toBeInTheDocument()
      expect(screen.getByTestId('runner-detail-coder-models')).toHaveTextContent('openai/gpt-4.5')
      expect(screen.getByTestId('runner-detail-max-slots')).toContainElement(screen.getByTestId('slots-editor'))

      const statusBadges = screen.getAllByTestId('runner-status-badge')
      expect(statusBadges.length).toBeGreaterThanOrEqual(1)
      expect(statusBadges[0]).toHaveAttribute('data-status', 'busy')
      expect(screen.getByTestId('runner-connection-state')).toHaveAttribute('data-state', 'connected')
      expect(screen.getByTestId('runner-detail-last-heartbeat')).toBeInTheDocument()
      expect(screen.getByTestId('runner-detail-capacity')).toHaveTextContent('1/2 slots')
    })

    it('renders every active work as an independent row (3 works → 3 rows)', async () => {
      _runnerData = makeRunner({
        status: 'busy',
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-a',
            workType: 'workflow',
            stage: 'plan',
            title: 'Work A',
            issue: { projectId: 'proj-1', issueNumber: 1 },
          },
          {
            workId: 'w2',
            ownerKind: 'workflow',
            ownerId: 'wf-b',
            workType: 'workflow',
            stage: 'build',
            title: 'Work B',
            issue: { projectId: 'proj-1', issueNumber: 2 },
          },
          {
            workId: 'w3',
            ownerKind: 'agent-job',
            ownerId: 'aj-3',
            workType: 'agent-job',
            stage: 'check',
            title: 'Work C',
          },
        ],
      })
      renderPage()

      await waitFor(() => {
        const list = screen.getByTestId('runner-detail-active-works-list')
        expect(list).toHaveAttribute('data-count', '3')
      })

      const list = screen.getByTestId('runner-detail-active-works-list')
      const rows = within(list).getAllByTestId('active-work-detail-row')
      expect(rows).toHaveLength(3)
      expect(within(rows[0]).getByText('Work A')).toBeInTheDocument()
      expect(within(rows[1]).getByText('Work B')).toBeInTheDocument()
      expect(within(rows[2]).getByText('Work C')).toBeInTheDocument()
    })

    it('renders a navigable issue link when an active work carries an issue ref', async () => {
      _runnerData = makeRunner({
        status: 'busy',
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-1',
            workType: 'workflow',
            title: 'Add dark mode',
            issue: { projectId: 'proj-1', issueNumber: 42 },
          },
        ],
      })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('active-work-issue-link')).toBeInTheDocument()
      })

      const link = screen.getByTestId('active-work-issue-link')
      expect(link).toHaveAttribute('href', '/mohist-local/issues/42')
      expect(link).toHaveTextContent('issue #42')
    })

    it('renders stage and title without a broken or placeholder link when issue is absent', async () => {
      _runnerData = makeRunner({
        status: 'busy',
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-1',
            workType: 'workflow',
            stage: 'build',
            title: 'No-issue work',
          },
        ],
      })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('active-work-detail-row')).toBeInTheDocument()
      })

      const row = screen.getByTestId('active-work-detail-row')
      expect(within(row).getByText('No-issue work')).toBeInTheDocument()
      expect(within(row).getByText(/stage: build/)).toBeInTheDocument()
      expect(within(row).queryByTestId('active-work-issue-link')).not.toBeInTheDocument()
    })

    it('shows an explicit "no active works" message when activeWorks is empty', async () => {
      _runnerData = makeRunner({ status: 'idle', activeWorks: [] })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('runner-detail-no-active-works')).toBeInTheDocument()
      })
    })
  })

  describe('slots editor', () => {
    it('renders the input with the maxSlots value', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: 2 })
      renderPage()

      await waitFor(() => {
        const input = screen.getByTestId('slots-editor-input') as HTMLInputElement
        expect(input.value).toBe('2')
      })
    })

    it('renders "—" when maxSlots is null', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: undefined, capacity: undefined })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('runner-detail-max-slots')).toHaveTextContent('—')
      })
      expect(screen.queryByTestId('slots-editor')).not.toBeInTheDocument()
    })

    it('calls mutate on increase button click', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: 2 })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('slots-editor-input')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('slots-editor-increase'))

      await waitFor(() => {
        expect(slotsHandler).toHaveBeenCalled()
      })
      expect(slotsHandler).toHaveBeenCalledWith({ runnerId: 'runner-7', slots: 3 })
    })

    it('calls mutate on decrease button click', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: 2 })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('slots-editor-input')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('slots-editor-decrease'))

      await waitFor(() => {
        expect(slotsHandler).toHaveBeenCalled()
      })
      expect(slotsHandler).toHaveBeenCalledWith({ runnerId: 'runner-7', slots: 1 })
    })

    it('disables decrease button at value 1', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: 1 })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('slots-editor-decrease')).toBeInTheDocument()
      })

      expect(screen.getByTestId('slots-editor-decrease')).toBeDisabled()
    })

    it('shows saving indicator when mutation is pending', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: 2 })
      slotsHandler.mockImplementation(() => new Promise(() => {}))
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('slots-editor-increase')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('slots-editor-increase'))
      await waitFor(() => {
        expect(screen.getByTestId('slots-editor-saving')).toBeInTheDocument()
      })
    })

    it('shows toast on mutation error', async () => {
      _runnerData = makeRunner({ maxWorkflowSlots: 2 })
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('slots-editor-increase')).toBeInTheDocument()
      })

      slotsHandler.mockRejectedValueOnce(new Error('boom'))

      fireEvent.click(screen.getByTestId('slots-editor-increase'))

      await waitFor(() => {
        expect(toast.error).toHaveBeenCalledWith('Failed to update slots: boom')
      })
    })
  })

  describe('navigation', () => {
    it('navigates back to the activity page via the back button', async () => {
      _runnerData = makeRunner()
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('runner-detail-back')).toBeInTheDocument()
      })

      fireEvent.click(screen.getByTestId('runner-detail-back'))
      expect(screen.getByText('Activity')).toBeInTheDocument()
    })

    it('404 not-found state offers a back-to-activity action', async () => {
      _runnerError = new ApiError("Runner 'runner-7' not found", 404)
      renderPage()

      await waitFor(() => {
        expect(screen.getByTestId('runner-not-found-back')).toBeInTheDocument()
      })
    })
  })
})
