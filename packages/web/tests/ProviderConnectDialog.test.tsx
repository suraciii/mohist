import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from './test-utils'
import { ProviderConnectDialog } from '../src/components/ProviderConnectDialog'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'

const mockProvider = {
  id: 'openai',
  name: 'OpenAI',
  baseURL: 'https://api.openai.com',
  models: ['gpt-4', 'gpt-3.5-turbo'],
  configured: false,
  source: 'none' as const,
  isBuiltin: true,
  isDefault: true,
  apiKeyMasked: null,
}

vi.mock('../src/hooks/useQueries', async () => {
  const actual = await import('../src/hooks/useQueries')
  return {
    ...actual,
    useSaveProvider: vi.fn(),
    useTestProvider: vi.fn(),
  }
})

const { useSaveProvider, useTestProvider } = await import('../src/hooks/useQueries')

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
  ;(useSaveProvider as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
    error: null,
  })
  ;(useTestProvider as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
})

describe('ProviderConnectDialog', () => {
  describe('Dialog renders with provider info', () => {
    it('should display provider name and description', () => {
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      expect(screen.getByText('Connect OpenAI')).toBeInTheDocument()
      expect(screen.getByText(/Enter your OpenAI API key to enable GPT models/i)).toBeInTheDocument()
    })

    it('should display API Key input field', () => {
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      expect(screen.getByPlaceholderText('sk-...')).toBeInTheDocument()
    })

    it('should not render when provider is null', () => {
      const { container } = renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={null} />
      )

      expect(container.firstChild).toBeNull()
    })
  })

  describe('API Key input validation', () => {
    it('should have save button disabled with empty API key', () => {
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const saveButton = screen.getByText('Save')
      expect(saveButton).toBeDisabled()
    })

    it('should have test connection button disabled with empty API key', () => {
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const testButton = screen.getByText('Test Connection')
      expect(testButton).toBeDisabled()
    })
  })

  describe('Test Connection button enables with input', () => {
    it('should enable Test Connection button when valid API key is entered', () => {
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const input = screen.getByPlaceholderText('sk-...')
      fireEvent.change(input, { target: { value: 'sk-test123456789' } })

      const testButton = screen.getByText('Test Connection')
      expect(testButton).not.toBeDisabled()
    })

    it('should enable Save button when valid API key is entered', () => {
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const input = screen.getByPlaceholderText('sk-...')
      fireEvent.change(input, { target: { value: 'sk-test123456789' } })

      const saveButton = screen.getByText('Save')
      expect(saveButton).not.toBeDisabled()
    })
  })

  describe('Dialog shows test success', () => {
    it('should display success message with green checkmark when test succeeds', async () => {
      const testMutate = vi.fn().mockImplementation((_vars, callbacks) => {
        setTimeout(() => {
          callbacks.onSuccess({ success: true })
        }, 0)
      })
      ;(useTestProvider as ReturnType<typeof vi.fn>).mockReturnValue({
        mutate: testMutate,
        isPending: false,
      })

      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const input = screen.getByPlaceholderText('sk-...')
      fireEvent.change(input, { target: { value: 'sk-valid-key' } })

      const testButton = screen.getByText('Test Connection')
      fireEvent.click(testButton)

      await waitFor(() => {
        expect(screen.getByText('Connection successful! Your API key is valid.')).toBeInTheDocument()
      })
    })
  })

  describe('Dialog shows test failure', () => {
    it('should display error message with red X when test fails', async () => {
      const testMutate = vi.fn().mockImplementation((_vars, callbacks) => {
        setTimeout(() => {
          callbacks.onSuccess({ success: false })
        }, 0)
      })
      ;(useTestProvider as ReturnType<typeof vi.fn>).mockReturnValue({
        mutate: testMutate,
        isPending: false,
      })

      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const input = screen.getByPlaceholderText('sk-...')
      fireEvent.change(input, { target: { value: 'sk-invalid-key' } })

      const testButton = screen.getByText('Test Connection')
      fireEvent.click(testButton)

      await waitFor(() => {
        expect(screen.getByText('Connection failed. Please check your API key and try again.')).toBeInTheDocument()
      })
    })

    it('should display error message when test throws error', async () => {
      const testMutate = vi.fn().mockImplementation((_vars, callbacks) => {
        setTimeout(() => {
          callbacks.onError(new Error('Network error'))
        }, 0)
      })
      ;(useTestProvider as ReturnType<typeof vi.fn>).mockReturnValue({
        mutate: testMutate,
        isPending: false,
      })

      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
      )

      const input = screen.getByPlaceholderText('sk-...')
      fireEvent.change(input, { target: { value: 'sk-error-key' } })

      const testButton = screen.getByText('Test Connection')
      fireEvent.click(testButton)

      await waitFor(() => {
        expect(screen.getByText('Connection failed: Network error')).toBeInTheDocument()
      })
    })
  })

  describe('Save functionality', () => {
    it('should call onClose after successful save', async () => {
      const onClose = vi.fn()
      const saveMutate = vi.fn().mockImplementation((_vars, callbacks) => {
        setTimeout(() => {
          callbacks.onSuccess({ id: 'openai', configured: true })
        }, 0)
      })
      ;(useSaveProvider as ReturnType<typeof vi.fn>).mockReturnValue({
        mutate: saveMutate,
        isPending: false,
        error: null,
      })

      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={onClose} provider={mockProvider} />
      )

      const input = screen.getByPlaceholderText('sk-...')
      fireEvent.change(input, { target: { value: 'sk-test-key' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(onClose).toHaveBeenCalled()
      })
    })
  })

  describe('Cancel button', () => {
    it('should call onClose when Cancel is clicked', () => {
      const onClose = vi.fn()
      renderWithQueryClient(
        <ProviderConnectDialog open={true} onClose={onClose} provider={mockProvider} />
      )

      const cancelButton = screen.getByText('Cancel')
      fireEvent.click(cancelButton)

      expect(onClose).toHaveBeenCalled()
    })
  })
})