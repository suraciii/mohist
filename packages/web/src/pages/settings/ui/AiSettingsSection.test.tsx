// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
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

describe('AiSettingsSection', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  function arrangeLoaded() {
    useOpencodeRuntimeMock.mockReturnValue({ isLoading: false, error: null })
    useAvailableModelIdsMock.mockReturnValue({
      data: ['openai/gpt-4', 'anthropic/claude-3', 'google/gemini-2'],
      isLoading: false,
      error: null,
    })
    useOpencodeModelMock.mockReturnValue({ data: { model: null } })
    useUpdateOpencodeModelMock.mockReturnValue({ mutate: vi.fn() })
    useStageModelsMock.mockReturnValue({ data: { stageModels: null } })
    useSetStageModelsMock.mockReturnValue({ mutate: vi.fn() })
  }

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
