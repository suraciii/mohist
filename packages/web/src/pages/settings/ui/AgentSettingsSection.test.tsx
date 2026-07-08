import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { baseRender, fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { toast } from 'sonner'
import type { AgentRuntimeConfig, GeneralConfig } from '../../../entities/settings'

const runtimeClient = vi.hoisted(() => ({
  getAgentRuntime: vi.fn(),
  getConfig: vi.fn(),
  updateAgentRuntime: vi.fn(),
}))

vi.mock('../../../entities/settings/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings/api/client')>()
  return {
    ...actual,
    getAgentRuntime: runtimeClient.getAgentRuntime,
    getConfig: runtimeClient.getConfig,
    updateAgentRuntime: runtimeClient.updateAgentRuntime,
  }
})

const { AgentSettingsSection } = await import(
  './AgentSettingsSection'
)

const RUNTIME: AgentRuntimeConfig = {
  timeout: 1800000,
  stageTimeout: 3600000,
  taskTimeout: 600000,
  maxConcurrent: 8,
  maxGracePeriods: 2,
  pollInterval: 30000,
}

const DEFAULT_RUNTIME: AgentRuntimeConfig = {
  timeout: 600000,
  stageTimeout: 3600000,
  taskTimeout: 600000,
  maxConcurrent: 3,
  maxGracePeriods: 3,
  pollInterval: 5000,
}

const DEFAULT_CONFIG: GeneralConfig = {
  agentTimeout: 600,
  taskTimeout: 600,
  stageTimeout: 3600,
  maxConcurrentAgents: 3,
  maxGracePeriods: 3,
  pollInterval: 5000,
  logLevel: 'INFO',
}

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

function getInputByLabel(label: string): HTMLInputElement {
  const labelEl = screen.getByText(label).closest('label')
  if (!labelEl) throw new Error(`No label for ${label}`)
  const wrapper = labelEl.parentElement
  if (!wrapper) throw new Error(`No wrapper for label ${label}`)
  const input = wrapper.querySelector('input')
  if (!input) throw new Error(`No input for label ${label}`)
  return input as HTMLInputElement
}

function getNumberInputByLabel(label: string): HTMLInputElement {
  const labelEl = screen.getByText(label).closest('label')
  if (!labelEl) throw new Error(`Label "${label}" not found`)
  const container = labelEl.parentElement
  if (!container) throw new Error(`Label "${label}" has no parent`)
  const input = container.querySelector('input[type="number"]')
  if (!input) throw new Error(`No number input found under label "${label}"`)
  return input as HTMLInputElement
}

