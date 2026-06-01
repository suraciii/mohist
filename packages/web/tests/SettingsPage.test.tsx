import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { baseRender, screen, fireEvent, waitFor } from './test-utils'
import { SettingsPage } from '../src/pages/settings/ui/SettingsPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import React from 'react'

function createSystemInfo(overrides: Partial<any> = {}) {
  return {
    running: { version: '1.2.3', gitHash: 'abcdef1234567890', startedAt: '2026-06-01T00:00:00Z' },
    source: { path: '/repo', branch: 'main', head: 'fedcba0987654321', dirty: false },
    install: { mode: 'local-source', serviceManager: 'systemd-user', serverUnit: 'mohist.service', runnerUnit: 'mohist-runner.service' },
    update: { status: 'update-available', available: true, reason: 'A newer source version is available' },
    services: { server: 'active', runner: 'active' },
    paths: { db: '/db', config: '/config', logs: '/logs', opencode: '/opencode' },
    ...overrides,
  }
}

vi.mock('../src/entities/settings/api/queries', async () => {
  const actual = await import('../src/entities/settings/api/queries')
  return {
    ...actual,
    useOpencodeRuntime: vi.fn(),
    useOpencodeModel: vi.fn(),
    useUpdateOpencodeModel: vi.fn(),
    useStageModels: vi.fn(),
    useSetStageModels: vi.fn(),
    useAvailableModelIds: vi.fn(),
    useLogLevel: vi.fn(),
    useSetLogLevel: vi.fn(),
    useSystemInfo: vi.fn(),
    useSystemUpdate: vi.fn(),
    useSystemUpdateStatus: vi.fn(),
    useAgentRuntime: vi.fn(),
    useSetAgentRuntime: vi.fn(),
  }
})

const {
  useOpencodeRuntime,
  useOpencodeModel,
  useUpdateOpencodeModel,
  useStageModels,
  useSetStageModels,
  useAvailableModelIds,
  useLogLevel,
  useSetLogLevel,
  useSystemInfo,
  useSystemUpdate,
  useSystemUpdateStatus,
  useAgentRuntime,
  useSetAgentRuntime,
} = await import('../src/entities/settings/api/queries')

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

function renderWithQueryClient(
  ui: React.ReactElement,
  initialEntries = ['/settings/ai'],
) {
  const queryClient = createMockQueryClient()
  return baseRender(
    <MemoryRouter initialEntries={initialEntries}>
      <QueryClientProvider client={queryClient}>
        <Routes>
          <Route path="/settings/:section" element={ui} />
          <Route path="/settings" element={ui} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  ;(useOpencodeRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { mode: 'local-opencode', command: 'opencode', model: null, note: 'external coder agent' },
    isLoading: false,
    error: null,
  })
  ;(useOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { model: null },
    isLoading: false,
    error: null,
  })
  ;(useUpdateOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useStageModels as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { stageModels: null },
    isLoading: false,
    error: null,
  })
  ;(useSetStageModels as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useAvailableModelIds as ReturnType<typeof vi.fn>).mockReturnValue({
    data: ['openai/gpt-4', 'anthropic/claude-3-opus'],
    isLoading: false,
    error: null,
  })
  ;(useLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { level: 'INFO' },
    isLoading: false,
    error: null,
  })
  ;(useSetLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
    data: null,
    isLoading: false,
    error: null,
  })
  ;(useSystemUpdate as ReturnType<typeof vi.fn>).mockReturnValue({
    mutateAsync: vi.fn(),
    isPending: false,
  })
  ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
    data: null,
    isLoading: false,
    error: null,
  })
  ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
    data: {
      timeout: 1800000,
      stageTimeout: 3600000,
      taskTimeout: 600000,
      maxConcurrent: 8,
      maxGracePeriods: 2,
      pollInterval: 30000,
    },
    isLoading: false,
    error: null,
  })
  ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
})

afterEach(() => {
  vi.useRealTimers()
})

