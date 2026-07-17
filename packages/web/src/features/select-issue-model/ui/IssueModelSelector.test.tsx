import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import {
  IssueModelSelector,
  type IssueModelSelectorDependencies,
} from './IssueModelSelector'

const mocks = vi.hoisted(() => ({
  useAvailableModelIds: vi.fn(),
  useOpencodeModel: vi.fn(),
  useModelVariants: vi.fn(() => ({})),
  getIssueWorkflowVariables: vi.fn(),
  patchIssueWorkflowDefinitionVar: vi.fn(),
  patchIssueWorkflowStageDefinitionVar: vi.fn(),
}))

const dependencies = {
  useAvailableModelIds: () => mocks.useAvailableModelIds(),
  useOpencodeModel: () => mocks.useOpencodeModel(),
  useModelVariants: () => mocks.useModelVariants(),
  getIssueWorkflowVariables: mocks.getIssueWorkflowVariables,
  patchIssueWorkflowDefinitionVar: mocks.patchIssueWorkflowDefinitionVar,
  patchIssueWorkflowStageDefinitionVar: mocks.patchIssueWorkflowStageDefinitionVar,
} as unknown as IssueModelSelectorDependencies

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

beforeEach(() => {
  window.localStorage.clear()
  mocks.useOpencodeModel.mockReturnValue({ data: { model: null, variant: null } })
  mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: {}, stages: {} })
  mocks.patchIssueWorkflowDefinitionVar.mockResolvedValue({ vars: { agent: {} }, stages: {} })
  mocks.patchIssueWorkflowStageDefinitionVar.mockResolvedValue({ vars: {}, stages: {} })
})

function renderSelector(props: { currentModel?: string | null; currentStageModels?: Record<string, string> | null } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj_test" initialProjects={[{ id: 'proj_test', name: 'Test', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
        <IssueModelSelector
          issueNumber={42}
          currentModel={props.currentModel ?? null}
          currentStageModels={props.currentStageModels ?? null}
          dependencies={dependencies}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function openAdvanced() {
  fireEvent.click(screen.getByRole('button', { name: /Per-stage overrides/i }))
}

describe('IssueModelSelector default-model variant chips', () => {
  it('renders no variant chips for a model that has no variants', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['openai/gpt-4'], modelVariants: {} },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({})
    const workflowVariables = Promise.resolve({ vars: { agent: { model: 'openai/gpt-4' } }, stages: {} })
    mocks.getIssueWorkflowVariables.mockReturnValue(workflowVariables)
    renderSelector({ currentModel: 'openai/gpt-4' })

    await act(async () => {
      await workflowVariables
    })

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

  it('renders no standalone variant dropdown next to the default model selector', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    const workflowVariables = Promise.resolve({ vars: { agent: { model: 'anthropic/claude' } }, stages: {} })
    mocks.getIssueWorkflowVariables.mockReturnValue(workflowVariables)
    renderSelector({ currentModel: 'anthropic/claude' })

    await act(async () => {
      await workflowVariables
    })

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
        { model: 'anthropic/claude', variant: 'medium' },
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
        { model: 'anthropic/claude', variant: null },
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
        { model: 'anthropic/claude', variant: null },
        'proj_test',
      )
    })
    const valueArg = mocks.patchIssueWorkflowDefinitionVar.mock.calls[0]?.[2] as Record<string, unknown>
    expect(valueArg).toHaveProperty('variant', null)
    expect(Object.prototype.hasOwnProperty.call(valueArg, 'variant')).toBe(true)
  })

  it('clears the issue default override variant when Use default is clicked', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.useOpencodeModel.mockReturnValue({ data: { model: 'openai/gpt-4', variant: null } })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude', variant: 'high' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const clearButton = await waitFor(() => screen.getByRole('button', { name: /Use default/i }))
    fireEvent.click(clearButton)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowDefinitionVar).toHaveBeenCalledWith(
        42,
        'agent',
        { model: null, variant: null },
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
        { model: 'anthropic/claude', variant: 'high' },
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
        { model: 'anthropic/claude', variant: 'high' },
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
        { model: 'anthropic/claude', variant: null },
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
      { model: 'anthropic/claude', variant: null },
      'proj_test',
    )

    const targetedStage = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls[0]?.[1]
    expect(targetedStage).toBe('plan')

    const allStageCalls = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls.map((c) => c[1])
    expect(allStageCalls).not.toContain('check')
  })

  it('clears the stage override variant when a stage override is cleared', async () => {
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
    const clearButton = await waitFor(() => screen.getByTitle('Clear'))
    fireEvent.click(clearButton)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledWith(
        42,
        'build',
        'agent',
        { model: null, variant: null },
        'proj_test',
      )
    })
    const valueArg = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls[0]?.[3] as Record<string, unknown>
    expect(valueArg).toHaveProperty('variant', null)
    expect(Object.prototype.hasOwnProperty.call(valueArg, 'variant')).toBe(true)
  })

  it('clearing a stage override only clears the targeted stage variant', async () => {
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
      stages: {
        plan: { vars: { agent: { model: 'anthropic/claude', variant: 'max' } } },
        check: { vars: { agent: { model: 'anthropic/claude', variant: 'high' } } },
      },
    })
    renderSelector({ currentStageModels: { plan: 'anthropic/claude', check: 'anthropic/claude' } })

    openAdvanced()
    const clearButtons = await waitFor(() => screen.getAllByTitle('Clear'))
    fireEvent.click(clearButtons[0])

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledTimes(1)
    })
    expect(mocks.patchIssueWorkflowStageDefinitionVar).toHaveBeenCalledWith(
      42,
      'plan',
      'agent',
      { model: null, variant: null },
      'proj_test',
    )

    const allStageCalls = mocks.patchIssueWorkflowStageDefinitionVar.mock.calls.map((c) => c[1])
    expect(allStageCalls).not.toContain('check')
  })
})
