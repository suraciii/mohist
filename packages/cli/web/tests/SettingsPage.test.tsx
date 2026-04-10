import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent } from './test-utils'
import { SettingsPage } from '../src/components/SettingsPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
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
  }
})

const { useProviders, useDeleteProvider, useSaveProvider, useTestProvider } = await import('../src/hooks/useQueries')

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
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>
  )
}

beforeEach(() => {
  vi.clearAllMocks()
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

      fireEvent.click(screen.getByText('General'))

      expect(screen.getByText('General settings coming soon.')).toBeInTheDocument()
    })

    it('should switch back to Providers tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      fireEvent.click(screen.getByText('General'))
      expect(screen.getByText('General settings coming soon.')).toBeInTheDocument()

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
