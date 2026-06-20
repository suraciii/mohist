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
})

describe('AiSettingsSection variant picker', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('hides the default-model variant picker when the selected model has no variants', () => {
    arrangeLoaded({
      defaultModel: 'openai/gpt-4',
      modelVariants: { 'openai/gpt-4': [] },
    })
    renderSection()

    expect(screen.queryByTestId('settings-default-model-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders the default-model variant picker with only the selected model variants', async () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      modelVariants: {
        'anthropic/claude-3': ['low', 'medium', 'high', 'max'],
      },
    })
    renderSection()

    const trigger = screen.getByTestId('settings-default-model-variant-trigger')
    expect(trigger).toBeInTheDocument()
    fireEvent.click(trigger)

    const list = await waitFor(() => screen.getByRole('listbox'))
    const opts = Array.from(list.querySelectorAll('[role="option"]')).map((el) => el.textContent?.trim())
    expect(opts).toEqual(['Default', 'low', 'medium', 'high', 'max'])
  })

  it('shows the stored default variant as the selected value when supported', () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'high',
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    renderSection()

    const trigger = screen.getByTestId('settings-default-model-variant-trigger')
    expect(trigger).toHaveTextContent('high')
  })

  it('drops an unsupported stored variant from the picker label', () => {
    arrangeLoaded({
      defaultModel: 'anthropic/claude-3',
      defaultVariant: 'ultra',
      modelVariants: { 'anthropic/claude-3': ['low', 'high'] },
    })
    renderSection()

    const trigger = screen.getByTestId('settings-default-model-variant-trigger')
    expect(trigger).toHaveTextContent('Variant')
  })

  it('hides the per-stage variant picker when the stage model is absent from modelVariants', () => {
    arrangeLoaded({
      stageModels: { build: 'openai/gpt-4' },
      stageModelVariants: {},
      modelVariants: { 'anthropic/claude-3': ['low', 'high'] },
    })
    renderSection()
    fireEvent.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))

    expect(screen.queryByTestId('settings-stage-model-build-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders the per-stage variant picker only for the stage whose model has variants', () => {
    arrangeLoaded({
      stageModels: { build: 'anthropic/claude-3', check: 'openai/gpt-4' },
      modelVariants: {
        'anthropic/claude-3': ['low', 'medium', 'high'],
        'openai/gpt-4': [],
      },
    })
    renderSection()
    fireEvent.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))

    expect(screen.getByTestId('settings-stage-model-build-variant-trigger')).toBeInTheDocument()
    expect(screen.queryByTestId('settings-stage-model-check-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders the per-stage variant picker with the stored stage variant when supported', () => {
    arrangeLoaded({
      stageModels: { build: 'anthropic/claude-3' },
      stageModelVariants: { build: 'medium' },
      modelVariants: { 'anthropic/claude-3': ['low', 'medium', 'high'] },
    })
    renderSection()
    fireEvent.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))

    const trigger = screen.getByTestId('settings-stage-model-build-variant-trigger')
    expect(trigger).toHaveTextContent('medium')
  })
})
