import '@testing-library/jest-dom'
import { beforeEach, describe, expect, it } from 'vitest'
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { mocks, renderSelector, resetIssueModelSelectorTestState } from './IssueModelSelectorTestSupport'

beforeEach(() => {
  cleanup()
  resetIssueModelSelectorTestState()
})

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
    fireEvent.click(claudeRow)

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
    fireEvent.click(claudeRow)

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

    const clearOption = await waitFor(() => {
      const items = document.querySelectorAll('[cmdk-item]')
      const found = Array.from(items).find(
        (el) => el.textContent?.includes('Use default'),
      )
      if (!found) throw new Error('clear option not found')
      return found as HTMLElement
    })
    fireEvent.click(clearOption)

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

describe('IssueModelSelector default-model popover accessibility and keyboard', () => {
  it('selects on click and closes the popover', async () => {
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
    fireEvent.click(claudeRow)

    await waitFor(() => {
      expect(mocks.patchIssueWorkflowDefinitionVar).toHaveBeenCalledWith(
        42,
        'agent',
        { model: 'anthropic/claude', variant: null },
        'proj_test',
      )
    })
  })

  it('pointerDown alone does NOT select', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['anthropic/claude', 'openai/gpt-4'], modelVariants: {} },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const gptRow = await waitFor(() => document.querySelector('[data-model-id="openai/gpt-4"]') as HTMLElement)
    fireEvent.pointerDown(gptRow)

    expect(mocks.patchIssueWorkflowDefinitionVar).not.toHaveBeenCalled()
  })

  it('Escape closes the popover without changing selection', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['anthropic/claude', 'openai/gpt-4'], modelVariants: {} },
      isLoading: false,
      error: null,
    })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const search = await waitFor(() => screen.getByPlaceholderText('Search models...'))
    fireEvent.keyDown(search, { key: 'Escape' })

    await waitFor(() => {
      expect(screen.queryByPlaceholderText('Search models...')).toBeNull()
    })
    expect(mocks.patchIssueWorkflowDefinitionVar).not.toHaveBeenCalled()
  })

  it('exposes combobox/listbox/option roles on the default-model popover', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'anthropic/claude', variant: 'medium' } },
      stages: {},
    })
    renderSelector({ currentModel: 'anthropic/claude' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    const combobox = await waitFor(() => screen.getByRole('combobox'))
    expect(combobox).toBeTruthy()
    expect(combobox.getAttribute('aria-expanded')).toBe('true')

    const listbox = screen.getByRole('listbox')
    expect(listbox).toBeTruthy()

    const options = await waitFor(() => screen.getAllByRole('option'))
    expect(options.length).toBeGreaterThanOrEqual(2)

    const claudeOption = options.find(
      (o) => o.getAttribute('data-model-id') === 'anthropic/claude',
    )
    expect(claudeOption).toBeTruthy()
    expect(claudeOption!.getAttribute('aria-selected')).toBe('true')
  })

  it('renders Override, Recent, and All-models sections as labeled groups', async () => {
    window.localStorage.setItem(
      'mohist:recent-issue-models',
      JSON.stringify(['openai/gpt-4']),
    )
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: {},
      },
      isLoading: false,
      error: null,
    })
    mocks.useOpencodeModel.mockReturnValue({ data: { model: 'anthropic/claude', variant: null } })
    mocks.getIssueWorkflowVariables.mockResolvedValue({
      vars: { agent: { model: 'openai/gpt-4' } },
      stages: {},
    })
    renderSelector({ currentModel: 'openai/gpt-4' })

    const trigger = await waitFor(() => screen.getByTestId('issue-coder-model-trigger'))
    fireEvent.click(trigger)

    await waitFor(() => {
      const groups = document.querySelectorAll('[cmdk-group-heading]')
      const headings = Array.from(groups).map((g) => g.textContent)
      expect(headings).toContain('Override')
      expect(headings).toContain('Recent')
    })
  })

  it('chip click on default-model popover persists model+variant through patchIssueWorkflowDefinitionVar', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.useModelVariants.mockReturnValue({ 'anthropic/claude': ['low', 'medium', 'high'] })
    mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: {}, stages: {} })
    renderSelector()

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
})
