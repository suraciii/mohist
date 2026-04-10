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

const defaultSaveProviderMock = {
  mutate: vi.fn(),
  isPending: false,
  error: null,
}

const defaultTestProviderMock = {
  mutate: vi.fn(),
  isPending: false,
}

beforeEach(() => {
  vi.clearAllMocks()
  ;(useSaveProvider as ReturnType<typeof vi.fn>).mockReturnValue(defaultSaveProviderMock)
  ;(useTestProvider as ReturnType<typeof vi.fn>).mockReturnValue(defaultTestProviderMock)
})

describe('CustomProviderDialog', () => {
  describe('Form validation for provider ID', () => {
    it('should show error for empty provider ID', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
      fireEvent.change(idInput, { target: { value: '' } })

      expect(screen.getByText('Provider ID is required')).toBeInTheDocument()
    })

    it('should show error for provider ID with special characters', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
      fireEvent.change(idInput, { target: { value: 'My Provider!' } })

      expect(screen.getByText('Provider ID must contain only lowercase letters, numbers, and hyphens')).toBeInTheDocument()
    })

    it('should auto-lowercase provider ID input', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
      fireEvent.change(idInput, { target: { value: 'MyProvider' } })

      expect((idInput as HTMLInputElement).value).toBe('myprovider')
      expect(screen.queryByText(/Provider ID must contain only/i)).not.toBeInTheDocument()
    })

    it('should allow valid provider ID with lowercase letters, numbers, hyphens', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
      fireEvent.change(idInput, { target: { value: 'my-custom-provider-123' } })

      expect(screen.queryByText(/Provider ID must contain only/i)).not.toBeInTheDocument()
    })
  })

  describe('Form validation for base URL', () => {
    it('should show error for empty base URL', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const baseURLInput = screen.getByPlaceholderText('e.g., https://api.example.com/v1')
      fireEvent.change(baseURLInput, { target: { value: '' } })

      expect(screen.getByText('Base URL is required')).toBeInTheDocument()
    })

    it('should show error for invalid URL format', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const baseURLInput = screen.getByPlaceholderText('e.g., https://api.example.com/v1')
      fireEvent.change(baseURLInput, { target: { value: 'not-a-valid-url' } })

      expect(screen.getByText('Base URL must be a valid URL (e.g., https://api.example.com)')).toBeInTheDocument()
    })

    it('should allow valid URL', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const baseURLInput = screen.getByPlaceholderText('e.g., https://api.example.com/v1')
      fireEvent.change(baseURLInput, { target: { value: 'https://api.example.com/v1' } })

      expect(screen.queryByText(/Base URL must be a valid URL/i)).not.toBeInTheDocument()
    })
  })

  describe('Save button disabled with invalid form', () => {
    it('should have Save button disabled when form is empty', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const saveButton = screen.getByText('Save')
      expect(saveButton).toBeDisabled()
    })

    it('should have Save button disabled when only ID is filled', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
      fireEvent.change(idInput, { target: { value: 'my-provider' } })

      const saveButton = screen.getByText('Save')
      expect(saveButton).toBeDisabled()
    })

    it('should have Save button disabled when form has validation errors', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      const idInput = screen.getByPlaceholderText('e.g., my-custom-provider')
      fireEvent.change(idInput, { target: { value: 'Invalid ID!' } })

      const saveButton = screen.getByText('Save')
      expect(saveButton).toBeDisabled()
    })

    it('should have Save button enabled when all fields are valid', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      expect(saveButton).not.toBeDisabled()
    })
  })

  describe('Warning dialog shows without test', () => {
    it('should show warning when Save is clicked without Test Connection', async () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText('Test Recommended')).toBeInTheDocument()
      })
    })

    it('should show warning message text', async () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText(/You haven't tested the connection yet/i)).toBeInTheDocument()
      })
    })
  })

  describe('Pre-save warning allows override', () => {
    it('should show Save Anyway button in warning dialog', async () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText('Save Anyway')).toBeInTheDocument()
      })
    })

    it('should call save with fields when Save Anyway is clicked', async () => {
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

      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText('Save Anyway')).toBeInTheDocument()
      })

      const saveAnywayButton = screen.getByText('Save Anyway')
      fireEvent.click(saveAnywayButton)

      await waitFor(() => {
        expect(saveMutate).toHaveBeenCalledWith(
          { id: 'my-provider', data: expect.objectContaining({ name: 'My Provider', models: ['gpt-4'] }) },
          expect.any(Object)
        )
      })
    })

    it('should show Test First button in warning dialog', async () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText('Test First')).toBeInTheDocument()
      })
    })

    it('should dismiss warning when Test First is clicked', async () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText('Test First')).toBeInTheDocument()
      })

      const testFirstButton = screen.getByText('Test First')
      fireEvent.click(testFirstButton)

      expect(screen.queryByText('Test Recommended')).not.toBeInTheDocument()
    })
  })

  describe('All form fields collect input', () => {
    it('should capture all form field values on save', async () => {
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

      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Custom AI' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.custom.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-my-secret-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4, gpt-3.5-turbo, claude-3' } })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.getByText('Save Anyway')).toBeInTheDocument()
      })

      const saveAnywayButton = screen.getByText('Save Anyway')
      fireEvent.click(saveAnywayButton)

      await waitFor(() => {
        expect(saveMutate).toHaveBeenCalledWith(
          {
            id: 'my-provider',
            data: expect.objectContaining({
              name: 'My Custom AI',
              baseURL: 'https://api.custom.com/v1',
              models: ['gpt-4', 'gpt-3.5-turbo', 'claude-3'],
            }),
          },
          expect.any(Object)
        )
      })
    })
  })

  describe('Test Connection functionality', () => {
    it('should enable Test Connection button when form is valid', () => {
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const testButton = screen.getByText('Test Connection')
      expect(testButton).not.toBeDisabled()
    })

    it('should display success message when test succeeds', async () => {
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
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const testButton = screen.getByText('Test Connection')
      fireEvent.click(testButton)

      await waitFor(() => {
        expect(screen.getByText('Connection successful!')).toBeInTheDocument()
      })
    })

    it('should display error message when test fails', async () => {
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
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const testButton = screen.getByText('Test Connection')
      fireEvent.click(testButton)

      await waitFor(() => {
        expect(screen.getByText('Connection failed. Please check your settings.')).toBeInTheDocument()
      })
    })

    it('should save directly without warning when test was successful', async () => {
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
        <CustomProviderDialog open={true} onClose={vi.fn()} />
      )

      fireEvent.change(screen.getByPlaceholderText('e.g., my-custom-provider'), { target: { value: 'my-provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., My Custom Provider'), { target: { value: 'My Provider' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., https://api.example.com/v1'), { target: { value: 'https://api.example.com/v1' } })
      fireEvent.change(screen.getByPlaceholderText('sk-...'), { target: { value: 'sk-test-key' } })
      fireEvent.change(screen.getByPlaceholderText('e.g., gpt-4, gpt-3.5-turbo, claude-3'), { target: { value: 'gpt-4' } })

      const testButton = screen.getByText('Test Connection')
      fireEvent.click(testButton)

      await waitFor(() => {
        expect(screen.getByText('Connection successful!')).toBeInTheDocument()
      })

      const saveButton = screen.getByText('Save')
      fireEvent.click(saveButton)

      await waitFor(() => {
        expect(screen.queryByText('Test Recommended')).not.toBeInTheDocument()
        expect(saveMutate).toHaveBeenCalled()
      })
    })
  })

  describe('Cancel functionality', () => {
    it('should call onClose when Cancel is clicked', () => {
      const onClose = vi.fn()
      renderWithQueryClient(
        <CustomProviderDialog open={true} onClose={onClose} />
      )

      const cancelButton = screen.getByText('Cancel')
      fireEvent.click(cancelButton)

      expect(onClose).toHaveBeenCalled()
    })
  })
})