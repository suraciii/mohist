import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from './test-utils'
import { SettingsPage } from '../src/components/SettingsPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import React from 'react'

const mockProviders = [
  {
    id: 'openai',
    name: 'OpenAI',
    baseURL: 'https://api.openai.com',
    models: ['gpt-4', 'gpt-3.5-turbo'],
    configured: true,
    source: 'config' as const,
    isBuiltin: true,
    isDefault: true,
    apiKeyMasked: 'sk-****1234',
  },
  {
    id: 'anthropic',
    name: 'Anthropic',
    baseURL: 'https://api.anthropic.com',
    models: ['claude-3-opus', 'claude-3-sonnet'],
    configured: false,
    source: 'none' as const,
    isBuiltin: true,
    isDefault: false,
    apiKeyMasked: null,
  },
]

vi.mock('../src/hooks/useQueries', async () => {
  const actual = await import('../src/hooks/useQueries')
  return {
    ...actual,
    useProviders: vi.fn(),
    useDeleteProvider: vi.fn(),
    useSaveProvider: vi.fn(),
    useTestProvider: vi.fn(),
    useConfig: vi.fn(),
    useUpdateConfig: vi.fn(),
  }
})

const { useProviders, useDeleteProvider, useSaveProvider, useTestProvider, useConfig, useUpdateConfig } = await import('../src/hooks/useQueries')

function createMockQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = createMockQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>{ui}</BrowserRouter>
    </QueryClientProvider>
  )
}

function switchToGeneralTab() {
  fireEvent.click(screen.getByText('General'))
}

function setupMocks() {
  ;(useProviders as ReturnType<typeof vi.fn>).mockReturnValue({
    data: mockProviders,
    isLoading: false,
    error: null,
  })
  ;(useDeleteProvider as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useSaveProvider as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useTestProvider as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { agentTimeout: 1800000, maxConcurrentAgents: 8, pollInterval: 30000 },
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  })
  ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
    mutateAsync: vi.fn().mockResolvedValue({}),
    mutate: vi.fn(),
    isPending: false,
  })
}

beforeEach(() => {
  vi.clearAllMocks()
  setupMocks()
})

