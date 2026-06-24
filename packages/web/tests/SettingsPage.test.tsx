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
    useConfig: vi.fn(),
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
  useConfig,
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
    data: { model: null, variant: null },
    isLoading: false,
    error: null,
  })
  ;(useUpdateOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useStageModels as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { stageModels: null, stageModelVariants: null },
    isLoading: false,
    error: null,
  })
  ;(useSetStageModels as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useAvailableModelIds as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { models: ['openai/gpt-4', 'anthropic/claude-3-opus'], modelVariants: {} },
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
  ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
    data: {
      agentTimeout: 600,
      taskTimeout: 600,
      stageTimeout: 3600,
      maxConcurrentAgents: 3,
      maxGracePeriods: 3,
      pollInterval: 5000,
    },
    isLoading: false,
    error: null,
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

    it('should display opencode model count via the lightweight hint', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getAllByText(/2 models available/i)[0]).toBeInTheDocument()
    })

    it('should not render the redundant Runtime/Command/Models summary or provider note', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.queryByText('Command')).not.toBeInTheDocument()
      expect(screen.queryByText('Models')).not.toBeInTheDocument()
      expect(screen.queryByText(/Mohist does not configure AI providers/i)).not.toBeInTheDocument()
    })

    it('renders the default model trigger with the stored variant suffix when the model reports variants', () => {
      ;(useAvailableModelIds as ReturnType<typeof vi.fn>).mockReturnValue({
        data: {
          models: ['openai/gpt-4', 'anthropic/claude-3-opus'],
          modelVariants: { 'anthropic/claude-3-opus': ['low', 'medium', 'high'] },
        },
        isLoading: false,
        error: null,
      })
      ;(useOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { model: 'anthropic/claude-3-opus', variant: 'high' },
        isLoading: false,
        error: null,
      })

      renderWithQueryClient(<SettingsPage />)

      expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
      const trigger = document.getElementById('settings-default-model') as HTMLElement
      expect(trigger).toBeInTheDocument()
      expect(trigger.textContent).toContain('high')
    })

    it('does not show a variant suffix on the default model trigger when the model is not in the variants map', () => {
      ;(useAvailableModelIds as ReturnType<typeof vi.fn>).mockReturnValue({
        data: {
          models: ['openai/gpt-4', 'anthropic/claude-3-opus'],
          modelVariants: { 'anthropic/claude-3-opus': [] },
        },
        isLoading: false,
        error: null,
      })
      ;(useOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { model: 'anthropic/claude-3-opus', variant: 'high' },
        isLoading: false,
        error: null,
      })

      renderWithQueryClient(<SettingsPage />)

      expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
      const trigger = document.getElementById('settings-default-model') as HTMLElement
      expect(trigger.textContent).not.toContain('high')
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
      await waitFor(() => expect(screen.getAllByText(/Waiting for reconnect/i).length).toBeGreaterThan(0))

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

    it('renders superseded state with current runtime identity and hides active progress', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo({
          running: { version: '1.2.4', gitHash: 'newsha9876543210', startedAt: '2026-06-01T00:00:00Z' },
        }),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'superseded',
          stage: 'Waiting for reconnect',
          runningGitHash: 'abcdef1234567890',
          sourceHead: 'fedcba0987654321',
          completedAt: '2026-05-31T00:00:00Z',
          reason: 'Running git hash differs from job source HEAD',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      expect(screen.getByTestId('system-update-superseded')).toBeInTheDocument()
      expect(screen.getAllByText(/Previous update is no longer relevant/i).length).toBeGreaterThan(0)
      expect(screen.getByTestId('system-update-superseded-runtime')).toHaveTextContent('v1.2.4')
      expect(screen.getByTestId('system-update-superseded-runtime')).toHaveTextContent('newsha98')
      expect(screen.queryByTestId('system-update-progress-stages')).not.toBeInTheDocument()
      expect(screen.queryByTestId('system-update-stage-Building')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).toBeInTheDocument()
    })

    it('renders Succeeded outcome label for completed updates', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'succeeded',
          stage: 'Verifying runtime',
          outcome: 'succeeded',
          completedAt: '2026-06-01T00:00:05Z',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const outcome = screen.getByTestId('system-update-outcome')
      expect(outcome).toHaveAttribute('data-outcome', 'succeeded')
      expect(outcome).toHaveTextContent('Succeeded')
    })

    it('renders Recovered outcome label with warnings detail', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'recovered',
          stage: 'Verifying runtime',
          outcome: 'recovered',
          reason: 'Skill assets missing: managed skill manifest not found',
          completedAt: '2026-06-01T00:00:05Z',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const outcome = screen.getByTestId('system-update-outcome')
      expect(outcome).toHaveAttribute('data-outcome', 'recovered')
      expect(outcome).toHaveTextContent('Recovered with warnings')
      expect(outcome).toHaveTextContent('Skill assets missing')
    })

    it('renders Failed outcome label with unavailable capability', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'failed',
          stage: 'Restoring runner',
          outcome: 'failed',
          reason: 'Runner restore failed: systemctl could not start mohist-runner.service',
          unavailableCapability: 'runner',
          completedAt: '2026-06-01T00:00:05Z',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const outcome = screen.getByTestId('system-update-outcome')
      expect(outcome).toHaveAttribute('data-outcome', 'failed')
      expect(outcome).toHaveTextContent('Failed')
      expect(outcome).toHaveTextContent('runner')
    })

    it('renders Cancelled outcome label for interrupted updates', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'cancelled',
          stage: 'Preparing workflow runner',
          outcome: 'cancelled',
          reason: 'Update was interrupted',
          completedAt: '2026-06-01T00:00:03Z',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const outcome = screen.getByTestId('system-update-outcome')
      expect(outcome).toHaveAttribute('data-outcome', 'cancelled')
      expect(outcome).toHaveTextContent('Cancelled')
    })

    it('shows CLI-triggered update outcome persisted by the server', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo({
          running: { version: '1.2.4', gitHash: 'clioutcome123abc', startedAt: '2026-06-01T00:00:00Z' },
        }),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'succeeded',
          stage: 'Verifying runtime',
          outcome: 'succeeded',
          jobId: 'cli-update-job-001',
          sourceHead: 'clioutcome123abc',
          runningGitHash: 'clioutcome123abc',
          completedAt: '2026-06-01T00:00:10Z',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const outcome = screen.getByTestId('system-update-outcome')
      expect(outcome).toHaveAttribute('data-outcome', 'succeeded')
      expect(outcome).toHaveTextContent('Succeeded')
      expect(screen.queryByTestId('system-update-superseded-runtime')).not.toBeInTheDocument()
      expect(screen.queryByTestId('system-update-superseded')).not.toBeInTheDocument()
    })

    it('renders shared update progress stage names matching CLI labels', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useSystemUpdateStatus as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { hasJob: true, job: createUpdateJob({
          status: 'running',
          stage: 'Restoring runner',
        }) },
        isLoading: false,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const stagesContainer = screen.getByTestId('system-update-progress-stages')
      expect(stagesContainer).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Building')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Restarting server')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Waiting for reconnect')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Restoring runner')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Verifying runtime')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Restoring runner')).toHaveAttribute('data-state', 'current')
      expect(screen.getByTestId('system-update-stage-Building')).toHaveAttribute('data-state', 'done')
      expect(screen.getByTestId('system-update-stage-Verifying runtime')).toHaveAttribute('data-state', 'pending')
    })

    it('displays the actual persisted log level from the API instead of a hardcoded value', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { level: 'DEBUG' },
        isLoading: false,
        isError: false,
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const trigger = screen.getByRole('combobox')
      expect(trigger).toHaveTextContent('DEBUG')
    })

    it('renders the four supported log-level options', () => {
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const trigger = screen.getByRole('combobox')
      fireEvent.click(trigger)

      for (const level of ['DEBUG', 'INFO', 'WARN', 'ERROR']) {
        expect(screen.getByRole('option', { name: level })).toBeInTheDocument()
      }
    })

    it('persists a new log level through the config API and shows the saved value', async () => {
      vi.useRealTimers()
      const mutateAsync = vi.fn().mockResolvedValue({ level: 'ERROR' })
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { level: 'INFO' },
        isLoading: false,
        isError: false,
      })
      ;(useSetLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const trigger = screen.getByRole('combobox')
      fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
      fireEvent.mouseDown(trigger, { button: 0 })
      fireEvent.click(trigger)

      await waitFor(() => expect(screen.getByRole('option', { name: 'ERROR' })).toBeInTheDocument())
      const errorOption = screen.getByRole('option', { name: 'ERROR' })
      fireEvent.pointerDown(errorOption, { button: 0, pointerType: 'mouse' })
      fireEvent.pointerUp(errorOption, { button: 0, pointerType: 'mouse' })
      fireEvent.click(errorOption)

      await waitFor(() => expect(mutateAsync).toHaveBeenCalledWith('ERROR'))
    })

    it('surfaces a failed log-level save as a visible error and reverts the displayed value', async () => {
      vi.useRealTimers()
      const mutateAsync = vi.fn().mockRejectedValue(new Error('logLevel must be one of DEBUG, INFO, WARN, ERROR'))
      ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
        data: createSystemInfo(),
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })
      ;(useLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
        data: { level: 'INFO' },
        isLoading: false,
        isError: false,
      })
      ;(useSetLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const trigger = screen.getByRole('combobox')
      fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
      fireEvent.mouseDown(trigger, { button: 0 })
      fireEvent.click(trigger)

      await waitFor(() => expect(screen.getByRole('option', { name: 'WARN' })).toBeInTheDocument())
      const warnOption = screen.getByRole('option', { name: 'WARN' })
      fireEvent.pointerDown(warnOption, { button: 0, pointerType: 'mouse' })
      fireEvent.pointerUp(warnOption, { button: 0, pointerType: 'mouse' })
      fireEvent.click(warnOption)

      await waitFor(() => expect(mutateAsync).toHaveBeenCalledWith('WARN'))
      await waitFor(() => expect(screen.getByText(/logLevel must be one of/i)).toBeInTheDocument())
      await waitFor(() => expect(trigger).toHaveTextContent('INFO'))
    })
  })
})
