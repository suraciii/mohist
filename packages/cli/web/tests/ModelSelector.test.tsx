import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from './test-utils'
import { ModelSelector } from '../src/components/ModelSelector'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import React from 'react'

vi.mock('../src/lib/api', async () => {
  const actual = await import('../src/lib/api')
  return {
    ...actual,
    api: {
      ...actual.api,
      updateSessionModel: vi.fn().mockResolvedValue({ id: 'test-session', model: 'claude-sonnet-4-20250514' }),
    },
  }
})

vi.mock('../src/hooks/useModels', () => ({
  useModels: vi.fn().mockReturnValue({
    data: [
      {
        id: 'anthropic',
        name: 'Anthropic',
        configured: true,
        models: [
          { id: 'claude-opus-4-20250514', name: 'Claude Opus 4', badges: ['latest'], contextWindow: 200000 },
          { id: 'claude-sonnet-4-20250514', name: 'Claude Sonnet 4', badges: ['latest'], contextWindow: 200000 },
          { id: 'claude-haiku-4-20250514', name: 'Claude Haiku 4', badges: ['latest'], contextWindow: 200000 },
        ],
      },
      {
        id: 'openai',
        name: 'OpenAI',
        configured: true,
        models: [
          { id: 'gpt-4o', name: 'GPT-4o', badges: [], contextWindow: 128000 },
          { id: 'gpt-4o-mini', name: 'GPT-4o Mini', badges: [], contextWindow: 128000 },
        ],
      },
      {
        id: 'glm',
        name: 'GLM',
        configured: true,
        models: [
          { id: 'glm-4-flash', name: 'GLM-4 Flash', badges: ['free'], contextWindow: 128000 },
          { id: 'glm-4-plus', name: 'GLM-4 Plus', badges: [], contextWindow: 128000 },
        ],
      },
    ],
    isLoading: false,
    error: null,
  }),
}))

const { api } = await import('../src/lib/api')

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

describe('ModelSelector', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    localStorage.clear()
  })

  describe('Rendering', () => {
    it('should render with default text when no model is selected', () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      expect(screen.getByText('Select model')).toBeInTheDocument()
    })

    it('should render with model name when currentModel is provided', () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" currentModel="claude-sonnet-4-20250514" />
      )

      expect(screen.getByText('Claude Sonnet 4')).toBeInTheDocument()
    })

    it('should show chevron icon', () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      expect(screen.getByRole('button')).toContainHTML('svg')
    })
  })

  describe('Opening and closing', () => {
    it('should open popover when clicked', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      const button = screen.getByRole('button')
      fireEvent.click(button)

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })
    })

    it('should close popover when clicked again', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      const button = screen.getByRole('button')
      fireEvent.click(button)

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      fireEvent.click(button)

      await waitFor(() => {
        expect(screen.queryByPlaceholderText('Search models...')).not.toBeInTheDocument()
      })
    })

    it('should show provider groups when opened', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByText('Anthropic')).toBeInTheDocument()
        expect(screen.getByText('OpenAI')).toBeInTheDocument()
      })
    })
  })

  describe('Model search', () => {
    it('should filter models by search query', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const searchInput = screen.getByPlaceholderText('Search models...')
      fireEvent.change(searchInput, { target: { value: 'claude' } })

      await waitFor(() => {
        const modelItems = screen.getAllByText(/Claude/)
        expect(modelItems.length).toBeGreaterThan(0)
      })
    })

    it('should show no results message for non-matching search', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const searchInput = screen.getByPlaceholderText('Search models...')
      fireEvent.change(searchInput, { target: { value: 'xyznonexistent' } })

      await waitFor(() => {
        expect(screen.getByText('No models found')).toBeInTheDocument()
      })
    })

    it('should clear search when input is cleared', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const searchInput = screen.getByPlaceholderText('Search models...')
      fireEvent.change(searchInput, { target: { value: 'claude' } })

      await waitFor(() => {
        expect(screen.queryByText('No models found')).not.toBeInTheDocument()
      })

      fireEvent.change(searchInput, { target: { value: '' } })

      await waitFor(() => {
        expect(screen.getByText('Anthropic')).toBeInTheDocument()
      })
    })
  })

  describe('Model selection', () => {
    it('should call updateSessionModel when a model is selected', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const modelButton = screen.getByText('Claude Opus 4')
      fireEvent.click(modelButton)

      await waitFor(() => {
        expect(api.updateSessionModel).toHaveBeenCalledWith(
          'test-session',
          'claude-opus-4-20250514',
          undefined
        )
      })
    })

    it('should call updateSessionModel with variant when provided', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" currentVariant="latest" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const modelButton = screen.getByText('Claude Sonnet 4')
      fireEvent.click(modelButton)

      await waitFor(() => {
        expect(api.updateSessionModel).toHaveBeenCalledWith(
          'test-session',
          'claude-sonnet-4-20250514',
          'latest'
        )
      })
    })
  })

  describe('Badges display', () => {
    it('should display free badge for free models', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByText('Free')).toBeInTheDocument()
      })
    })

    it('should display latest badge for latest models', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getAllByText('Latest').length).toBeGreaterThan(0)
      })
    })
  })

  describe('Keyboard navigation', () => {
    it('should navigate with arrow keys', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const searchInput = screen.getByPlaceholderText('Search models...')
      searchInput.focus()
      fireEvent.keyDown(searchInput, { key: 'ArrowDown', preventDefault: () => {} })

      await waitFor(() => {
        const highlighted = screen.getAllByText(/Claude/)
        expect(highlighted.length).toBeGreaterThan(0)
      })
    })

    it('should close on Escape key', async () => {
      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByPlaceholderText('Search models...')).toBeInTheDocument()
      })

      const searchInput = screen.getByPlaceholderText('Search models...')
      fireEvent.keyDown(searchInput, { key: 'Escape' })

      await waitFor(() => {
        expect(screen.queryByPlaceholderText('Search models...')).not.toBeInTheDocument()
      })
    })
  })

  describe('Recent models', () => {
    it('should show recent models section when localStorage has recent models', async () => {
      localStorage.setItem('mohist:recent-models', JSON.stringify(['claude-sonnet-4-20250514']))

      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.getByText('Recent')).toBeInTheDocument()
      })
    })

    it('should not show recent models section when no recent models', async () => {
      localStorage.setItem('mohist:recent-models', JSON.stringify([]))

      renderWithQueryClient(
        <ModelSelector sessionId="test-session" />
      )

      fireEvent.click(screen.getByRole('button'))

      await waitFor(() => {
        expect(screen.queryByText('Recent')).not.toBeInTheDocument()
      })
    })
  })
})