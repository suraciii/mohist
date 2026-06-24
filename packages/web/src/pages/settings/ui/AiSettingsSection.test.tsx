// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

const useOpencodeRuntimeMock = vi.fn()
const useAvailableModelIdsMock = vi.fn()
const useOpencodeModelMock = vi.fn()
const useUpdateOpencodeModelMock = vi.fn()
const useStageModelsMock = vi.fn()
const useSetStageModelsMock = vi.fn()

vi.mock('../../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings')>()
  return {
    ...actual,
    useOpencodeRuntime: () => useOpencodeRuntimeMock(),
    useAvailableModelIds: () => useAvailableModelIdsMock(),
    useOpencodeModel: () => useOpencodeModelMock(),
    useUpdateOpencodeModel: () => useUpdateOpencodeModelMock(),
    useStageModels: () => useStageModelsMock(),
    useSetStageModels: () => useSetStageModelsMock(),
  }
})

import { AiSettingsSection } from './AiSettingsSection'

function renderSection() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <AiSettingsSection />
    </QueryClientProvider>,
  )
}

interface ArrangeOptions {
  models?: string[]
  modelVariants?: Record<string, string[]>
  defaultModel?: string | null
  defaultVariant?: string | null
  stageModels?: Record<string, string> | null
  stageModelVariants?: Record<string, string> | null
}

function arrangeLoaded(options: ArrangeOptions = {}) {
  const models = options.models ?? ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2']
  const modelVariants = options.modelVariants ?? {}
  useOpencodeRuntimeMock.mockReturnValue({ isLoading: false, error: null })
  useAvailableModelIdsMock.mockReturnValue({
    data: { models, modelVariants },
    isLoading: false,
    error: null,
  })
  useOpencodeModelMock.mockReturnValue({
    data: { model: options.defaultModel ?? null, variant: options.defaultVariant ?? null },
  })
  useUpdateOpencodeModelMock.mockReturnValue({ mutate: vi.fn() })
  useStageModelsMock.mockReturnValue({
    data: {
      stageModels: options.stageModels ?? null,
      stageModelVariants: options.stageModelVariants ?? null,
    },
  })
  useSetStageModelsMock.mockReturnValue({ mutate: vi.fn() })
}

describe('AiSettingsSection', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('does not render the Runtime/Command/Models summary block', () => {
    arrangeLoaded()
    renderSection()

    expect(screen.queryByText('Runtime')).not.toBeInTheDocument()
    expect(screen.queryByText('Command')).not.toBeInTheDocument()
    expect(screen.queryByText('Models')).not.toBeInTheDocument()
    expect(
      screen.queryByText(/does not configure AI providers/i),
    ).not.toBeInTheDocument()
  })

  it('keeps the Default Coder Agent Model ModelSelect and shows the model-count hint', () => {
    arrangeLoaded()
    renderSection()

    expect(screen.getByText('Default Coder Agent Model')).toBeInTheDocument()
    expect(screen.getByText('3 models available')).toBeInTheDocument()
    expect(screen.getByText('Opencode default')).toBeInTheDocument()
  })

  it('keeps the Stage Model Overrides section available', () => {
    arrangeLoaded()
    renderSection()

    expect(screen.getByRole('button', { name: /Stage Model Overrides/i })).toBeInTheDocument()
  })

  it('exposes and updates the Stage Model Overrides disclosure state', async () => {
    arrangeLoaded()
    const user = userEvent.setup()
    renderSection()

    const button = screen.getByRole('button', { name: /Stage Model Overrides/i })
    expect(button).toHaveAttribute('aria-expanded', 'false')
    expect(button).toHaveAttribute('aria-controls', 'settings-stage-model-overrides')
    expect(document.getElementById('settings-stage-model-overrides')).not.toBeInTheDocument()

    await user.keyboard('[Tab]')
    await user.keyboard('[Tab]')
    expect(button).toHaveFocus()
    await user.keyboard('[Enter]')

    expect(button).toHaveAttribute('aria-expanded', 'true')
    expect(document.getElementById('settings-stage-model-overrides')).toBeInTheDocument()

    await user.keyboard(' ')
    expect(button).toHaveAttribute('aria-expanded', 'false')
    expect(document.getElementById('settings-stage-model-overrides')).not.toBeInTheDocument()
  })

  it('moves focus into ModelSelect search and supports Escape and arrow keys', async () => {
    arrangeLoaded()
    const user = userEvent.setup()
    renderSection()

    const defaultModelButton = screen.getByRole('button', { name: /Default Coder Agent Model/i })
    await user.click(defaultModelButton)

    const searchInput = await screen.findByPlaceholderText('Search models...')
    expect(searchInput).toHaveFocus()

    await user.keyboard('[ArrowDown]')
    expect(screen.getByRole('button', { name: /gemini-2/i })).toHaveClass('bg-blue-50')

    await user.keyboard('[Escape]')
    expect(screen.queryByPlaceholderText('Search models...')).not.toBeInTheDocument()
    expect(defaultModelButton).toHaveFocus()
  })

  it('lists executable pipeline stages including integrate and excluding fix', async () => {
    arrangeLoaded({
      stageModels: { plan: 'openai/gpt-4', build: 'openai/gpt-4', check: 'openai/gpt-4', integrate: 'openai/gpt-4' },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))

    const overrideRegion = document.getElementById('settings-stage-model-overrides')!
    expect(overrideRegion.textContent).toMatch(/integrate/)
    expect(overrideRegion.textContent).not.toMatch(/\bfix\b/)

    expect(document.getElementById('settings-stage-model-plan')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-build')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-check')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-integrate')).toBeInTheDocument()
    expect(document.getElementById('settings-stage-model-fix')).toBeNull()
  })
})

