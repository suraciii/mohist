import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { http, HttpResponse } from 'msw'
import { baseRender, screen, fireEvent, waitFor, TEST_PROJECT } from './test-utils'
import { SettingsPage } from '../src/pages/settings/ui/SettingsPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import { ProjectProvider } from '../src/entities/project/model/ProjectContext'
import { useMswServer } from './support/msw'
import React from 'react'

let _opencodeRuntimeData: any = { mode: 'local-opencode', command: 'opencode', model: null, note: 'external coder agent' }
let _opencodeRuntimeLoading = false
let _opencodeRuntimeError: string | null = null

let _configData: Record<string, unknown> = {
  agentTimeout: 600,
  taskTimeout: 600,
  stageTimeout: 3600,
  maxConcurrentAgents: 3,
  maxGracePeriods: 3,
  pollInterval: 5000,
  logLevel: 'INFO',
}

let _workflowVariablesData: any = { vars: null, stages: null }

let _opencodeModelsData: any = { models: ['openai/gpt-4', 'anthropic/claude-3-opus'], modelVariants: {} }

let _systemInfoData: any = null
let _systemInfoLoading = false
let _systemInfoError: string | null = null

let _systemUpdateStatusData: any = null

let _setLogLevelError: string | null = null

const _healthHandler = vi.fn()

