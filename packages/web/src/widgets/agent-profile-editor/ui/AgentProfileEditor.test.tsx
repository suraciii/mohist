// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo } from '../../../entities/agent'
import { AgentProfileEditor } from './AgentProfileEditor'

const mocks = vi.hoisted(() => ({
  createMutation: { mutate: vi.fn(), isPending: false },
  updateMutation: { mutate: vi.fn(), isPending: false },
  archiveMutation: { mutate: vi.fn(), isPending: false },
  availableModels: { models: ['gpt-4', 'gpt-4o', 'claude-3'] },
  modelVariants: {},
}))

vi.mock('../../../entities/agent', () => ({
  useCreateAgent: () => mocks.createMutation,
  useUpdateAgent: () => mocks.updateMutation,
  useArchiveAgent: () => mocks.archiveMutation,
  readAgentModelAndVariant: (agent: any) => {
    if (!agent?.agentConfig) return { model: null, variant: null }
    const cfg = agent.agentConfig as Record<string, unknown>
    return { model: cfg.model as string ?? null, variant: cfg.variant as string ?? null }
  },
  writeAgentModelAndVariant: (current: any, model: string | null, variant: string | null) => {
    const base: Record<string, unknown> = current && typeof current === 'object' && !Array.isArray(current) ? { ...current } : {}
    if (model) base.model = model
    else { delete base.model; delete base.variant }
    if (variant) base.variant = variant
    else delete base.variant
    if (Object.keys(base).length === 0) return null
    return base
  },
}))

vi.mock('../../../entities/settings', () => ({
  useAvailableModelIds: () => ({ data: mocks.availableModels, isLoading: false }),
  useModelVariants: () => mocks.modelVariants,
}))

vi.mock('../../../shared/lib/useDocumentTitle', () => ({
  useDocumentTitle: () => {},
}))

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

const defaultProps = {
  open: true,
  onClose: vi.fn(),
}

