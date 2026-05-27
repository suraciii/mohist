import { describe, it, expect, vi, beforeEach } from 'vitest'
import { baseRender, screen, fireEvent } from './test-utils'
import { SettingsPage } from '../src/components/SettingsPage'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import React from 'react'

vi.mock('../src/hooks/useQueries', async () => {
  const actual = await import('../src/hooks/useQueries')
  return {
    ...actual,
    useOpencodeRuntime: vi.fn(),
    useOpencodeModel: vi.fn(),
    useUpdateOpencodeModel: vi.fn(),
    useStageModels: vi.fn(),
    useSetStageModels: vi.fn(),
    useAvailableModelIds: vi.fn(),
    useLogLevel: vi.fn(),
    useSetLogLevel: vi.fn(),
    useSystemInfo: vi.fn(),
    useRebuildSystem: vi.fn(),
    useAgentRuntime: vi.fn(),
    useSetAgentRuntime: vi.fn(),
  }
})

const {
  useOpencodeRuntime,
  useOpencodeModel,
  useUpdateOpencodeModel,
  useStageModels,
  useSetStageModels,
  useAvailableModelIds,
  useLogLevel,
  useSetLogLevel,
  useSystemInfo,
  useRebuildSystem,
  useAgentRuntime,
  useSetAgentRuntime,
} = await import('../src/hooks/useQueries')

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
        <Routes>
          <Route path="/settings/:section" element={ui} />
          <Route path="/settings" element={ui} />
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  ;(useOpencodeRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { mode: 'local-opencode', command: 'opencode', model: null, note: 'external coder agent' },
    isLoading: false,
    error: null,
  })
  ;(useOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { model: null },
    isLoading: false,
    error: null,
  })
  ;(useUpdateOpencodeModel as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useStageModels as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { stageModels: null },
    isLoading: false,
    error: null,
  })
  ;(useSetStageModels as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useAvailableModelIds as ReturnType<typeof vi.fn>).mockReturnValue({
    data: ['openai/gpt-4', 'anthropic/claude-3-opus'],
    isLoading: false,
    error: null,
  })
  ;(useLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
    data: { level: 'INFO' },
    isLoading: false,
    error: null,
  })
  ;(useSetLogLevel as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useSystemInfo as ReturnType<typeof vi.fn>).mockReturnValue({
    data: null,
    isLoading: false,
    error: null,
  })
  ;(useRebuildSystem as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
  ;(useAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
    data: {
      timeout: 1800000,
      stageTimeout: 3600000,
      taskTimeout: 600000,
      maxConcurrent: 8,
      maxGracePeriods: 2,
      pollInterval: 30000,
    },
    isLoading: false,
    error: null,
  })
  ;(useSetAgentRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
    mutate: vi.fn(),
    isPending: false,
  })
})

describe('SettingsPage', () => {
  describe('Coder Agent Tab', () => {
    it('should render Coder Agent tab by default', () => {
      renderWithQueryClient(<SettingsPage />)

      expect(screen.getByText('Settings')).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Coder Agent' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Runtime' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'System' })).toBeInTheDocument()
      expect(screen.getAllByRole('heading', { name: 'External Coder Agent' })[0]).toBeInTheDocument()
    })

    it('should display opencode model count', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getAllByText('2')[0]).toBeInTheDocument()
    })

    it('should explain that providers are external to Mohist', () => {
      renderWithQueryClient(<SettingsPage />)
      expect(screen.getAllByText(/Mohist does not configure AI providers/i)[0]).toBeInTheDocument()
    })
  })

  describe('Tab switching', () => {
    it('should switch to Runtime tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      fireEvent.click(screen.getByRole('button', { name: 'Runtime' }))

      expect(screen.getAllByRole('heading', { name: 'Coder Agent Runtime' })[0]).toBeInTheDocument()
    })

    it('should switch back to Coder Agent tab when clicked', () => {
      renderWithQueryClient(<SettingsPage />)

      fireEvent.click(screen.getByRole('button', { name: 'Runtime' }))
      expect(screen.getAllByRole('heading', { name: 'Coder Agent Runtime' })[0]).toBeInTheDocument()

      fireEvent.click(screen.getByRole('button', { name: 'Coder Agent' }))
      expect(screen.getAllByRole('heading', { name: 'External Coder Agent' })[0]).toBeInTheDocument()
    })

    it('should highlight active tab', () => {
      renderWithQueryClient(<SettingsPage />)

      const aiTab = screen.getByRole('button', { name: 'Coder Agent' })
      const agentTab = screen.getByRole('button', { name: 'Runtime' })

      expect(aiTab).toHaveClass('bg-blue-50')
      expect(aiTab).toHaveClass('text-blue-700')
      expect(agentTab).not.toHaveClass('bg-blue-50')

      fireEvent.click(agentTab)

      expect(agentTab).toHaveClass('bg-blue-50')
      expect(agentTab).toHaveClass('text-blue-700')
      expect(aiTab).not.toHaveClass('bg-blue-50')
    })
  })

  describe('Loading state', () => {
    it('should display loading skeletons when opencode runtime is loading', () => {
      ;(useOpencodeRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
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
    it('should display error message when opencode runtime query fails', () => {
      ;(useOpencodeRuntime as ReturnType<typeof vi.fn>).mockReturnValue({
        data: undefined,
        isLoading: false,
        error: new Error('Failed to load opencode runtime'),
      })

      renderWithQueryClient(<SettingsPage />)

      expect(screen.getAllByText(/Failed to load opencode runtime/i)[0]).toBeInTheDocument()
    })
  })
})