function renderSection() {
  const queryClient = createMockQueryClient()
  return baseRender(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <AgentSettingsSection />
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

async function renderLoaded() {
  render(<AgentSettingsSection />)
  await waitFor(() => {
    expect(screen.getByText('Session Timeout')).toBeInTheDocument()
  })
}

function makeRuntimeConfig(overrides: Partial<AgentRuntimeConfig> = {}): AgentRuntimeConfig {
  return {
    ...DEFAULT_RUNTIME,
    ...overrides,
  }
}

function makeGeneralConfig(overrides: Partial<GeneralConfig> = {}): GeneralConfig {
  return {
    ...DEFAULT_CONFIG,
    ...overrides,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers({ shouldAdvanceTime: true })
  runtimeClient.getAgentRuntime.mockResolvedValue(DEFAULT_RUNTIME)
  runtimeClient.getConfig.mockResolvedValue(DEFAULT_CONFIG)
  runtimeClient.updateAgentRuntime.mockResolvedValue(DEFAULT_RUNTIME)
})

afterEach(() => {
  vi.useRealTimers()
})

describe('AgentSettingsSection (Runtime tab)', () => {
  it('renders the runtime panel successfully when config exposes scheduling values', async () => {
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Runtime')).toBeInTheDocument()
    })
    expect(screen.queryByText(/Failed to load settings/i)).not.toBeInTheDocument()
    const sessionInput = getInputByLabel('Session Timeout')
    expect(sessionInput.value).toBe('10')
  })

  it('displays a load error and does not render default runtime values when config load fails', async () => {
    runtimeClient.getAgentRuntime.mockImplementation(async () => {
      throw new Error('Empty response from /agent-runtime')
    })
    runtimeClient.getConfig.mockImplementation(async () => {
      throw new Error('server unavailable')
    })

    renderSection()

    await waitFor(() => {
      expect(screen.getByText(/Failed to load settings/i)).toBeInTheDocument()
    })
    expect(screen.queryByText('Session Timeout')).not.toBeInTheDocument()
  })

  it('converts server seconds to form minutes when displaying scheduling values', async () => {
    runtimeClient.getAgentRuntime.mockResolvedValue(
      makeRuntimeConfig({
        timeout: 900000,
        taskTimeout: 300000,
        stageTimeout: 1800000,
        pollInterval: 15000,
        maxConcurrent: 7,
        maxGracePeriods: 4,
      }),
    )
    runtimeClient.getConfig.mockResolvedValue(
      makeGeneralConfig({
        agentTimeout: 900,
        taskTimeout: 300,
        stageTimeout: 1800,
        pollInterval: 15000,
        maxConcurrentAgents: 7,
        maxGracePeriods: 4,
      }),
    )

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })
    const sessionInput = getInputByLabel('Session Timeout')
    expect(sessionInput.value).toBe('15')
    const stageInput = getInputByLabel('Stage Timeout')
    expect(stageInput.value).toBe('30')
    const taskInput = getInputByLabel('Task Timeout')
    expect(taskInput.value).toBe('5')
    const pollInput = getInputByLabel('Poll Interval')
    expect(pollInput.value).toBe('15')
    const maxConcurrentInput = getInputByLabel('Max Concurrent')
    expect(maxConcurrentInput.value).toBe('7')
    const graceInput = getInputByLabel('Retry attempts')
    expect(graceInput.value).toBe('4')
  })

  it('renders runtime labels and units from field metadata', async () => {
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Retry attempts')).toBeInTheDocument()
    })
    expect(screen.getByText('times')).toBeInTheDocument()
    expect(screen.getByText('Max Concurrent')).toBeInTheDocument()
    expect(screen.getByText('sessions')).toBeInTheDocument()
    expect(screen.queryByText('Retry Budget')).not.toBeInTheDocument()
    expect(screen.queryByText('grace periods')).not.toBeInTheDocument()
    expect(screen.queryByText('agents')).not.toBeInTheDocument()
  })

  it('shows runtime field descriptions on hover and focus', async () => {
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Max Concurrent')).toBeInTheDocument()
    })

    fireEvent.mouseEnter(screen.getByText('Max Concurrent'))
    expect(screen.getByRole('tooltip')).toHaveTextContent(
      'Upper bound constrained by runner capacity shown in the sidebar (active/max); excess tasks queue.',
    )
    fireEvent.mouseLeave(screen.getByText('Max Concurrent'))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()

    fireEvent.focus(screen.getByText('Poll Interval'))
    expect(screen.getByRole('tooltip')).toHaveTextContent('Shorter = more realtime but higher CPU/network.')
    fireEvent.blur(screen.getByText('Poll Interval'))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('keeps timeout field descriptions unchanged and available through tooltips', async () => {
    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    fireEvent.focus(screen.getByText('Session Timeout'))
    expect(screen.getByRole('tooltip')).toHaveTextContent(
      'Maximum total time an external coder agent session can run.',
    )
    fireEvent.blur(screen.getByText('Session Timeout'))

    fireEvent.focus(screen.getByText('Stage Timeout'))
    expect(screen.getByRole('tooltip')).toHaveTextContent('Maximum time a single workflow stage can take.')
    fireEvent.blur(screen.getByText('Stage Timeout'))

    fireEvent.focus(screen.getByText('Task Timeout'))
    expect(screen.getByRole('tooltip')).toHaveTextContent('Maximum time a single task within a stage can take.')
  })

  it('persists changed supported fields through the config API and reflects the updated value', async () => {
    vi.useRealTimers()
    const updateAgentRuntime = vi.fn().mockImplementation(async (payload) => ({
      ...DEFAULT_RUNTIME,
      ...payload,
    }))
    runtimeClient.updateAgentRuntime.mockImplementation(updateAgentRuntime)

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })
    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '20' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }))

    await waitFor(() => expect(updateAgentRuntime).toHaveBeenCalledTimes(1))
    const callArg = updateAgentRuntime.mock.calls[0]?.[0] as { timeout: number }
    expect(callArg.timeout).toBe(1200000)
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Save Changes' })).toBeDisabled(),
    )
    expect(screen.queryByText(/Settings saved successfully/i)).not.toBeInTheDocument()
  })

  it('surfaces a save failure inline as a role=alert + aria-live=polite error card (T-003)', async () => {
    vi.useRealTimers()
    const updateAgentRuntime = vi.fn().mockRejectedValue(
      new Error('agentTimeout must be a number'),
    )
    runtimeClient.updateAgentRuntime.mockImplementation(updateAgentRuntime)

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })
    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '20' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }))

    await waitFor(() => expect(updateAgentRuntime).toHaveBeenCalledTimes(1))
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Save Changes' })).toBeEnabled(),
    )

    const errorBanner = await screen.findByTestId('agent-runtime-save-error')
    expect(errorBanner).toHaveAttribute('role', 'alert')
    expect(errorBanner).toHaveAttribute('aria-live', 'polite')
    expect(errorBanner).toHaveTextContent(/agentTimeout must be a number/i)
    expect(errorBanner.className).toContain('text-red-700')
    expect(screen.queryByText(/Settings saved successfully/i)).not.toBeInTheDocument()
  })

  it('disables unsupported fields with explanatory text and excludes them from save payloads', async () => {
    vi.useRealTimers()
    const updateAgentRuntime = vi.fn().mockImplementation(async (payload) => ({
      ...DEFAULT_RUNTIME,
      ...payload,
    }))
    runtimeClient.updateAgentRuntime.mockImplementation(updateAgentRuntime)
    runtimeClient.getConfig.mockResolvedValue(
      makeGeneralConfig({ maxGracePeriods: undefined, pollInterval: undefined }),
    )

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    const graceInput = getInputByLabel('Retry attempts')
    expect(graceInput).toBeDisabled()
    const pollInput = getInputByLabel('Poll Interval')
    expect(pollInput).toBeDisabled()

    expect(screen.getAllByText(/not exposed by the server configuration/i).length).toBeGreaterThanOrEqual(2)

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '20' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }))

    await waitFor(() => expect(updateAgentRuntime).toHaveBeenCalledTimes(1))
    const callArg = updateAgentRuntime.mock.calls[0]?.[0] as Record<string, number>
    expect(callArg.timeout).toBe(1200000)
    expect(callArg.maxGracePeriods).toBeUndefined()
    expect(callArg.pollInterval).toBeUndefined()
  })

  it('resets supported fields to defaults and skips unsupported ones', async () => {
    vi.useRealTimers()
    const updateAgentRuntime = vi.fn().mockImplementation(async (payload) => ({
      ...DEFAULT_RUNTIME,
      ...payload,
    }))
    runtimeClient.updateAgentRuntime.mockImplementation(updateAgentRuntime)
    runtimeClient.getAgentRuntime.mockResolvedValue(
      makeRuntimeConfig({
        timeout: 1500000,
        maxConcurrent: 12,
        maxGracePeriods: 8,
        pollInterval: 90000,
      }),
    )
    runtimeClient.getConfig.mockResolvedValue(
      makeGeneralConfig({ maxGracePeriods: undefined }),
    )

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Reset to Defaults' }))

    const confirm = await screen.findByRole('button', { name: 'Reset' })
    fireEvent.click(confirm)

    await waitFor(() => expect(updateAgentRuntime).toHaveBeenCalledTimes(1))
    const callArg = updateAgentRuntime.mock.calls[0]?.[0] as Record<string, number>
    expect(callArg.timeout).toBe(600000)
    expect(callArg.maxConcurrent).toBe(3)
    expect(callArg.maxGracePeriods).toBeUndefined()
    expect(callArg.pollInterval).toBe(5000)
  })

  it('opens the shared AlertDialog on Reset and does not call updateAgentRuntime before confirm (T-001)', async () => {
    vi.useRealTimers()
    const updateAgentRuntime = vi.fn().mockImplementation(async (payload) => ({
      ...DEFAULT_RUNTIME,
      ...payload,
    }))
    runtimeClient.updateAgentRuntime.mockImplementation(updateAgentRuntime)

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Reset to Defaults' }))

    const dialog = await screen.findByTestId('agent-reset-alert')
    expect(dialog).toBeInTheDocument()
    expect(dialog).toHaveAttribute('data-tone', 'destructive')

    expect(updateAgentRuntime).not.toHaveBeenCalled()

    fireEvent.click(within(dialog).getByTestId('agent-reset-alert-cancel'))

    await waitFor(() => {
      expect(screen.queryByTestId('agent-reset-alert')).not.toBeInTheDocument()
    })
    expect(updateAgentRuntime).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Reset to Defaults' }))
    const dialog2 = await screen.findByTestId('agent-reset-alert')
    fireEvent.click(within(dialog2).getByTestId('agent-reset-alert-confirm'))

    await waitFor(() => expect(updateAgentRuntime).toHaveBeenCalledTimes(1))
  })

  it('does not render the hand-written fixed-inset-0 confirm overlay markup (T-001)', async () => {
    vi.useRealTimers()
    runtimeClient.updateAgentRuntime.mockImplementation(async (payload) => ({
      ...DEFAULT_RUNTIME,
      ...payload,
    }))

    renderSection()

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByRole('button', { name: 'Reset to Defaults' }))

    await screen.findByTestId('agent-reset-alert')

    const handWrittenOverlay = document.querySelector(
      '.fixed.inset-0.flex.items-center.justify-center.bg-black\\/50',
    )
    expect(handWrittenOverlay).toBeNull()
  })
})

