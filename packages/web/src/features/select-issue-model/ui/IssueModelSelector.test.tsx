// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { IssueModelSelector } from './IssueModelSelector'

const mocks = vi.hoisted(() => ({
  useAvailableModelIds: vi.fn(),
  useOpencodeModel: vi.fn(),
  useProject: vi.fn(() => ({ projectId: 'proj_test' })),
  getIssueWorkflowVariables: vi.fn(),
  patchIssueWorkflowDefinitionVar: vi.fn(),
  patchIssueWorkflowStageDefinitionVar: vi.fn(),
  useQueryClient: vi.fn(),
}))

vi.mock('../../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings')>()
  return {
    ...actual,
    useAvailableModelIds: () => mocks.useAvailableModelIds(),
    useOpencodeModel: () => mocks.useOpencodeModel(),
  }
})

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    getIssueWorkflowVariables: mocks.getIssueWorkflowVariables,
    patchIssueWorkflowDefinitionVar: mocks.patchIssueWorkflowDefinitionVar,
    patchIssueWorkflowStageDefinitionVar: mocks.patchIssueWorkflowStageDefinitionVar,
  }
})

vi.mock('../../../entities/project', () => ({
  useProject: mocks.useProject,
}))

vi.mock('@tanstack/react-query', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-query')>()),
  useQueryClient: () => mocks.useQueryClient(),
}))

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

beforeEach(() => {
  window.localStorage.clear()
  mocks.useQueryClient.mockReturnValue({
    invalidateQueries: vi.fn(),
  })
  mocks.useOpencodeModel.mockReturnValue({ data: { model: null, variant: null } })
  mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: {}, stages: {} })
  mocks.patchIssueWorkflowDefinitionVar.mockResolvedValue({ vars: { agent: {} }, stages: {} })
  mocks.patchIssueWorkflowStageDefinitionVar.mockResolvedValue({ vars: {}, stages: {} })
})

function renderSelector(props: { currentModel?: string | null; currentStageModels?: Record<string, string> | null } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <IssueModelSelector issueNumber={42} currentModel={props.currentModel ?? null} currentStageModels={props.currentStageModels ?? null} />
    </QueryClientProvider>,
  )
}

function openAdvanced() {
  fireEvent.click(screen.getByRole('button', { name: /Per-stage overrides/i }))
}

describe('IssueModelSelector variant picker', () => {
  it('hides the issue-level variant picker when no model is configured', () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['anthropic/claude', 'openai/gpt-4'], modelVariants: { 'anthropic/claude': ['low', 'high'] } },
      isLoading: false,
      error: null,
    })
    renderSelector()

    expect(screen.queryByTestId('issue-coder-model-variant-variant-trigger')).not.toBeInTheDocument()
  })

  it('does not offer an issue-level variant picker for an inherited default model', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['anthropic/claude'], modelVariants: { 'anthropic/claude': ['low', 'high'] } },
      isLoading: false,
      error: null,
    })
    mocks.useOpencodeModel.mockReturnValue({ data: { model: 'anthropic/claude', variant: null } })
    renderSelector()

    await waitFor(() => {
      expect(screen.getByText('claude')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('issue-coder-model-variant-variant-trigger')).not.toBeInTheDocument()
    expect(mocks.patchIssueWorkflowDefinitionVar).not.toHaveBeenCalled()
  })

  it('hides the issue-level variant picker when the configured model has no variants', () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['openai/gpt-4'], modelVariants: {} },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: { agent: { model: 'openai/gpt-4' } }, stages: {} })
    renderSelector({ currentModel: 'openai/gpt-4' })

    return waitFor(() => {
      expect(screen.queryByTestId('issue-coder-model-variant-variant-trigger')).not.toBeInTheDocument()
    })
  })

  it('renders the issue-level variant picker only with the configured model variants', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: { agent: { model: 'anthropic/claude' } }, stages: {} })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-variant-variant-trigger'))
    expect(trigger).toBeInTheDocument()
    fireEvent.click(trigger)
    const list = await waitFor(() => screen.getByRole('listbox'))
    const opts = Array.from(list.querySelectorAll('[role="option"]')).map((el) => el.textContent?.trim())
    expect(opts).toEqual(['Default', 'low', 'medium', 'high'])
  })

  it('shows the stored issue-level variant as selected when supported', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude', variant: 'medium' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-variant-variant-trigger'))
    expect(trigger).toHaveTextContent('medium')
  })

  it('drops an unsupported stored variant from the picker label', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude', variant: 'ultra' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-variant-variant-trigger'))
    expect(trigger).toHaveTextContent('Variant')
  })

  it('shows the per-stage variant picker only for stages whose model has variants', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'high'], 'openai/gpt-4': [] },
      },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: {
        build: { vars: { agent: { model: 'anthropic/claude' } } },
        check: { vars: { agent: { model: 'openai/gpt-4' } } },
      },
    })

    renderSelector({ currentStageModels: { build: 'anthropic/claude', check: 'openai/gpt-4' } })

    openAdvanced()
    await waitFor(() => {
      expect(screen.getByTestId('issue-stage-model-variant-build-variant-trigger')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('issue-stage-model-variant-check-variant-trigger')).not.toBeInTheDocument()
  })

  it('shows the stored per-stage variant as selected when supported', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: {
        build: { vars: { agent: { model: 'anthropic/claude', variant: 'high' } } },
      },
    })

    renderSelector({ currentStageModels: { build: 'anthropic/claude' } })

    openAdvanced()
    const trigger = await waitFor(() => screen.getByTestId('issue-stage-model-variant-build-variant-trigger'))
    expect(trigger).toHaveTextContent('high')
  })
})
