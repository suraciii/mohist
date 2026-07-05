// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { IssueModelSelector } from './IssueModelSelector'

const mocks = vi.hoisted(() => ({
  useAvailableModelIds: vi.fn(),
  useOpencodeModel: vi.fn(),
  useModelVariants: vi.fn(() => ({})),
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
    useModelVariants: () => mocks.useModelVariants(),
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

describe('IssueModelSelector default-model variant chips', () => {
  it('renders no variant chips for a model that has no variants', () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['openai/gpt-4'], modelVariants: {} },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({})
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: { agent: { model: 'openai/gpt-4' } }, stages: {} })
    renderSelector({ currentModel: 'openai/gpt-4' })

    expect(screen.queryByTestId('issue-coder-model-variant-openai/gpt-4-low')).not.toBeInTheDocument()
    expect(screen.queryByTestId('issue-coder-model-variant-openai/gpt-4-high')).not.toBeInTheDocument()
  })

  it('renders inline variant chips for variant-capable models in the bespoke default popover', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: { agent: { model: 'anthropic/claude' } }, stages: {} })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    await waitFor(() => {
      expect(screen.getByTestId('issue-coder-model-variant-anthropic/claude-low')).toBeInTheDocument()
      expect(screen.getByTestId('issue-coder-model-variant-anthropic/claude-medium')).toBeInTheDocument()
      expect(screen.getByTestId('issue-coder-model-variant-anthropic/claude-high')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('issue-coder-model-variant-openai/gpt-4-low')).not.toBeInTheDocument()
  })

  it('renders no standalone variant dropdown next to the default model selector', () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: { agent: { model: 'anthropic/claude' } }, stages: {} })
    renderSelector({ currentModel: 'anthropic/claude' })

    expect(screen.queryByTestId('issue-coder-model-variant-variant-trigger')).not.toBeInTheDocument()
  })

  it('selects model+variant through the issue API when a default-model chip is clicked', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: { agent: { model: 'anthropic/claude' } }, stages: {} })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const mediumChip = await waitFor(() => screen.getByTestId('issue-coder-model-variant-anthropic/claude-medium'))
    fireEvent.click(mediumChip)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowDefinitionVar).toHaveBeenCalledWith(
        42,
        'agent',
        { type: 'opencode', model: 'anthropic/claude', variant: 'medium' },
        'proj_test',
      )
    })
  })

  it('selects the default variant (no variant) when the model body is clicked', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude', variant: 'high' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const claudeRow = await waitFor(() => document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement)
    fireEvent.pointerDown(claudeRow)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowDefinitionVar).toHaveBeenCalledWith(
        42,
        'agent',
        { type: 'opencode', model: 'anthropic/claude', variant: null },
        'proj_test',
      )
    })
  })

  it('still emits variant: null on issue default select when no variant was previously stored (idempotent delete)', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const claudeRow = await waitFor(() => document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement)
    fireEvent.pointerDown(claudeRow)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowDefinitionVar).toHaveBeenCalledWith(
        42,
        'agent',
        { type: 'opencode', model: 'anthropic/claude', variant: null },
        'proj_test',
      )
    })
    const valueArg = mocks.patchIssueWorkflowDefinitionVar.mock.calls[0]?.[2] as Record<string, unknown>
    expect(valueArg).toHaveProperty('variant', null)
    expect(Object.prototype.hasOwnProperty.call(valueArg, 'variant')).toBe(true)
  })

  it('highlights the active variant chip for the currently selected default model', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude', variant: 'medium' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const activeChip = await waitFor(() => screen.getByTestId('issue-coder-model-variant-anthropic/claude-medium'))
    expect(activeChip.getAttribute('data-variant-active')).toBe('true')
    const lowChip = screen.getByTestId('issue-coder-model-variant-anthropic/claude-low')
    expect(lowChip.getAttribute('data-variant-active')).toBe('false')
  })
})

