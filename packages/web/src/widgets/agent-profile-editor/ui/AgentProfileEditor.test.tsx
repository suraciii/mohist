import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import type { AgentInfo } from '../../../entities/agent'
import { AgentProfileEditor, type AgentProfileEditorOperationsHook } from './AgentProfileEditor'
import { useMswServer } from '../../../../tests/support/msw'

const FORBIDDEN_AGENT_CONFIG_KEYS = [
  'type',
  'livenessQuietThresholdMs',
  'probeTimeoutMs',
  'sessionStartTimeoutMs',
  'compaction',
] as const

function AssertNoLegacyKey(agentConfig: Record<string, unknown> | null) {
  for (const key of FORBIDDEN_AGENT_CONFIG_KEYS) {
    expect(agentConfig).not.toHaveProperty(key)
  }
}

const mocks = {
  createMutation: { mutate: vi.fn(), isPending: false },
  updateMutation: { mutate: vi.fn(), isPending: false },
  archiveMutation: { mutate: vi.fn(), isPending: false },
}

useMswServer(
  http.get('*/api/projects/:projectId/opencode/models', ({ request }) => {
    const models = new URL(request.url).searchParams.get('runtime') === 'pi'
      ? ['pi/anthropic/claude']
      : ['openai/gpt-4', 'anthropic/claude']
    return HttpResponse.json({
      success: true,
      data: {
        models,
        modelVariants: {
          'openai/gpt-4': ['standard'],
          'anthropic/claude': ['low', 'medium', 'high'],
        },
      },
    })
  }),
)

const operationsHook: AgentProfileEditorOperationsHook = () => ({
  createAgent: mocks.createMutation,
  updateAgent: mocks.updateMutation,
  archiveAgent: mocks.archiveMutation,
})


function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

const defaultProps = {
  open: true,
  onClose: vi.fn(),
}

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}</div>
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
            operationsHook={operationsHook}
          />
          <LocationProbe />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

async function openAgentModelSelect() {
  fireEvent.click(document.querySelector('#agent-model') as HTMLElement)
  await waitFor(() => {
    expect(screen.getByTestId('agent-model-row-anthropic/claude-variant-high')).toBeInTheDocument()
  })
}

