import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { baseRender, screen, fireEvent, waitFor } from './test-utils'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { AgentSettingsSection } from '../src/pages/settings/ui/AgentSettingsSection'

vi.mock('../src/entities/settings/api/queries', async () => {
  const actual = await import('../src/entities/settings/api/queries')
  return {
    ...actual,
    useAgentRuntime: vi.fn(),
    useSetAgentRuntime: vi.fn(),
    useConfig: vi.fn(),
  }
})

const { useAgentRuntime, useSetAgentRuntime, useConfig } = await import('../src/entities/settings/api/queries')

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

function getInputByLabel(label: string): HTMLInputElement {
  const labelEl = screen.getByText(label, { selector: 'label' })
  const wrapper = labelEl.parentElement
  if (!wrapper) throw new Error(`No wrapper for label ${label}`)
  const input = wrapper.querySelector('input')
  if (!input) throw new Error(`No input for label ${label}`)
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

function makeRuntimeConfig(overrides: Partial<{
  timeout: number
  stageTimeout: number
  taskTimeout: number
  maxConcurrent: number
  maxGracePeriods: number
  pollInterval: number
}> = {}) {
  return {
    timeout: 600000,
    stageTimeout: 3600000,
    taskTimeout: 600000,
    maxConcurrent: 3,
    maxGracePeriods: 3,
    pollInterval: 5000,
    ...overrides,
  }
}

function makeGeneralConfig(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    agentTimeout: 600,
    taskTimeout: 600,
    stageTimeout: 3600,
    maxConcurrentAgents: 3,
    maxGracePeriods: 3,
    pollInterval: 5000,
    ...overrides,
  } as Record<string, unknown>
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

afterEach(() => {
  vi.useRealTimers()
})

describe('AgentSettingsSection (Runtime tab)', () => {
  it('renders the runtime panel successfully when config exposes scheduling values', async () => {
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeRuntimeConfig(),
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeGeneralConfig(),
      isLoading: false,
      error: null,
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync: vi.fn(),
      isPending: false,
    })

    renderSection()

    expect(screen.queryByText(/Failed to load settings/i)).not.toBeInTheDocument()
    expect(screen.getByText(/Coder Agent Runtime/i)).toBeInTheDocument()
    const sessionInput = getInputByLabel('Session Timeout')
    expect(sessionInput.value).toBe('10')
  })

  it('displays a load error and does not render default runtime values when config load fails', () => {
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('Empty response from /agent-runtime'),
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error('server unavailable'),
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync: vi.fn(),
      isPending: false,
    })

    renderSection()

    expect(screen.getByText(/Failed to load settings/i)).toBeInTheDocument()
    expect(screen.queryByText('Session Timeout')).not.toBeInTheDocument()
  })

  it('converts server seconds to form minutes when displaying scheduling values', () => {
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeRuntimeConfig({
        timeout: 900000,
        taskTimeout: 300000,
        stageTimeout: 1800000,
        pollInterval: 15000,
        maxConcurrent: 7,
        maxGracePeriods: 4,
      }),
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeGeneralConfig({
        agentTimeout: 900,
        taskTimeout: 300,
        stageTimeout: 1800,
        pollInterval: 15000,
        maxConcurrentAgents: 7,
        maxGracePeriods: 4,
      }),
      isLoading: false,
      error: null,
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync: vi.fn(),
      isPending: false,
    })

    renderSection()

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
    const graceInput = getInputByLabel('Retry Budget')
    expect(graceInput.value).toBe('4')
  })

  it('persists changed supported fields through the config API and shows the updated value', async () => {
    vi.useRealTimers()
    const mutateAsync = vi.fn().mockImplementation(async (payload) => {
      const next = { ...makeRuntimeConfig(), ...payload }
      return next
    })
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeRuntimeConfig(),
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeGeneralConfig(),
      isLoading: false,
      error: null,
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync,
      mutate: vi.fn(),
      isPending: false,
    })

    renderSection()

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '20' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }))

    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    const callArg = mutateAsync.mock.calls[0]?.[0] as { timeout: number }
    expect(callArg.timeout).toBe(1200000)
    await waitFor(() => expect(screen.getByText(/Settings saved successfully/i)).toBeInTheDocument())
  })

  it('surfaces a save failure as a visible error and leaves the form in a not-saved state', async () => {
    vi.useRealTimers()
    const mutateAsync = vi.fn().mockRejectedValue(new Error('agentTimeout must be a number'))
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeRuntimeConfig(),
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeGeneralConfig(),
      isLoading: false,
      error: null,
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync,
      mutate: vi.fn(),
      isPending: false,
    })

    renderSection()

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '20' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }))

    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(screen.getByText(/agentTimeout must be a number/i)).toBeInTheDocument())
    expect(screen.queryByText(/Settings saved successfully/i)).not.toBeInTheDocument()
  })

  it('disables unsupported fields with explanatory text and excludes them from save payloads', async () => {
    vi.useRealTimers()
    const mutateAsync = vi.fn().mockImplementation(async (payload) => ({ ...makeRuntimeConfig(), ...payload }))
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeRuntimeConfig(),
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeGeneralConfig({ maxGracePeriods: undefined, pollInterval: undefined }),
      isLoading: false,
      error: null,
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync,
      mutate: vi.fn(),
      isPending: false,
    })

    renderSection()

    const graceInput = getInputByLabel('Retry Budget')
    expect(graceInput).toBeDisabled()
    const pollInput = getInputByLabel('Poll Interval')
    expect(pollInput).toBeDisabled()

    expect(screen.getAllByText(/not exposed by the server configuration/i).length).toBeGreaterThanOrEqual(2)

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '20' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save Changes' }))

    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    const callArg = mutateAsync.mock.calls[0]?.[0] as Record<string, number>
    expect(callArg.timeout).toBe(1200000)
    expect(callArg.maxGracePeriods).toBeUndefined()
    expect(callArg.pollInterval).toBeUndefined()
  })

  it('resets supported fields to defaults and skips unsupported ones', async () => {
    vi.useRealTimers()
    const mutateAsync = vi.fn().mockImplementation(async (payload) => ({ ...makeRuntimeConfig(), ...payload }))
    ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeRuntimeConfig({
        timeout: 1500000,
        maxConcurrent: 12,
        maxGracePeriods: 8,
        pollInterval: 90000,
      }),
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    })
    ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
      data: makeGeneralConfig({ maxGracePeriods: undefined }),
      isLoading: false,
      error: null,
    })
    ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
      mutateAsync,
      mutate: vi.fn(),
      isPending: false,
    })

    renderSection()

    fireEvent.click(screen.getByRole('button', { name: 'Reset to Defaults' }))

    const confirm = await screen.findByRole('button', { name: 'Reset' })
    fireEvent.click(confirm)

    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    const callArg = mutateAsync.mock.calls[0]?.[0] as Record<string, number>
    expect(callArg.timeout).toBe(600000)
    expect(callArg.maxConcurrent).toBe(3)
    expect(callArg.maxGracePeriods).toBeUndefined()
    expect(callArg.pollInterval).toBe(5000)
  })
})
