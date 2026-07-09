// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { toast } from 'sonner'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import type { RunnerStatusRow } from '../../../entities/runner'
import { RunnerDetailPage } from './RunnerDetailPage'

const mocks = vi.hoisted(() => ({
  useRunner: vi.fn(),
  useUpdateRunnerSlots: vi.fn(),
}))

vi.mock('../../../entities/runner', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/runner')>()
  return {
    ...actual,
    useRunner: mocks.useRunner,
    useUpdateRunnerSlots: mocks.useUpdateRunnerSlots,
  }
})

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

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[TEST_PROJECT]} initialProjectId={TEST_PROJECT.id}>
        <MemoryRouter initialEntries={['/mohist-local/runners/runner-7']}>
          <Routes>
            <Route path="/:projectName/runners/:runnerId" element={<RunnerDetailPage />} />
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
    vi.clearAllMocks()
    mocks.useUpdateRunnerSlots.mockReturnValue({ mutate: vi.fn(), isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  describe('loading state', () => {
    it('shows a loading state while the runner is being fetched', () => {
      mocks.useRunner.mockReturnValue({ data: undefined, isLoading: true, error: null })
      renderPage()
      expect(screen.getByTestId('runner-detail-loading')).toBeInTheDocument()
    })
  })

  describe('not-found state', () => {
    it('surfaces a clear not-found state when the runner id 404s', async () => {
      const error = Object.assign(new Error('Runner \'runner-7\' not found'), { status: 404 })
      mocks.useRunner.mockReturnValue({ data: undefined, isLoading: false, error })
      renderPage()
      await waitFor(() => {
        expect(screen.getByTestId('runner-not-found')).toBeInTheDocument()
      })
      expect(screen.queryByTestId('runner-detail-id-cell')).not.toBeInTheDocument()
      expect(screen.queryByTestId('runner-detail-active-works-list')).not.toBeInTheDocument()
    })

    it('shows a generic error card for non-404 failures', () => {
      const error = Object.assign(new Error('boom'), { status: 500 })
      mocks.useRunner.mockReturnValue({ data: undefined, isLoading: false, error })
      renderPage()
      expect(screen.getByTestId('runner-detail-error')).toBeInTheDocument()
      expect(screen.getByText('boom')).toBeInTheDocument()
    })
  })

  describe('full detail rendering', () => {
    it('renders identity, capabilities, active works, and health metrics', () => {
      const runner = makeRunner({
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
            issue: { projectId: 'proj-1', issueId: 'issue-42', issueNumber: 42 },
          },
        ],
      })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      expect(screen.getByTestId('runner-detail-id')).toHaveTextContent('runner-7')
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

    it('renders every active work as an independent row (3 works → 3 rows)', () => {
      const runner = makeRunner({
        status: 'busy',
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-a',
            workType: 'workflow',
            stage: 'plan',
            title: 'Work A',
            issue: { projectId: 'proj-1', issueId: 'issue-1', issueNumber: 1 },
          },
          {
            workId: 'w2',
            ownerKind: 'workflow',
            ownerId: 'wf-b',
            workType: 'workflow',
            stage: 'build',
            title: 'Work B',
            issue: { projectId: 'proj-1', issueId: 'issue-2', issueNumber: 2 },
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
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      const list = screen.getByTestId('runner-detail-active-works-list')
      expect(list).toHaveAttribute('data-count', '3')
      const rows = within(list).getAllByTestId('active-work-detail-row')
      expect(rows).toHaveLength(3)
      expect(within(rows[0]).getByText('Work A')).toBeInTheDocument()
      expect(within(rows[1]).getByText('Work B')).toBeInTheDocument()
      expect(within(rows[2]).getByText('Work C')).toBeInTheDocument()
    })

    it('renders a navigable issue link when an active work carries an issue ref', () => {
      const runner = makeRunner({
        status: 'busy',
        activeWorks: [
          {
            workId: 'w1',
            ownerKind: 'workflow',
            ownerId: 'wf-1',
            workType: 'workflow',
            title: 'Add dark mode',
            issue: { projectId: 'proj-1', issueId: 'issue-42', issueNumber: 42 },
          },
        ],
      })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      const link = screen.getByTestId('active-work-issue-link')
      expect(link).toHaveAttribute('href', '/mohist-local/issues/42')
      expect(link).toHaveTextContent('issue #42')
    })

    it('renders stage and title without a broken or placeholder link when issue is absent', () => {
      const runner = makeRunner({
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
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      const row = screen.getByTestId('active-work-detail-row')
      expect(within(row).getByText('No-issue work')).toBeInTheDocument()
      expect(within(row).getByText(/stage: build/)).toBeInTheDocument()
      expect(within(row).queryByTestId('active-work-issue-link')).not.toBeInTheDocument()
    })

    it('shows an explicit "no active works" message when activeWorks is empty', () => {
      const runner = makeRunner({ status: 'idle', activeWorks: [] })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()
      expect(screen.getByTestId('runner-detail-no-active-works')).toBeInTheDocument()
    })
  })

  describe('slots editor', () => {
    it('renders the input with the maxSlots value', () => {
      const runner = makeRunner({ maxWorkflowSlots: 2 })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()
      const input = screen.getByTestId('slots-editor-input') as HTMLInputElement
      expect(input.value).toBe('2')
    })

    it('renders "—" when maxSlots is null', () => {
      const runner = makeRunner({ maxWorkflowSlots: undefined, capacity: undefined })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()
      expect(screen.getByTestId('runner-detail-max-slots')).toHaveTextContent('—')
      expect(screen.queryByTestId('slots-editor')).not.toBeInTheDocument()
    })

    it('calls mutate on increase button click', () => {
      const mutate = vi.fn()
      mocks.useUpdateRunnerSlots.mockReturnValue({ mutate, isPending: false })
      const runner = makeRunner({ maxWorkflowSlots: 2 })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      fireEvent.click(screen.getByTestId('slots-editor-increase'))
      expect(mutate).toHaveBeenCalledWith(
        { runnerId: 'runner-7', slots: 3 },
        expect.any(Object),
      )
    })

    it('calls mutate on decrease button click', () => {
      const mutate = vi.fn()
      mocks.useUpdateRunnerSlots.mockReturnValue({ mutate, isPending: false })
      const runner = makeRunner({ maxWorkflowSlots: 2 })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      fireEvent.click(screen.getByTestId('slots-editor-decrease'))
      expect(mutate).toHaveBeenCalledWith(
        { runnerId: 'runner-7', slots: 1 },
        expect.any(Object),
      )
    })

    it('disables decrease button at value 1', () => {
      mocks.useUpdateRunnerSlots.mockReturnValue({ mutate: vi.fn(), isPending: false })
      const runner = makeRunner({ maxWorkflowSlots: 1 })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      expect(screen.getByTestId('slots-editor-decrease')).toBeDisabled()
    })

    it('shows saving indicator when mutation is pending', () => {
      mocks.useUpdateRunnerSlots.mockReturnValue({ mutate: vi.fn(), isPending: true })
      const runner = makeRunner({ maxWorkflowSlots: 2 })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      expect(screen.getByTestId('slots-editor-saving')).toBeInTheDocument()
    })

    it('shows toast on mutation error', () => {
      vi.mocked(toast.error).mockClear()
      const mutate = vi.fn().mockImplementation((_vars, { onError }: { onError: (err: Error) => void }) => {
        onError(new Error('boom'))
      })
      mocks.useUpdateRunnerSlots.mockReturnValue({ mutate, isPending: false })
      const runner = makeRunner({ maxWorkflowSlots: 2 })
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()

      fireEvent.click(screen.getByTestId('slots-editor-increase'))
      expect(toast.error).toHaveBeenCalledWith('Failed to update slots: boom')
    })
  })

  describe('navigation', () => {
    it('navigates back to the activity page via the back button', () => {
      const runner = makeRunner()
      mocks.useRunner.mockReturnValue({ data: runner, isLoading: false, error: null })
      renderPage()
      fireEvent.click(screen.getByTestId('runner-detail-back'))
      expect(screen.getByText('Activity')).toBeInTheDocument()
    })

    it('404 not-found state offers a back-to-activity action', () => {
      const error = Object.assign(new Error('Runner \'runner-7\' not found'), { status: 404 })
      mocks.useRunner.mockReturnValue({ data: undefined, isLoading: false, error })
      renderPage()
      expect(screen.getByTestId('runner-not-found-back')).toBeInTheDocument()
    })
  })
})