describe('AgentSettingsSection mutation feedback (T-003)', () => {
  beforeEach(() => {
    runtimeClient.getAgentRuntime.mockResolvedValue(RUNTIME)
    runtimeClient.updateAgentRuntime.mockResolvedValue(RUNTIME)
  })

  it('does not render inline success or error mutation banners', async () => {
    await renderLoaded()

    expect(screen.queryByText('Settings saved successfully.')).not.toBeInTheDocument()
    expect(screen.queryByText(/Save failed|Reset failed/)).not.toBeInTheDocument()
  })

  it('fires toast.success via useSetAgentRuntime hook on save success', async () => {
    await renderLoaded()

    const timeoutInput = getNumberInputByLabel('Session Timeout')
    fireEvent.change(timeoutInput, { target: { value: '25' } })

    fireEvent.click(screen.getByRole('button', { name: /Save Changes/ }))

    await waitFor(() => {
      expect(runtimeClient.updateAgentRuntime).toHaveBeenCalled()
    })

    await waitFor(() => {
      expect(toast.success).toHaveBeenCalledWith('Coder agent runtime updated')
    })

    expect(toast.error).not.toHaveBeenCalled()
  })

  it('fires toast.error via useSetAgentRuntime hook on save failure', async () => {
    runtimeClient.updateAgentRuntime.mockImplementation(async () => {
      throw new Error('Boom')
    })

    await renderLoaded()

    const timeoutInput = getNumberInputByLabel('Session Timeout')
    fireEvent.change(timeoutInput, { target: { value: '25' } })

    fireEvent.click(screen.getByRole('button', { name: /Save Changes/ }))

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Boom')
    })

    expect(toast.success).not.toHaveBeenCalled()
    expect(screen.queryByText(/Save failed/)).not.toBeInTheDocument()
  })

  it('renders field-level validation errors inline and does not toast them (T-003 a11y wiring)', async () => {
    await renderLoaded()

    const timeoutInput = getNumberInputByLabel('Session Timeout')
    fireEvent.change(timeoutInput, { target: { value: '0' } })

    const validationError = screen.getByText('Must be at least 1 minute')
    expect(validationError).toBeInTheDocument()
    expect(validationError.className).toContain('text-red-700')
    expect(validationError).toHaveAttribute('role', 'alert')

    expect(timeoutInput).toHaveAttribute('aria-invalid', 'true')
    expect(timeoutInput.getAttribute('aria-describedby')).toBe(validationError.id)
    expect(validationError.id).toMatch(/-error$/)

    expect(toast.success).not.toHaveBeenCalled()
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('surfaces reset failure through the hook toast AND inline saveError card (T-003)', async () => {
    runtimeClient.updateAgentRuntime.mockImplementation(async () => {
      throw new Error('Reset boom')
    })

    await renderLoaded()

    fireEvent.click(screen.getByRole('button', { name: /Reset to Defaults/ }))

    await screen.findByText('Reset Coder Agent Settings')

    fireEvent.click(screen.getByRole('button', { name: /^Reset$/ }))

    await waitFor(() => {
      expect(toast.error).toHaveBeenCalledWith('Reset boom')
    })

    const errorBanner = await screen.findByTestId('agent-runtime-save-error')
    expect(errorBanner).toHaveAttribute('role', 'alert')
    expect(errorBanner).toHaveAttribute('aria-live', 'polite')
    expect(errorBanner).toHaveTextContent(/Reset boom/i)
  })

  it('does not add a sonner toast call from handleSave / confirmReset beyond what useSetAgentRuntime already does (T-003)', async () => {
    const callsBefore = vi.mocked(toast.error).mock.calls.length
    const callsBeforeSuccess = vi.mocked(toast.success).mock.calls.length

    runtimeClient.updateAgentRuntime.mockImplementation(async () => {
      throw new Error('Inline only')
    })

    await renderLoaded()

    const timeoutInput = getNumberInputByLabel('Session Timeout')
    fireEvent.change(timeoutInput, { target: { value: '25' } })
    fireEvent.click(screen.getByRole('button', { name: /Save Changes/ }))

    await screen.findByTestId('agent-runtime-save-error')

    const callsAfterSave = vi.mocked(toast.error).mock.calls.length - callsBefore
    expect(callsAfterSave).toBe(1)

    const callsBeforeReset = vi.mocked(toast.error).mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: /Reset to Defaults/ }))
    await screen.findByText('Reset Coder Agent Settings')
    fireEvent.click(screen.getByRole('button', { name: /^Reset$/ }))

    await waitFor(() => {
      expect(screen.getByTestId('agent-runtime-save-error')).toHaveTextContent(/Inline only/i)
    })

    const callsAfterReset = vi.mocked(toast.error).mock.calls.length - callsBeforeReset
    expect(callsAfterReset).toBe(1)

    expect(vi.mocked(toast.success).mock.calls.length - callsBeforeSuccess).toBe(0)
  })
})