useMswServer(
  http.get('*/api/health', () => {
    _healthHandler()
    return new HttpResponse(null, { status: 200 })
  }),
  http.get('*/api/opencode/runtime', () => {
    if (_opencodeRuntimeLoading) return new Promise(() => {})
    if (_opencodeRuntimeError) {
      return HttpResponse.json({ success: false, error: _opencodeRuntimeError }, { status: 500 })
    }
    return HttpResponse.json({ success: true, data: _opencodeRuntimeData })
  }),
  http.get('*/api/config', () => HttpResponse.json({ success: true, data: _configData })),
  http.put('*/api/config/:key', async ({ params, request }) => {
    const key = params.key as string
    if (key === 'logLevel') {
      if (_setLogLevelError) {
        return HttpResponse.json({ success: false, error: _setLogLevelError }, { status: 400 })
      }
      const body = await request.json() as { value: string }
      _configData = { ..._configData, logLevel: body.value }
      return HttpResponse.json({ success: true, data: _configData })
    }
    const body = await request.json() as { value: number | string }
    _configData = { ..._configData, [key]: body.value }
    return HttpResponse.json({ success: true, data: _configData })
  }),
  http.get('*/api/projects/:projectId/variables', () =>
    HttpResponse.json({ success: true, data: _workflowVariablesData }),
  ),
  http.patch('*/api/projects/:projectId/variables', async ({ request }) => {
    const body = await request.json() as any
    if (body.vars) {
      _workflowVariablesData = { ..._workflowVariablesData, vars: { ...(_workflowVariablesData.vars || {}), ...body.vars } }
    }
    if (body.stages) {
      _workflowVariablesData = { ..._workflowVariablesData, stages: { ...(_workflowVariablesData.stages || {}), ...body.stages } }
    }
    return HttpResponse.json({ success: true, data: _workflowVariablesData })
  }),
  http.get('*/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: _opencodeModelsData }),
  ),
  http.get('*/api/system/info', () => {
    if (_systemInfoLoading) return new Promise(() => {})
    if (_systemInfoError) {
      return HttpResponse.json({ success: false, error: _systemInfoError }, { status: 500 })
    }
    return HttpResponse.json({ success: true, data: _systemInfoData })
  }),
  http.post('*/api/system/update', () =>
    HttpResponse.json({ success: true, data: { job: { jobId: 'job-1', status: 'waiting-for-reconnect' } } }),
  ),
  http.get('*/api/system/update/status', () =>
    HttpResponse.json({ success: true, data: _systemUpdateStatusData }),
  ),
  http.get('*/api/templates/system', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/workflow-templates/system', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
)

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
        <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
          <Routes>
            <Route path="/settings/:section" element={ui} />
            <Route path="/settings" element={ui} />
          </Routes>
        </ProjectProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

function renderWithoutProject(
  ui: React.ReactElement,
  initialEntries: string[] = ['/settings/repositories'],
) {
  const queryClient = createMockQueryClient()
  return baseRender(
    <MemoryRouter initialEntries={initialEntries}>
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId={null} initialProjects={[]}>
          <Routes>
            <Route path="/settings/:section" element={ui} />
            <Route path="/settings" element={ui} />
          </Routes>
        </ProjectProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  _opencodeRuntimeData = { mode: 'local-opencode', command: 'opencode', model: null, note: 'external coder agent' }
  _opencodeRuntimeLoading = false
  _opencodeRuntimeError = null
  _configData = {
    agentTimeout: 600,
    taskTimeout: 600,
    stageTimeout: 3600,
    maxConcurrentAgents: 3,
    maxGracePeriods: 3,
    pollInterval: 5000,
    logLevel: 'INFO',
  }
  _workflowVariablesData = { vars: null, stages: null }
  _opencodeModelsData = { models: ['openai/gpt-4', 'anthropic/claude-3-opus'], modelVariants: {} }
  _systemInfoData = null
  _systemInfoLoading = false
  _systemInfoError = null
  _systemUpdateStatusData = null
  _setLogLevelError = null
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('SettingsPage', () => {
  describe('Coder Agent Tab', () => {
    it('should render Coder Agent tab by default', () => {
      renderWithQueryClient(<SettingsPage />)

      expect(screen.getByRole('link', { name: 'Coder Agent' })).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Runtime' })).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'System' })).toBeInTheDocument()
      expect(screen.getAllByRole('heading', { name: 'Coder Agent' })[0]).toBeInTheDocument()
    })

    it('should display opencode model count via the lightweight hint', async () => {
      renderWithQueryClient(<SettingsPage />)
      await waitFor(() => {
        expect(screen.getAllByText(/2 models available/i)[0]).toBeInTheDocument()
      })
    })

    it('should not render the redundant Runtime/Command/Models summary or provider note', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.queryByText('Command')).not.toBeInTheDocument()
      expect(screen.queryByText('Models')).not.toBeInTheDocument()
      expect(screen.queryByText(/Mohist does not configure AI providers/i)).not.toBeInTheDocument()
    })

    it('renders the default model trigger with the stored variant suffix when the model reports variants', async () => {
      _opencodeModelsData = {
        models: ['openai/gpt-4', 'anthropic/claude-3-opus'],
        modelVariants: { 'anthropic/claude-3-opus': ['low', 'medium', 'high'] },
      }
      _workflowVariablesData = { vars: { agent: { type: 'opencode', model: 'anthropic/claude-3-opus', variant: 'high' } }, stages: null }

      renderWithQueryClient(<SettingsPage />)

      await waitFor(() => {
        expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
        const trigger = document.getElementById('settings-default-model') as HTMLElement
        expect(trigger).toBeInTheDocument()
        expect(trigger.textContent).toContain('high')
      })
    })

    it('does not show a variant suffix on the default model trigger when the model is not in the variants map', async () => {
      _opencodeModelsData = {
        models: ['openai/gpt-4', 'anthropic/claude-3-opus'],
        modelVariants: { 'anthropic/claude-3-opus': [] },
      }
      _workflowVariablesData = { vars: { agent: { type: 'opencode', model: 'anthropic/claude-3-opus', variant: 'high' } }, stages: null }

      renderWithQueryClient(<SettingsPage />)

      await waitFor(() => {
        expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
        const trigger = document.getElementById('settings-default-model') as HTMLElement
        expect(trigger.textContent).not.toContain('high')
      })
    })
  })

  describe('Tab switching', () => {
    it('should switch to Runtime section when the Runtime sub-nav link is clicked', () => {
      renderWithQueryClient(<SettingsPage />, ['/settings/ai'])

      fireEvent.click(screen.getByRole('link', { name: 'Runtime' }))

      expect(screen.getAllByRole('heading', { name: 'Runtime' })[0]).toBeInTheDocument()
    })

    it('should switch back to Coder Agent section when its sub-nav link is clicked', () => {
      renderWithQueryClient(<SettingsPage />, ['/settings/ai'])

      fireEvent.click(screen.getByRole('link', { name: 'Runtime' }))
      expect(screen.getAllByRole('heading', { name: 'Runtime' })[0]).toBeInTheDocument()

      fireEvent.click(screen.getByRole('link', { name: 'Coder Agent' }))
      expect(screen.getAllByRole('heading', { name: 'Coder Agent' })[0]).toBeInTheDocument()
    })

    it('should mark the active sub-nav item with aria-current="page"', () => {
      renderWithQueryClient(<SettingsPage />, ['/settings/ai'])

      const aiLink = screen.getByRole('link', { name: 'Coder Agent' })
      const agentLink = screen.getByRole('link', { name: 'Runtime' })

      expect(aiLink).toHaveAttribute('aria-current', 'page')
      expect(agentLink).not.toHaveAttribute('aria-current')

      fireEvent.click(agentLink)

      expect(agentLink).toHaveAttribute('aria-current', 'page')
      expect(aiLink).not.toHaveAttribute('aria-current')
    })

    it('does not prompt when a dirty Runtime form re-clicks the active tab', async () => {
      renderWithQueryClient(<SettingsPage />, ['/settings/agent'])

      await waitFor(() => {
        expect(screen.getByLabelText('Session Timeout')).toBeInTheDocument()
      })
      const timeoutInput = screen.getByLabelText('Session Timeout')
      fireEvent.change(timeoutInput, { target: { value: '31' } })

      fireEvent.click(screen.getByRole('link', { name: 'Runtime' }))

      expect(screen.queryByTestId('settings-dirty-discard-alert')).not.toBeInTheDocument()
      expect(screen.getAllByRole('heading', { name: 'Runtime' })[0]).toBeInTheDocument()
      expect(timeoutInput).toHaveValue(31)
    })
  })

  describe('Loading state', () => {
    it('should display loading skeletons when opencode runtime is loading', () => {
      _opencodeRuntimeLoading = true

      const { container } = renderWithQueryClient(<SettingsPage />)

      const skeletons = container.querySelectorAll('.animate-pulse')
      expect(skeletons.length).toBeGreaterThan(0)
    })
  })

  describe('Error state', () => {
    it('should display error message when opencode runtime query fails', async () => {
      _opencodeRuntimeError = 'Failed to load opencode runtime'

      renderWithQueryClient(<SettingsPage />)

      await waitFor(() => {
        expect(screen.getAllByText(/Failed to load opencode runtime/i)[0]).toBeInTheDocument()
      })
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

    it('renders typed runtime and source fields', async () => {
      _systemInfoData = createSystemInfo()

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText('Running version').length).toBeGreaterThan(0)
      })
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

    it('hides update action for unsupported installs and shows note', async () => {
      _systemInfoData = createSystemInfo({
        source: { path: null, branch: null, head: null, dirty: false },
        install: { mode: 'binary', serviceManager: null, serverUnit: null, runnerUnit: null },
        update: { status: 'unsupported', available: false, reason: 'Web update is unsupported for the detected deployment' },
        services: { server: null, runner: null },
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText(/Web update is unsupported/i).length).toBeGreaterThan(0)
      })
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
    })

    it('renders system info error state without placeholder runtime facts', async () => {
      _systemInfoError = 'system info failed'

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText(/system info failed/i).length).toBeGreaterThan(0)
      })
      expect(screen.queryByText('Running version')).not.toBeInTheDocument()
      expect(screen.queryByText(/Web update is unsupported/i)).not.toBeInTheDocument()
    })

    it('renders logs path and update progress label', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          logs: [
            { at: '2026-06-01T00:00:00Z', stage: 'Building', message: 'Starting update' },
            { at: '2026-06-01T00:00:01Z', stage: 'Waiting for reconnect', message: 'Server restart requested' },
          ],
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText('/logs').length).toBeGreaterThan(0)
      })
      expect(screen.getAllByText(/Waiting for restart/i).length).toBeGreaterThan(0)
      expect(screen.getAllByText('/repo').length).toBeGreaterThan(0)
      expect(screen.getAllByText('mohist.service').length).toBeGreaterThan(0)
      expect(screen.getAllByText('mohist-runner.service').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Update log').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Starting update').length).toBeGreaterThan(0)
      expect(screen.getAllByText('Server restart requested').length).toBeGreaterThan(0)
    })

    it('renders dirty-source warning and disables update action', async () => {
      _systemInfoData = createSystemInfo({
        source: { path: '/repo', branch: 'main', head: 'fedcba0987654321', dirty: true },
        update: { status: 'dirty-source', available: true, reason: 'Source tree has uncommitted changes' },
      })

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText(/Local source has uncommitted changes/i).length).toBeGreaterThan(0)
      })
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
    })

    it('renders recovered persisted failure message from update status', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'failed',
          stage: 'Restarting server',
          reason: 'systemctl exited with code 1',
          logs: [{ at: '2026-06-01T00:00:01Z', stage: 'Restarting server', message: 'systemctl exited with code 1' }],
          completedAt: '2026-06-01T00:00:02Z',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText(/systemctl exited with code 1/i).length).toBeGreaterThan(0)
      })
    })

    it('renders persisted in-progress update state after reload', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'running',
          stage: 'Building',
          reason: 'Starting update',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText(/Building/i).length).toBeGreaterThan(0)
      })
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).not.toBeInTheDocument()
    })

    it('renders explicit ready state after reconnect hash match', async () => {
      _systemInfoData = createSystemInfo({
        running: { version: '1.2.3', gitHash: 'fedcba0987654321', startedAt: '2026-06-01T00:00:00Z' },
      })
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          runningGitHash: 'fedcba0987654321',
          sourceHead: 'fedcba0987654321',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getAllByText(/Ready/i).length).toBeGreaterThan(0)
      })
    })

    it('renders superseded state with current runtime identity and hides active progress', async () => {
      _systemInfoData = createSystemInfo({
        running: { version: '1.2.4', gitHash: 'newsha9876543210', startedAt: '2026-06-01T00:00:00Z' },
      })
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'superseded',
          stage: 'Waiting for reconnect',
          runningGitHash: 'abcdef1234567890',
          sourceHead: 'fedcba0987654321',
          completedAt: '2026-05-31T00:00:00Z',
          reason: 'Running git hash differs from job source HEAD',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getByTestId('system-update-superseded')).toBeInTheDocument()
      })
      expect(screen.getAllByText(/Previous update is no longer relevant/i).length).toBeGreaterThan(0)
      expect(screen.getByTestId('system-update-superseded-runtime')).toHaveTextContent('v1.2.4')
      expect(screen.getByTestId('system-update-superseded-runtime')).toHaveTextContent('newsha98')
      expect(screen.queryByTestId('system-update-progress-stages')).not.toBeInTheDocument()
      expect(screen.queryByTestId('system-update-stage-Building')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /Update & Restart/i })).toBeInTheDocument()
    })

    it('renders Succeeded outcome label for completed updates', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'succeeded',
          stage: 'Verifying runtime',
          outcome: 'succeeded',
          completedAt: '2026-06-01T00:00:05Z',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        const outcome = screen.getByTestId('system-update-outcome')
        expect(outcome).toHaveAttribute('data-outcome', 'succeeded')
        expect(outcome).toHaveTextContent('Succeeded')
      })
    })

    it('renders Recovered outcome label with warnings detail', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'recovered',
          stage: 'Verifying runtime',
          outcome: 'recovered',
          reason: 'Skill assets missing: managed skill manifest not found',
          completedAt: '2026-06-01T00:00:05Z',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        const outcome = screen.getByTestId('system-update-outcome')
        expect(outcome).toHaveAttribute('data-outcome', 'recovered')
        expect(outcome).toHaveTextContent('Recovered with warnings')
        expect(outcome).toHaveTextContent('Skill assets missing')
      })
    })

    it('renders Failed outcome label with unavailable capability', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'failed',
          stage: 'Restoring runner',
          outcome: 'failed',
          reason: 'Runner restore failed: systemctl could not start mohist-runner.service',
          unavailableCapability: 'runner',
          completedAt: '2026-06-01T00:00:05Z',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        const outcome = screen.getByTestId('system-update-outcome')
        expect(outcome).toHaveAttribute('data-outcome', 'failed')
        expect(outcome).toHaveTextContent('Failed')
        expect(outcome).toHaveTextContent('runner')
      })
    })

    it('renders Cancelled outcome label for interrupted updates', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'cancelled',
          stage: 'Preparing workflow runner',
          outcome: 'cancelled',
          reason: 'Update was interrupted',
          completedAt: '2026-06-01T00:00:03Z',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        const outcome = screen.getByTestId('system-update-outcome')
        expect(outcome).toHaveAttribute('data-outcome', 'cancelled')
        expect(outcome).toHaveTextContent('Cancelled')
      })
    })

    it('shows CLI-triggered update outcome persisted by the server', async () => {
      _systemInfoData = createSystemInfo({
        running: { version: '1.2.4', gitHash: 'clioutcome123abc', startedAt: '2026-06-01T00:00:00Z' },
      })
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'succeeded',
          stage: 'Verifying runtime',
          outcome: 'succeeded',
          jobId: 'cli-update-job-001',
          sourceHead: 'clioutcome123abc',
          runningGitHash: 'clioutcome123abc',
          completedAt: '2026-06-01T00:00:10Z',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        const outcome = screen.getByTestId('system-update-outcome')
        expect(outcome).toHaveAttribute('data-outcome', 'succeeded')
        expect(outcome).toHaveTextContent('Succeeded')
      })
      expect(screen.queryByTestId('system-update-superseded-runtime')).not.toBeInTheDocument()
      expect(screen.queryByTestId('system-update-superseded')).not.toBeInTheDocument()
    })

    it('renders shared update progress stage names matching CLI labels', async () => {
      _systemInfoData = createSystemInfo()
      _systemUpdateStatusData = {
        hasJob: true,
        job: createUpdateJob({
          status: 'running',
          stage: 'Restoring runner',
        }),
      }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        const stagesContainer = screen.getByTestId('system-update-progress-stages')
        expect(stagesContainer).toBeInTheDocument()
      })
      expect(screen.getByTestId('system-update-stage-Building')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Restarting server')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Waiting for reconnect')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Restoring runner')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Verifying runtime')).toBeInTheDocument()
      expect(screen.getByTestId('system-update-stage-Restoring runner')).toHaveAttribute('data-state', 'current')
      expect(screen.getByTestId('system-update-stage-Building')).toHaveAttribute('data-state', 'done')
      expect(screen.getByTestId('system-update-stage-Verifying runtime')).toHaveAttribute('data-state', 'pending')
    })

    it('displays the actual persisted log level from the API instead of a hardcoded value', async () => {
      _systemInfoData = createSystemInfo()
      _configData = { ..._configData, logLevel: 'DEBUG' }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      const trigger = await screen.findByRole('combobox')
      expect(trigger).toBeInTheDocument()
    })

    it('renders the four supported log-level options', async () => {
      _systemInfoData = createSystemInfo()

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getByRole('combobox')).toBeInTheDocument()
      })
      const trigger = screen.getByRole('combobox')
      fireEvent.click(trigger)

      for (const level of ['DEBUG', 'INFO', 'WARN', 'ERROR']) {
        expect(screen.getByRole('option', { name: level })).toBeInTheDocument()
      }
    })

    it('persists a new log level through the config API and shows the saved value', async () => {
      _systemInfoData = createSystemInfo()
      _configData = { ..._configData, logLevel: 'INFO' }

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getByRole('combobox')).toBeInTheDocument()
      })
      const trigger = screen.getByRole('combobox')
      fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
      fireEvent.mouseDown(trigger, { button: 0 })
      fireEvent.click(trigger)

      await waitFor(() => expect(screen.getByRole('option', { name: 'ERROR' })).toBeInTheDocument())
      const errorOption = screen.getByRole('option', { name: 'ERROR' })
      fireEvent.pointerDown(errorOption, { button: 0, pointerType: 'mouse' })
      fireEvent.pointerUp(errorOption, { button: 0, pointerType: 'mouse' })
      fireEvent.click(errorOption)

      await waitFor(() => expect(trigger).toHaveTextContent('ERROR'))
    })

    it('surfaces a failed log-level save as a visible error and reverts the displayed value', async () => {
      _systemInfoData = createSystemInfo()
      _configData = { ..._configData, logLevel: 'INFO' }
      _setLogLevelError = 'logLevel must be one of DEBUG, INFO, WARN, ERROR'

      renderWithQueryClient(<SettingsPage />, ['/settings/system'])

      await waitFor(() => {
        expect(screen.getByRole('combobox')).toBeInTheDocument()
      })
      const trigger = screen.getByRole('combobox')
      fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
      fireEvent.mouseDown(trigger, { button: 0 })
      fireEvent.click(trigger)

      await waitFor(() => expect(screen.getByRole('option', { name: 'WARN' })).toBeInTheDocument())
      const warnOption = screen.getByRole('option', { name: 'WARN' })
      fireEvent.pointerDown(warnOption, { button: 0, pointerType: 'mouse' })
      fireEvent.pointerUp(warnOption, { button: 0, pointerType: 'mouse' })
      fireEvent.click(warnOption)

      await waitFor(() => expect(screen.getByText(/logLevel must be one of/i)).toBeInTheDocument())
    })
  })

  describe('No-project empty state', () => {
    it('renders the no-project CTA in the Repositories section when no project is selected', () => {
      renderWithoutProject(<SettingsPage />, ['/settings/repositories'])

      expect(screen.getByTestId('no-project-select-button')).toBeInTheDocument()
      expect(screen.getByTestId('no-project-create-button')).toBeInTheDocument()
      expect(screen.queryByText('No project selected')).not.toBeInTheDocument()
    })

    it('renders the no-project CTA in the Label catalog section when no project is selected', () => {
      renderWithoutProject(<SettingsPage />, ['/settings/label-catalog'])

      expect(screen.getByTestId('no-project-select-button')).toBeInTheDocument()
      expect(screen.getByTestId('no-project-create-button')).toBeInTheDocument()
      expect(screen.queryByText('No project selected')).not.toBeInTheDocument()
    })

    it('renders the no-project CTA in the Templates section when no project is selected', () => {
      renderWithoutProject(<SettingsPage />, ['/settings/templates'])

      expect(screen.getByTestId('no-project-select-button')).toBeInTheDocument()
      expect(screen.getByTestId('no-project-create-button')).toBeInTheDocument()
      expect(screen.queryByText('No project selected')).not.toBeInTheDocument()
    })

    it('renders the no-project CTA in the Workflows section when no project is selected', () => {
      renderWithoutProject(<SettingsPage />, ['/settings/workflows'])

      expect(screen.getByTestId('no-project-select-button')).toBeInTheDocument()
      expect(screen.getByTestId('no-project-create-button')).toBeInTheDocument()
    })

    it('Select project CTA dispatches the sidebar reveal event', () => {
      const listener = vi.fn()
      window.addEventListener('mohist:sidebar:open-project-switcher', listener)
      try {
        renderWithoutProject(<SettingsPage />, ['/settings/repositories'])

        fireEvent.click(screen.getByTestId('no-project-select-button'))

        expect(listener).toHaveBeenCalledTimes(1)
      } finally {
        window.removeEventListener('mohist:sidebar:open-project-switcher', listener)
      }
    })

    it('Create Project CTA opens the inline CreateProjectDialog', () => {
      renderWithoutProject(<SettingsPage />, ['/settings/repositories'])

      fireEvent.click(screen.getByTestId('no-project-create-button'))

      expect(screen.getByTestId('create-project-dialog')).toBeInTheDocument()
    })
  })
})
