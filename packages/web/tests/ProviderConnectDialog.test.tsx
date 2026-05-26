import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from './test-utils'
import { ProviderConnectDialog } from '../src/components/ProviderConnectDialog'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'

const mockProvider = {
  id: 'openai',
  name: 'OpenAI',
  baseURL: 'https://api.openai.com',
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
  }
})

const { useSaveProvider } = await import('../src/hooks/useQueries')

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
})

describe('ProviderConnectDialog', () => {
  it('renders provider catalog copy without API key fields', () => {
    renderWithQueryClient(
      <ProviderConnectDialog open={true} onClose={vi.fn()} provider={mockProvider} />
    )

    expect(screen.getByText('Add OpenAI')).toBeInTheDocument()
    expect(screen.getByText(/Add OpenAI models to the Mohist model catalog/i)).toBeInTheDocument()
    expect(screen.getByText(/Mohist does not authenticate with providers/i)).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('sk-...')).not.toBeInTheDocument()
    expect(screen.queryByText('Test Connection')).not.toBeInTheDocument()
  })

  it('does not render when provider is null', () => {
    const { container } = renderWithQueryClient(
      <ProviderConnectDialog open={true} onClose={vi.fn()} provider={null} />
    )

    expect(container.firstChild).toBeNull()
  })

  it('saves provider catalog entry without credentials', async () => {
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

    fireEvent.click(screen.getByText('Add to Catalog'))

    await waitFor(() => {
      expect(saveMutate).toHaveBeenCalledWith(
        { id: 'openai', data: { name: 'OpenAI' } },
        expect.any(Object),
      )
      expect(onClose).toHaveBeenCalled()
    })
  })

  it('calls onClose when Cancel is clicked', () => {
    const onClose = vi.fn()
    renderWithQueryClient(
      <ProviderConnectDialog open={true} onClose={onClose} provider={mockProvider} />
    )

    fireEvent.click(screen.getByText('Cancel'))

    expect(onClose).toHaveBeenCalled()
  })
})
