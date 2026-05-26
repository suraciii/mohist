import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from './test-utils'
import { CustomProviderDialog } from '../src/components/CustomProviderDialog'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'

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

function fillValidCatalogForm() {
  fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
  fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
  fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
  fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4, gpt-3.5-turbo' } })
}

describe('CustomProviderDialog', () => {
  it('renders catalog copy without credential or connection-test controls', () => {
    renderWithQueryClient(<CustomProviderDialog open={true} onClose={vi.fn()} />)

    expect(screen.getByText('Add Custom Provider Catalog')).toBeInTheDocument()
    expect(screen.getByText(/Mohist does not authenticate with the provider/i)).toBeInTheDocument()
    expect(screen.getByText(/Configure credentials in the external coder agent/i)).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('sk-...')).not.toBeInTheDocument()
    expect(screen.queryByText('Test Connection')).not.toBeInTheDocument()
  })

  it('validates provider id', () => {
    renderWithQueryClient(<CustomProviderDialog open={true} onClose={vi.fn()} />)

    fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: '' } })
    expect(screen.getByText('Provider ID is required')).toBeInTheDocument()

    fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'Invalid ID!' } })
    expect(screen.getByText('Provider ID must contain only lowercase letters, numbers, and hyphens')).toBeInTheDocument()
  })

  it('auto-lowercases provider id', () => {
    renderWithQueryClient(<CustomProviderDialog open={true} onClose={vi.fn()} />)

    const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
    fireEvent.change(idInput, { target: { value: 'MyProvider' } })

    expect((idInput as HTMLInputElement).value).toBe('myprovider')
  })

  it('validates base URL', () => {
    renderWithQueryClient(<CustomProviderDialog open={true} onClose={vi.fn()} />)

    const baseURLInput = screen.getByPlaceholderText('e.g., https://api.example.com/v1')
    fireEvent.change(baseURLInput, { target: { value: '' } })
    expect(screen.getByText('Base URL is required')).toBeInTheDocument()

    fireEvent.change(baseURLInput, { target: { value: 'not-a-valid-url' } })
    expect(screen.getByText('Base URL must be a valid URL (e.g., https://api.example.com)')).toBeInTheDocument()
  })

  it('keeps Save disabled until required catalog fields are valid', () => {
    renderWithQueryClient(<CustomProviderDialog open={true} onClose={vi.fn()} />)

    expect(screen.getByText('Save')).toBeDisabled()

    fillValidCatalogForm()

    expect(screen.getByText('Save')).not.toBeDisabled()
  })

  it('saves catalog entry directly without test warning', async () => {
    const saveMutate = vi.fn().mockImplementation((_vars, callbacks) => {
      setTimeout(() => {
        callbacks.onSuccess({ id: 'my-provider', configured: true })
      }, 0)
    })
    ;(useSaveProvider as ReturnType<typeof vi.fn>).mockReturnValue({
      mutate: saveMutate,
      isPending: false,
      error: null,
    })

    renderWithQueryClient(<CustomProviderDialog open={true} onClose={vi.fn()} />)
    fillValidCatalogForm()

    fireEvent.click(screen.getByText('Save'))

    await waitFor(() => {
      expect(screen.queryByText('Test Recommended')).not.toBeInTheDocument()
      expect(saveMutate).toHaveBeenCalledWith(
        {
          id: 'my-provider',
          data: expect.objectContaining({
            name: 'My Provider',
            baseURL: 'https://api.example.com/v1',
            models: ['gpt-4', 'gpt-3.5-turbo'],
            sdk: 'openai-compatible',
          }),
        },
        expect.any(Object),
      )
    })
  })

  it('calls onClose when Cancel is clicked', () => {
    const onClose = vi.fn()
    renderWithQueryClient(<CustomProviderDialog open={true} onClose={onClose} />)

    fireEvent.click(screen.getByText('Cancel'))

    expect(onClose).toHaveBeenCalled()
  })
})