function fillRequiredFields() {
  fireEvent.change(screen.getByTestId('editor-name'), { target: { value: 'Variant Agent' } })
  fireEvent.change(screen.getByTestId('editor-instructions'), { target: { value: 'Use the selected model' } })
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

    it('renders model variant chips and persists model plus variant on create', async () => {
      renderEditor()
      fillRequiredFields()
      await openAgentModelSelect()

      expect(screen.getByTestId('agent-model-row-anthropic/claude-variant-low')).toHaveTextContent('low')
      expect(screen.getByTestId('agent-model-row-anthropic/claude-variant-medium')).toHaveTextContent('medium')
      fireEvent.click(screen.getByTestId('agent-model-row-anthropic/claude-variant-high'))
      fireEvent.click(screen.getByTestId('editor-save'))

      expect(mocks.createMutation.mutate).toHaveBeenCalledWith(
        expect.objectContaining({
          agentConfig: { model: 'anthropic/claude', variant: 'high', runtime: 'opencode' },
        }),
        expect.any(Object),
      )
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
      await act(async () => {
        onSuccess({ id: 'new-id', name: 'Agent X' })
      })
      await waitFor(() => {
        expect(onClose).toHaveBeenCalled()
        expect(screen.getByTestId('current-path')).toHaveTextContent('/Test/agents/new-id')
      })
    })
  })

  describe('edit flow', () => {
    const existingAgent: AgentInfo = {
      id: 'agent-1',
      projectId: 'proj-1',
      name: 'Existing Agent',
      description: '',
      instructions: 'Original instructions',
      agentConfig: { model: 'anthropic/claude', variant: 'high' },
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

    it('persists an updated variant and restores its active state from stored agentConfig', async () => {
      renderEditor({ agent: existingAgent })
      await waitFor(() => {
        expect(document.querySelector('#agent-model')).toHaveTextContent('claude · high')
      })
      await openAgentModelSelect()
      expect(screen.getByTestId('agent-model-row-anthropic/claude-variant-high')).toHaveAttribute('data-variant-active', 'true')
      fireEvent.click(screen.getByTestId('agent-model-row-anthropic/claude-variant-medium'))
      fireEvent.click(screen.getByTestId('editor-save'))

      const updateCall = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][0]
       expect(updateCall.data.agentConfig).toEqual({ model: 'anthropic/claude', variant: 'medium', runtime: 'opencode' })

      cleanup()
      renderEditor({ agent: { ...existingAgent, agentConfig: updateCall.data.agentConfig } })
      await openAgentModelSelect()
      expect(screen.getByTestId('agent-model-row-anthropic/claude-variant-medium')).toHaveAttribute('data-variant-active', 'true')
    })

    it('selecting the model body clears only the stored variant', async () => {
      renderEditor({ agent: existingAgent })
      await openAgentModelSelect()
      fireEvent.click(document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement)
      fireEvent.click(screen.getByTestId('editor-save'))

      const updateCall = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][0]
       expect(updateCall.data.agentConfig).toEqual({ model: 'anthropic/claude', runtime: 'opencode' })
    })

    it('clear selection clears both model and variant', async () => {
      renderEditor({ agent: existingAgent })
      fireEvent.click(screen.getByTitle('Clear'))
      fireEvent.click(screen.getByTestId('editor-save'))

      const updateCall = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][0]
      expect(updateCall.data.agentConfig).toBeNull()
    })

    it('shows inline API error on update failure', async () => {
      renderEditor({ agent: existingAgent })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      const onError = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][1]?.onError
      await act(async () => {
        onError(new Error('UPDATE_FAILED'))
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-api-error')).toHaveTextContent('UPDATE_FAILED')
      })
    })

    it('persists agentConfig carrying only {model, variant} — drops legacy keys from spread', async () => {
      // Per #410 T-002 design D5: writeAgentModelAndVariant must NOT
      // preserve legacy ACP/liveness keys via spread. The agent profile
      // editor (and AgentDefinitionRoutes) only carry the converged shape;
      // any previous `type` / livenessQuietThresholdMs / probeTimeoutMs
      // / sessionStartTimeoutMs / compaction keys supplied via the
      // existing config are dropped before the API request.
      const legacyAgent: AgentInfo = {
        ...existingAgent,
        agentConfig: {
          type: 'opencode',
          livenessQuietThresholdMs: 1200000,
          probeTimeoutMs: 30000,
          model: 'anthropic/claude',
          variant: 'high',
        } as AgentInfo['agentConfig'],
      }
      renderEditor({ agent: legacyAgent })
      await act(async () => {
        fireEvent.change(screen.getByTestId('editor-name'), { target: { value: 'Renamed' } })
      })
      await act(async () => {
        screen.getByTestId('editor-save').click()
      })
      expect(mocks.updateMutation.mutate).toHaveBeenCalled()
      const callArgs = (mocks.updateMutation.mutate as ReturnType<typeof vi.fn>).mock.calls[0][0]
      const agentConfig = callArgs.data.agentConfig as Record<string, unknown> | null
      expect(agentConfig).not.toBeNull()
       expect(Object.keys(agentConfig ?? {}).sort()).toEqual(['model', 'runtime', 'variant'])
      AssertNoLegacyKey(agentConfig)
    })

    it('selecting Pi loads Pi models and persists the runtime', async () => {
      renderEditor()
      fireEvent.change(screen.getByTestId('agent-runtime'), { target: { value: 'pi' } })
      await waitFor(() => expect(document.querySelector('#agent-model')).toBeInTheDocument())
      fireEvent.click(document.querySelector('#agent-model') as HTMLElement)
      await waitFor(() => expect(document.querySelector('[data-model-id="pi/anthropic/claude"]')).toBeInTheDocument())
      expect(document.querySelector('[data-model-id="openai/gpt-4"]')).not.toBeInTheDocument()
      fireEvent.click(document.querySelector('[data-model-id="pi/anthropic/claude"]') as HTMLElement)
      fillRequiredFields()
      fireEvent.click(screen.getByTestId('editor-save'))
      expect(mocks.createMutation.mutate).toHaveBeenCalledWith(
        expect.objectContaining({ agentConfig: expect.objectContaining({ runtime: 'pi' }) }),
        expect.any(Object),
      )
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

    it('archive confirmation text describes the real effect and contains no pre-fix false phrase', async () => {
      renderEditor({ agent: activeAgent })
      await act(async () => {
        screen.getByTestId('editor-archive').click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-archive-confirm')).toBeInTheDocument()
      })
      const confirmButton = screen.getByTestId('editor-archive-confirm')
      const confirmDialog = confirmButton.closest('[role="dialog"]') as HTMLElement
      expect(confirmDialog).toBeTruthy()
      const description = confirmDialog.querySelector('[data-slot="dialog-description"]') as HTMLElement
      expect(description).toBeTruthy()
      const text = description.textContent ?? ''
      expect(text).toMatch(/leave the Active group/i)
      expect(text).toMatch(/cannot.*start new sessions/i)
      expect(text.toLowerCase()).not.toContain('remain visible')
      expect(text).not.toMatch(/can be reversed/i)
    })

    it('archive confirmation states reversibility with reference to the agent detail page (backed by the unarchive affordance)', async () => {
      renderEditor({ agent: activeAgent })
      await act(async () => {
        screen.getByTestId('editor-archive').click()
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-archive-confirm')).toBeInTheDocument()
      })
      const confirmButton = screen.getByTestId('editor-archive-confirm')
      const confirmDialog = confirmButton.closest('[role="dialog"]') as HTMLElement
      const description = confirmDialog.querySelector('[data-slot="dialog-description"]') as HTMLElement
      const text = description.textContent ?? ''
      expect(text).toMatch(/restore/i)
      expect(text).toMatch(/detail page/i)
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
      await act(async () => {
        onError(new Error('API_ERROR'))
      })
      await waitFor(() => {
        expect(screen.getByTestId('editor-api-error')).toHaveTextContent('API_ERROR')
      })
    })
  })
})