describe('IssueModelSelector per-stage variant chips', () => {
  it('renders compact variant chips for stages whose model has variants', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: { build: { vars: { agent: { model: 'anthropic/claude' } } } },
    })
    renderSelector({ currentStageModels: { build: 'anthropic/claude' } })

    openAdvanced()
    const buildTrigger = await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement)
    fireEvent.click(buildTrigger)

    await waitFor(() => {
      expect(screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-low')).toBeInTheDocument()
      expect(screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-medium')).toBeInTheDocument()
      expect(screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-high')).toBeInTheDocument()
    })
  })

  it('does not render per-stage chips for stages whose model has no variants', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: {
        build: { vars: { agent: { model: 'anthropic/claude' } } },
        check: { vars: { agent: { model: 'openai/gpt-4' } } },
      },
    })
    renderSelector({ currentStageModels: { build: 'anthropic/claude', check: 'openai/gpt-4' } })

    openAdvanced()
    const buildTrigger = await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement)
    fireEvent.click(buildTrigger)

    await waitFor(() => {
      expect(screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-high')).toBeInTheDocument()
    })
    expect(screen.queryByTestId('issue-stage-model-check-row-openai/gpt-4-variant-low')).not.toBeInTheDocument()
  })

  it('persists the selected per-stage variant through the issue API when a chip is clicked', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: { build: { vars: { agent: { model: 'anthropic/claude' } } } },
    })
    renderSelector({ currentStageModels: { build: 'anthropic/claude' } })

    openAdvanced()
    const buildTrigger = await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement)
    fireEvent.click(buildTrigger)

    const highChip = await waitFor(() => screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-high'))
    fireEvent.click(highChip)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledTimes(1)
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledWith(
        42,
        'build',
        'agent',
        { type: 'opencode', model: 'anthropic/claude', variant: 'high' },
        'proj_test',
      )
    })
  })

  it('persists model and variant when a chip is clicked on an empty per-stage row', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: {}, stages: {} })
    renderSelector()

    openAdvanced()
    const buildTrigger = await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement)
    fireEvent.click(buildTrigger)

    const highChip = await waitFor(() => screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-high'))
    fireEvent.click(highChip)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledTimes(1)
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledWith(
        42,
        'build',
        'agent',
        { type: 'opencode', model: 'anthropic/claude', variant: 'high' },
        'proj_test',
      )
    })
  })

  it('highlights the active per-stage variant chip', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: { build: { vars: { agent: { model: 'anthropic/claude', variant: 'high' } } } },
    })
    renderSelector({ currentStageModels: { build: 'anthropic/claude' } })

    openAdvanced()
    const buildTrigger = await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement)
    fireEvent.click(buildTrigger)

    const activeChip = await waitFor(() => screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-high'))
    expect(activeChip.getAttribute('data-variant-active')).toBe('true')
    const lowChip = screen.getByTestId('issue-stage-model-build-row-anthropic/claude-variant-low')
    expect(lowChip.getAttribute('data-variant-active')).toBe('false')
  })

  it('clears the stage-scoped variant when the per-stage model body is clicked without a variant chip', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: { build: { vars: { agent: { model: 'anthropic/claude', variant: 'max' } } } },
    })
    renderSelector({ currentStageModels: { build: 'anthropic/claude' } })

    openAdvanced()
    const buildTrigger = await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement)
    fireEvent.click(buildTrigger)

    const claudeRow = await waitFor(() =>
      document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement,
    )
    fireEvent.pointerDown(claudeRow)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledTimes(1)
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledWith(
        42,
        'build',
        'agent',
        { type: 'opencode', model: 'anthropic/claude', variant: null },
        'proj_test',
      )
    })
    const valueArg = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls[0]?.[3] as Record<string, unknown>
    expect(valueArg).toHaveProperty('variant', null)
    expect(Object.prototype.hasOwnProperty.call(valueArg, 'variant')).toBe(true)
  })

  it('stage-scoped variant delete only clears the targeted stage variant, leaving sibling stages untouched', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: {
        plan: { vars: { agent: { model: 'anthropic/claude', variant: 'max' } } },
        check: { vars: { agent: { model: 'anthropic/claude', variant: 'high' } } },
      },
    })
    renderSelector({ currentStageModels: { plan: 'anthropic/claude', check: 'anthropic/claude' } })

    openAdvanced()
    const planTrigger = await waitFor(() => document.getElementById('issue-stage-model-plan') as HTMLElement)
    fireEvent.click(planTrigger)

    const claudeRow = await waitFor(() =>
      document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement,
    )
    fireEvent.pointerDown(claudeRow)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledTimes(1)
    })

    expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledWith(
      42,
      'plan',
      'agent',
      { type: 'opencode', model: 'anthropic/claude', variant: null },
      'proj_test',
    )

    const targetedStage = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls[0]?.[1]
    expect(targetedStage).toBe('plan')

    const allStageCalls = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls.map((c) => c[1])
    expect(allStageCalls).not.toContain('check')
  })
})