describe('SettingsPage', () => {
  describe('Providers Tab', () => {
    it('should render Providers tab by default', () => {
      renderWithQueryClient(<SettingsPage />)

      expect(screen.getByText('Settings')).toBeInTheDocument()
      expect(screen.getByText('Providers')).toBeInTheDocument()
      expect(screen.getByText('General')).toBeInTheDocument()
    })

    it('should display Connected Providers section', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getByText('Connected Providers')).toBeInTheDocument()
    })

    it('should display Available Providers section', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getByText('Available Providers')).toBeInTheDocument()
    })

    it('should display Custom Providers section', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getByText('Custom Providers')).toBeInTheDocument()
      expect(screen.getByText('Add Custom Provider')).toBeInTheDocument()
    })
  })

  describe('Tab switching', () => {
    it('should switch to General tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      switchToGeneralTab()

      expect(screen.getByText('General Settings')).toBeInTheDocument()
      expect(screen.getByText('Agent Timeout')).toBeInTheDocument()
      expect(screen.getByText('Max Concurrent Agents')).toBeInTheDocument()
      expect(screen.getByText('Poll Interval')).toBeInTheDocument()
    })

    it('should switch back to Providers tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      switchToGeneralTab()
      expect(screen.getByText('General Settings')).toBeInTheDocument()

      fireEvent.click(screen.getByText('Providers'))
      expect(screen.getByText('Connected Providers')).toBeInTheDocument()
    })

    it('should highlight active tab', () => {
      renderWithQueryClient(<SettingsPage />)

      const generalTab = screen.getByText('General')
      const providersTab = screen.getByText('Providers')

      expect(providersTab.closest('button')).toHaveClass('border-blue-600', 'text-blue-600')
      expect(generalTab.closest('button')).not.toHaveClass('border-blue-600')

      fireEvent.click(generalTab)

      expect(generalTab.closest('button')).toHaveClass('border-blue-600', 'text-blue-600')
      expect(providersTab.closest('button')).not.toHaveClass('border-blue-600')
    })
  })

  describe('Loading state', () => {
    it('should display loading skeletons when providers are loading', () => {
      ;(useProviders as ReturnType<typeof vi.fn>).mockReturnValue({
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
    it('should display error message when providers query fails', () => {
      ;(useProviders as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: false,
        error: new Error('Failed to load providers'),
      })

      renderWithQueryClient(<SettingsPage />)

      expect(screen.getAllByText(/Failed to load providers/i)[0]).toBeInTheDocument()
    })
  })
})

describe('General Settings Tab', () => {
  describe('Form rendering', () => {
    it('should render fields with converted display values from config', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      expect(inputs).toHaveLength(3)

      expect(inputs[0]).toHaveValue(30) // 1800000ms → 30 min
      expect(inputs[1]).toHaveValue(8)  // maxConcurrentAgents: 8
      expect(inputs[2]).toHaveValue(30) // 30000ms → 30 sec
    })

    it('should render unit labels next to each field', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      expect(screen.getByText('minutes')).toBeInTheDocument()
      expect(screen.getByText('agents')).toBeInTheDocument()
      expect(screen.getByText('seconds')).toBeInTheDocument()
    })

    it('should render field descriptions', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      expect(screen.getByText('Maximum time an agent session can run before being terminated.')).toBeInTheDocument()
      expect(screen.getByText('Maximum number of agent sessions that can run simultaneously.')).toBeInTheDocument()
      expect(screen.getByText('How often the server checks for issue state changes.')).toBeInTheDocument()
    })

    it('should render Save buttons for each field', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const saveButtons = screen.getAllByText('Save')
      expect(saveButtons).toHaveLength(3)
    })

    it('should render Reset to Defaults button', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      expect(screen.getByText('Reset to Defaults')).toBeInTheDocument()
    })
  })

  describe('Validation', () => {
    it('should show error when Agent Timeout is less than 1', () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[0], { target: { value: '0' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[0])

      expect(screen.getByText('Must be at least 1 minute')).toBeInTheDocument()
      expect(mutateAsync).not.toHaveBeenCalled()
    })

    it('should show error when Max Concurrent Agents exceeds 16', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[1], { target: { value: '20' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[1])

      expect(screen.getByText('Must be at most 16')).toBeInTheDocument()
    })

    it('should show error when Poll Interval is less than 5', () => {
      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[2], { target: { value: '2' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[2])

      expect(screen.getByText('Must be at least 5 seconds')).toBeInTheDocument()
    })
  })

  describe('Save behavior', () => {
    it('should call updateConfig with ms-converted timeout value', async () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[0], { target: { value: '45' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[0])

      await waitFor(() => {
        expect(mutateAsync).toHaveBeenCalledWith({
          key: 'agent.timeout',
          value: 2700000, // 45 min → ms
        })
      })
    })

    it('should call updateConfig with raw maxConcurrentAgents value', async () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[1], { target: { value: '4' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[1])

      await waitFor(() => {
        expect(mutateAsync).toHaveBeenCalledWith({
          key: 'agent.maxConcurrent',
          value: 4,
        })
      })
    })

    it('should call updateConfig with ms-converted pollInterval value', async () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[2], { target: { value: '60' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[2])

      await waitFor(() => {
        expect(mutateAsync).toHaveBeenCalledWith({
          key: 'poll.interval',
          value: 60000, // 60 sec → ms
        })
      })
    })

    it('should not call API when validation fails', () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      const inputs = screen.getAllByRole('spinbutton')
      fireEvent.change(inputs[0], { target: { value: '0' } })

      const saveButtons = screen.getAllByText('Save')
      fireEvent.click(saveButtons[0])

      expect(mutateAsync).not.toHaveBeenCalled()
    })
  })

  describe('Reset to Defaults', () => {
    it('should call updateConfig for all three keys when confirmed', async () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      vi.spyOn(window, 'confirm').mockReturnValue(true)

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      fireEvent.click(screen.getByText('Reset to Defaults'))

      await waitFor(() => {
        expect(mutateAsync).toHaveBeenCalledWith({ key: 'agent.timeout', value: 1800000 })
        expect(mutateAsync).toHaveBeenCalledWith({ key: 'agent.maxConcurrent', value: 8 })
        expect(mutateAsync).toHaveBeenCalledWith({ key: 'poll.interval', value: 30000 })
      })

      expect(mutateAsync).toHaveBeenCalledTimes(3)
      vi.restoreAllMocks()
    })

    it('should not call API when reset is cancelled', () => {
      const mutateAsync = vi.fn().mockResolvedValue({})
      ;(useUpdateConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        mutateAsync,
        mutate: vi.fn(),
        isPending: false,
      })

      vi.spyOn(window, 'confirm').mockReturnValue(false)

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      fireEvent.click(screen.getByText('Reset to Defaults'))

      expect(mutateAsync).not.toHaveBeenCalled()
      vi.restoreAllMocks()
    })
  })

  describe('Loading state', () => {
    it('should display loading skeletons when config is loading', () => {
      ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: true,
        error: null,
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      expect(screen.getByText('General Settings')).toBeInTheDocument()
    })
  })

  describe('Error state', () => {
    it('should display error message when config query fails', () => {
      ;(useConfig as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: false,
        error: new Error('Failed to load config'),
        refetch: vi.fn(),
      })

      renderWithQueryClient(<SettingsPage />)
      switchToGeneralTab()

      expect(screen.getByText(/Failed to load settings/i)).toBeInTheDocument()
      expect(screen.getByText('Retry')).toBeInTheDocument()
    })
  })
})
