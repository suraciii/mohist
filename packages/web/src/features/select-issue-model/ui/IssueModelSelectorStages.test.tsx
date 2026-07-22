import '@testing-library/jest-dom'
import { beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import {
  mocks,
  openAdvanced,
  renderSelector,
  resetIssueModelSelectorTestState,
} from './IssueModelSelectorTestSupport'

beforeEach(() => {
  cleanup()
  resetIssueModelSelectorTestState()
})

describe('IssueModelSelector per-stage variant chips', () => {
  it('uses the stage backend when loading its model catalog', async () => {
    mocks.useAvailableModelIds.mockImplementation((runtime: string) => ({
      data: { models: runtime === 'pi' ? ['pi/anthropic/claude'] : ['openai/gpt-4'], modelVariants: {} },
      isLoading: false,
      error: null,
    }))
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: {},
      stages: { build: { vars: { agent: { runtime: 'opencode' } } } },
    })
    renderSelector()
    openAdvanced()
    const buildRuntime = await waitFor(() => screen.getByTestId('issue-stage-runtime-build'))
    expect(buildRuntime).toHaveValue('opencode')
    fireEvent.click(await waitFor(() => document.getElementById('issue-stage-model-build') as HTMLElement))
    expect(await screen.findByText('gpt-4')).toBeInTheDocument()
    expect(screen.queryByText('claude')).not.toBeInTheDocument()
  })

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
    fireEvent.click(claudeRow)

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
    fireEvent.click(claudeRow)

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
    expect(mocks.patchIssueWorkflowStageDefinitionVar.mock.calls.map((call) => call[1])).not.toContain('check')
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
    expect(mocks.patchIssueWorkflowStageDefinitionVar.mock.calls.map((call) => call[1])).not.toContain('check')
  })
})
