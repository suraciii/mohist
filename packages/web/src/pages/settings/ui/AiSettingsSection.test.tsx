// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
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
})