describe('SettingsPage', () => {
  describe('Coder Agent Tab', () => {
    it('should render Coder Agent tab by default', () => {
      renderWithQueryClient(<SettingsPage />)

      expect(screen.getByRole('tab', { name: 'Coder Agent' })).toBeInTheDocument()
      expect(screen.getByRole('tab', { name: 'Runtime' })).toBeInTheDocument()
      expect(screen.getByRole('tab', { name: 'System' })).toBeInTheDocument()
      expect(screen.getAllByRole('heading', { name: 'External Coder Agent' })[0]).toBeInTheDocument()
    })

    it('should display opencode model count', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getAllByText('2')[0]).toBeInTheDocument()
    })

    it('should explain that providers are external to Mohist', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getAllByText(/Mohist does not configure AI providers/i)[0]).toBeInTheDocument()
    })
  })

  describe('Tab switching', () => {
    it('should switch to Runtime tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      fireEvent.click(screen.getByRole('tab', { name: 'Runtime' }))

      expect(screen.getAllByRole('heading', { name: 'Coder Agent Runtime' })[0]).toBeInTheDocument()
    })

    it('should switch back to Coder Agent tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      fireEvent.click(screen.getByRole('tab', { name: 'Runtime' }))
      expect(screen.getAllByRole('heading', { name: 'Coder Agent Runtime' })[0]).toBeInTheDocument()

      fireEvent.click(screen.getByRole('tab', { name: 'Coder Agent' }))
      expect(screen.getAllByRole('heading', { name: 'External Coder Agent' })[0]).toBeInTheDocument()
    })

    it('should highlight active tab', () => {
      renderWithQueryClient(<SettingsPage />)

      const aiTab = screen.getByRole('tab', { name: 'Coder Agent' })
      const agentTab = screen.getByRole('tab', { name: 'Runtime' })

      expect(aiTab).toHaveAttribute('aria-selected', 'true')
      expect(agentTab).toHaveAttribute('aria-selected', 'false')

      fireEvent.click(agentTab)

      expect(agentTab).toHaveAttribute('aria-selected', 'true')
      expect(aiTab).toHaveAttribute('aria-selected', 'false')
    })
  })

  describe('Loading state', () => {
    it('should display loading skeletons when opencode runtime is loading', () => {
      ;(useOpencodeRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: true,
        error: null,
      })

      const { container } = renderWithQueryClient(<SettingsPage />)

      const skeletons = container.querySelectorAll('.animate-pulse')
      expect(skeletons.length).toBeGreaterThan(0)
    })
  })

  describe('Error state', () => {
    it('should display error message when opencode runtime query fails', () => {
      ;(useOpencodeRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: false,
        error: new Error('Failed to load opencode runtime'),
      })

      renderWithQueryClient(<SettingsPage />)

      expect(screen.getAllByText(/Failed to load opencode runtime/i)[0]).toBeInTheDocument()
    })
  })

  describe('System tab', () => {
    function createUpdateJob(overrides: Partial<any> = {}) {
      return {
        jobId: 'job-1',
        status: 'waiting-for-reconnect',
        stage: 'Waiting for reconnect',
        updateAvailable: true,
        runningGitHash: 'abcdef1234567890',
        sourceHead: 'fedcba0987654321',
        sourcePath: '/repo',
        serverUnit: 'mohist.service',
        runnerUnit: 'mohist-runner.service',
        reason: 'Waiting for restart',
        logs: [],
        createdAt: '2026-06-01T00:00:00Z',
        updatedAt: '2026-06-01T00:00:01Z',
        completedAt: null,
        ...overrides,
      }
    }

    it('renders typed runtime and source fields', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText('Running version').length).toBeGreaterThan(0)
      expect(screen.getAllByText('1.2.3').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Running git hash').length).toBeGreaterThan(0)
      expect(screen.getAllByText('abcdef12').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Path').length).toBeGreaterThan(0)
      expect(screen.getAllByText('/repo').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Started at').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Detail').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Service manager').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Server unit').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Runner unit').length).toBeGreaterThan(0)
      expect(screen.getAllByText(/fedcba09 \(fedcba0987654321\)/i).length).toBeGreaterThan(0)
      expect(screen.getAllByText('Mode').length).toBeGreaterThan(0)
      expect(screen.getAllByText('local-source').length).toBeGreaterThan(0)
      expect(screen.getAllByRole('button', { name: /Update & Restart/i }).length).toBeGreaterThan(0)
      expect(screen.queryByText(/Rebuild & Restart/i)).not.toBeInTheDocument()
    })

    it('hides update action for unsupported installs and shows note', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo({
          source: { path: null, branch: null, head: null, dirty: false },
          install: { mode: 'binary', serviceManager: null, serverUnit: null, runnerUnit: null },
          update: { status: 'unsupported', available: false, reason: 'Web update is unsupported for the detected deployment' },
          services: { server: null, runner: null },
        }),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
      expect(screen.getAllByText(/Web update is unsupported/i).length).toBeGreaterThan(0)
    })

    it('renders system info error state without placeholder runtime facts', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: false,
        isError: true,
        error: new Error('system info failed'),
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText(/system info failed/i).length).toBeGreaterThan(0)
      expect(screen.queryByText('Running version')).not.toBeInTheDocument()
      expect(screen.queryByText(/Web update is unsupported/i)).not.toBeInTheDocument()
    })

    it('renders logs path and update progress label', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          logs: [
            { at: '2026-06-01T00:00:00Z', stage: 'Building', message: 'Starting update' },
            { at: '2026-06-01T00:00:01Z', stage: 'Waiting for reconnect', message: 'Server restart requested' },
          ],
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText('/logs').length).toBeGreaterThan(0)
      expect(screen.getAllByText(/Waiting for restart/i).length).toBeGreaterThan(0)
      expect(screen.getAllByText('/repo').length).toBeGreaterThan(0)
      expect(screen.getAllByText('mohist.service').length).toBeGreaterThan(0)
      expect(screen.getAllByText('mohist-runner.service').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Update log').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Starting update').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Server restart requested').length).toBeGreaterThan(0)
    })

    it('recovers persisted reconnect state after reload and polls health', async () => {
      vi.useRealTimers()
      const refetchInfo = vi.fn().mockResolvedValue(undefined)
      const refetchUpdateStatus = vi.fn().mockResolvedValue(undefined)
      const fetchMock = vi.fn().mockResolvedValue({ ok: true })
      vi.stubGlobal('fetch', fetchMock)

      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: refetchInfo,
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob() },
        isLoading: false,
        error: null,
        refetch: refetchUpdateStatus,
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText(/Waiting for reconnect/i).length).toBeGreaterThan(0)
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
      await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/health'), { timeout: 4000 })
      await waitFor(() => expect(refetchInfo).toHaveBeenCalled(), { timeout: 4000 })
      await waitFor(() => expect(refetchUpdateStatus).toHaveBeenCalled(), { timeout: 4000 })
    })

    it('starts update, shows reconnect progress, polls health, and refetches runtime info', async () => {
      vi.useRealTimers()
      const waitingJob = createUpdateJob()
      let updateStarted = false
      const mutateAsync = vi.fn().mockImplementation(async () => {
        updateStarted = true
        return { job: waitingJob }
      })
      const refetchInfo = vi.fn().mockResolvedValue(undefined)
      const refetchUpdateStatus = vi.fn().mockResolvedValue(undefined)
      const fetchMock = vi.fn().mockResolvedValue({ ok: true })
      vi.stubGlobal('fetch', fetchMock)
      let trackingEnabled = false

      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: refetchInfo,
      })
      ;(useSystemUpdate as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        isPending: false,
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockImplementation((enabled: boolean) => {
        trackingEnabled = enabled
        return {
          data: updateStarted ? { hasJob: true, job: waitingJob } : { hasJob: false, job: null },
          isLoading: false,
          error: null,
          refetch: refetchUpdateStatus,
        }
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      fireEvent.click(screen.getAllByRole('button', { name: /Update & Restart/i })[0])

      await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
      await waitFor(() => expect(trackingEnabled).toBe(true))
      await waitFor(() => expect(screen.getByText(/Waiting for reconnect/i)).toBeInTheDocument())

      await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/health'), { timeout: 4000 })
      await waitFor(() => expect(refetchInfo).toHaveBeenCalled(), { timeout: 4000 })
      await waitFor(() => expect(refetchUpdateStatus).toHaveBeenCalled(), { timeout: 4000 })
    })

    it('renders dirty-source warning and disables update action', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo({
          source: { path: '/repo', branch: 'main', head: 'fedcba0987654321', dirty: true },
          update: { status: 'dirty-source', available: true, reason: 'Source tree has uncommitted changes' },
        }),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText(/Local source has uncommitted changes/i).length).toBeGreaterThan(0)
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
    })

    it('renders recovered persisted failure message from update status', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'failed',
          stage: 'Restarting server',
          reason: 'systemctl exited with code 1',
          logs: [{ at: '2026-06-01T00:00:01Z', stage: 'Restarting server', message: 'systemctl exited with code 1' }],
          completedAt: '2026-06-01T00:00:02Z',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText(/systemctl exited with code 1/i).length).toBeGreaterThan(0)
    })

    it('renders persisted in-progress update state after reload', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'running',
          stage: 'Building',
          reason: 'Starting update',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText(/Building/i).length).toBeGreaterThan(0)
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
    })

    it('renders explicit ready state after reconnect hash match', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo({
          running: { version: '1.2.3', gitHash: 'fedcba0987654321', startedAt: '2026-06-01T00:00:00Z' },
        }),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          runningGitHash: 'fedcba0987654321',
          sourceHead: 'fedcba0987654321',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getAllByText(/Ready/i).length).toBeGreaterThan(0)
    })
  })
})