describe('AiSettingsSection inline variant chips', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('does not render a standalone variant picker next to the default model selector', () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high', 'max'] },
    })
    renderSection()

    expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders inline variant chips on the default model row when the model has variants', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high', 'max'] },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))

    await waitFor(() => {
      for (const variant of ['low', 'medium', 'high', 'max']) {
        expect(
          document.querySelector(`[data-testid="settings-default-model-row-anthropic/claude-3-variant-${variant}"]`),
        ).toBeInTheDocument()
      }
    })
  })

  it('does not render variant chips on rows whose model has no variants', async () => {
    arrangeLoaded({
      modelVariants: { 'openai/gpt-4': [], 'anthropic/claude-3': ['low', 'high'] },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))

    await waitFor(() => {
      expect(
        document.querySelector('[data-testid="settings-default-model-row-anthropic/claude-3-variant-low"]'),
      ).toBeInTheDocument()
    })
    expect(
      document.querySelector('[data-testid="settings-default-model-row-openai/gpt-4-variant-low"]'),
    ).toBeNull()
  })

  it('highlights the stored default variant as the active chip', () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'high',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high', 'max'] },
    })
    renderSection()

    const trigger = screen.getByRole('button', { name: /Default Coder Agent Model/i })
    expect(trigger.textContent).toContain('high')
  })

  it('does not mark any chip active when the stored default variant is not in the model variants', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'ultra',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))

    const highChip = await waitFor(() =>
      screen.getByTestId('settings-default-model-row-anthropic/claude-3-variant-high'),
    )
    expect(highChip).toBeInTheDocument()
    expect(highChip.getAttribute('data-variant-active')).toBe('false')
  })

  it('persists only model and variant through the default-model mutation when a chip is clicked', async () => {
    const mutateMock = vi.fn()
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    useUpdateOpencodeModelMock.mockReturnValue({ mutate: mutateMock })
    const user = userEvent.setup()
    renderSection()

    await user.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))
    const highChip = await screen.findByTestId(
      'settings-default-model-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(mutateMock).toHaveBeenCalledTimes(1)
    })
    expect(mutateMock).toHaveBeenCalledWith({ model: 'anthropic/claude-3', variant: 'high' })
    expect(screen.queryByPlaceholderText('Search models...')).not.toBeInTheDocument()
  })

  it('persists the clicked model and variant when the default model is unset', async () => {
    const mutateMock = vi.fn()
    arrangeLoaded({
      defaultModel: null,
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    useUpdateOpencodeModelMock.mockReturnValue({ mutate: mutateMock })
    const user = userEvent.setup()
    renderSection()

    await user.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))
    const highChip = await screen.findByTestId(
      'settings-default-model-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(mutateMock).toHaveBeenCalledTimes(1)
    })
    expect(mutateMock).toHaveBeenCalledWith({ model: 'anthropic/claude-3', variant: 'high' })
  })

  it('persists the clicked model and variant when choosing a different default model', async () => {
    const mutateMock = vi.fn()
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    useUpdateOpencodeModelMock.mockReturnValue({ mutate: mutateMock })
    const user = userEvent.setup()
    renderSection()

    await user.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))
    const highChip = await screen.findByTestId(
      'settings-default-model-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(mutateMock).toHaveBeenCalledTimes(1)
    })
    expect(mutateMock).toHaveBeenCalledWith({ model: 'anthropic/claude-3', variant: 'high' })
  })

  it('does not render inline variant chips on a default model row whose model has no variants', async () => {
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      modelVariants: { 'openai/gpt-4': [] },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Default Coder Agent Model/i }))

    await waitFor(() => {
      expect(
        document.querySelector('[data-model-id="openai/gpt-4"]'),
      ).toBeInTheDocument()
    })
    expect(
      document.querySelector('[data-testid="settings-default-model-row-openai/gpt-4-variant-low"]'),
    ).toBeNull()
    expect(document.querySelectorAll('[data-variant-chip]').length).toBe(0)
  })

  it('does not render variant chips on per-stage rows whose stage model has no variants', async () => {
    arrangeLoaded({
      stageModels: { build: 'openai/gpt-4' },
      modelVariants: { 'openai/gpt-4': [], 'anthropic/claude-3': ['low', 'high'] },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))
    fireEvent.click(document.getElementById('settings-stage-model-build')!)

    await waitFor(() => {
      expect(
        document.querySelector('[data-model-id="openai/gpt-4"]'),
      ).toBeInTheDocument()
    })
    expect(
      document.querySelector('[data-testid="settings-stage-model-build-row-openai/gpt-4-variant-low"]'),
    ).toBeNull()
    expect(screen.queryByTestId('settings-stage-model-build-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders inline compact variant chips on a per-stage row whose model has variants', async () => {
    arrangeLoaded({
      stageModels: { build: 'anthropic/claude-3' },
      stageModelVariants: { build: 'medium' },
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    renderSection()

    fireEvent.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))
    fireEvent.click(document.getElementById('settings-stage-model-build')!)

    const mediumChip = await waitFor(() =>
      screen.getByTestId('settings-stage-model-build-row-anthropic/claude-3-variant-medium'),
    )
    expect(mediumChip).toBeInTheDocument()
    expect(mediumChip.getAttribute('data-variant-active')).toBe('true')
  })

  it('persists only model and variant through the stage-model mutation when a per-stage chip is clicked', async () => {
    const mutateMock = vi.fn()
    arrangeLoaded({
      stageModels: { build: 'anthropic/claude-3' },
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    useSetStageModelsMock.mockReturnValue({ mutate: mutateMock })
    const user = userEvent.setup()
    renderSection()

    await user.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))
    await user.click(document.getElementById('settings-stage-model-build')!)

    const highChip = await screen.findByTestId(
      'settings-stage-model-build-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(mutateMock).toHaveBeenCalledTimes(1)
    })
    expect(mutateMock).toHaveBeenCalledWith({ stage: 'build', model: 'anthropic/claude-3', variant: 'high' })
  })

  it('persists model and variant when a chip is clicked on an unset stage row', async () => {
    const mutateMock = vi.fn()
    arrangeLoaded({
      stageModels: null,
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    useSetStageModelsMock.mockReturnValue({ mutate: mutateMock })
    const user = userEvent.setup()
    renderSection()

    await user.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))
    await user.click(document.getElementById('settings-stage-model-build')!)

    const highChip = await screen.findByTestId(
      'settings-stage-model-build-row-anthropic/claude-3-variant-high',
    )
    await user.click(highChip)

    await waitFor(() => {
      expect(mutateMock).toHaveBeenCalledTimes(1)
    })
    expect(mutateMock).toHaveBeenCalledWith({ stage: 'build', model: 'anthropic/claude-3', variant: 'high' })
  })
})