function renderEditor(overrides: Partial<Parameters<typeof AgentProfileEditor>[0]> = {}) {
  const queryClient = createQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1" initialProjects={[{
        id: 'proj-1', name: 'Test',
        createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z',
        repositories: [],
      }]}>
        <MemoryRouter>
          <AgentProfileEditor
            {...defaultProps}
            {...overrides}
          />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('AgentProfileEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.createMutation = { mutate: vi.fn(), isPending: false }
    mocks.updateMutation = { mutate: vi.fn(), isPending: false }
    mocks.archiveMutation = { mutate: vi.fn(), isPending: false }
  })

  afterEach(() => {
    cleanup()
  })

  describe('create flow', () => {
    it('renders create form with title "New Agent"', () => {
      renderEditor()
      expect(screen.getByText('New Agent')).toBeInTheDocument()
      expect(screen.getByTestId('editor-save')).toHaveTextContent('Create Agent')
    })

    it('blocks submission with missing name and instructions', async () => {
      renderEditor()
      const saveBtn = screen.getByTestId('editor-save')
      await act(async () => {
        saveBtn.click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-name-error')).toHaveTextContent('Name is required')
      })
      expect(screen.getByTestId('editor-instructions-error')).toHaveTextContent('Instructions are required')
      expect(mocks.createMutation.mutate).not.toHaveBeenCalled()
    })

    it('calls createAgent with valid form data', async () => {
      renderEditor()
      await act(async () => {
        fireEvent.change(screen.getByTestId('editor-name'), { target: { value: 'My New Agent' } })
        fireEvent.change(screen.getByTestId('editor-instructions'), { target: { value: 'Be helpful' } })
      })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      expect(mocks.createMutation.mutate).toHaveBeenCalled()
      const callArgs = (mocks.createMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][0]
      expect(callArgs.name).toBe('My New Agent')
      expect(callArgs.instructions).toBe('Be helpful')
    })

    it('navigates to the new agent detail page on success', async () => {
      const onClose = vi.fn()
      renderEditor({ onClose })
      await act(async () => {
        fireEvent.change(screen.getByTestId('editor-name'), { target: { value: 'Agent X' } })
        fireEvent.change(screen.getByTestId('editor-instructions'), { target: { value: 'Do things' } })
      })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      const mutateFn = mocks.createMutation.mutate
      const onSuccess = (mutateFn as ReturnType<typeof vi.fn>).mock.calls[0][1]?.onSuccess
      expect(onSuccess).toBeDefined()
      onSuccess({ id: 'new-id', name: 'Agent X' })
      expect(onClose).toHaveBeenCalled()
    })
  })

  describe('edit flow', () => {
    const existingAgent: AgentInfo = {
      id: 'agent-1',
      projectId: 'proj-1',
      name: 'Existing Agent',
      description: '',
      instructions: 'Original instructions',
      agentConfig: { model: 'gpt-4', variant: 'high' },
      skills: ['code'],
      maxConcurrentRuns: null,
      status: 'active',
      createdAt: '2026-06-01T00:00:00.000Z',
      updatedAt: '2026-06-01T00:00:00.000Z',
    }

    it('renders edit form with pre-filled values', async () => {
      renderEditor({ agent: existingAgent })
      await waitFor(() => {
        expect(screen.getByText('Edit Agent')).toBeInTheDocument()
      })
      const nameInput = screen.getByTestId('editor-name') as HTMLInputElement
      expect(nameInput.value).toBe('Existing Agent')
      const instructionsInput = screen.getByTestId('editor-instructions') as HTMLTextAreaElement
      expect(instructionsInput.value).toBe('Original instructions')
      expect(screen.getByTestId('editor-save')).toHaveTextContent('Save Changes')
    })

    it('calls updateAgent with edited values', async () => {
      renderEditor({ agent: existingAgent })
      await act(async () => {
        fireEvent.change(screen.getByTestId('editor-name'), { target: { value: 'Updated Name' } })
        fireEvent.change(screen.getByTestId('editor-instructions'), { target: { value: 'Updated instructions' } })
        fireEvent.change(screen.getByTestId('editor-skills'), { target: { value: 'code, debug' } })
      })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      expect(mocks.updateMutation.mutate).toHaveBeenCalled()
      const callArgs = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][0]
      expect(callArgs.agentRef).toBe('agent-1')
      expect(callArgs.data.name).toBe('Updated Name')
      expect(callArgs.data.instructions).toBe('Updated instructions')
      expect(callArgs.data.skills).toEqual(['code', 'debug'])
    })

    it('shows inline API error on update failure', async () => {
      renderEditor({ agent: existingAgent })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      const onError = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][1]?.onError
      onError(new Error('UPDATE_FAILED'))
      await waitFor(() => {
        expect(screen.getByTestId('editor-api-error')).toHaveTextContent('UPDATE_FAILED')
      })
    })
  })

  describe('archive flow', () => {
    const activeAgent: AgentInfo = {
      id: 'agent-1',
      projectId: 'proj-1',
      name: 'To Archive',
      description: '',
      instructions: 'Do stuff',
      agentConfig: null,
      skills: [],
      maxConcurrentRuns: null,
      status: 'active',
      createdAt: '2026-06-01T00:00:00.000Z',
      updatedAt: '2026-06-01T00:00:00.000Z',
    }

    it('shows archive button for active profiles', () => {
      renderEditor({ agent: activeAgent })
      expect(screen.getByTestId('editor-archive')).toBeInTheDocument()
    })

    it('calls archiveAgent on confirm', async () => {
      renderEditor({ agent: activeAgent })
      await act(async () => {
        screen.getByTestId('editor-archive').click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-archive-confirm')).toBeInTheDocument()
      })
      await act(async () => {
        screen.getByTestId('editor-archive-confirm').click()
      })
      expect(mocks.archiveMutation.mutate).toHaveBeenCalledWith('agent-1', expect.any(Object))
    })
  })

  describe('validation', () => {
    it('blocks submission and shows inline errors for missing required fields', async () => {
      renderEditor()
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-name-error')).toBeInTheDocument()
      })
      expect(screen.getByTestId('editor-instructions-error')).toBeInTheDocument()
    })

    it('shows inline API errors', async () => {
      renderEditor()
      await act(async () => {
        fireEvent.change(screen.getByTestId('editor-name'), { target: { value: 'Agent Name' } })
        fireEvent.change(screen.getByTestId('editor-instructions'), { target: { value: 'Do stuff' } })
      })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      const mutate = mocks.createMutation.mutate
      const onError = (mutate as ReturnType<typeof vi.fn>).mock.calls[0]?.[1]?.onError
      expect(onError).toBeDefined()
      onError(new Error('API_ERROR'))
      await waitFor(() => {
        expect(screen.getByTestId('editor-api-error')).toHaveTextContent('API_ERROR')
      })
    })
  })
})
