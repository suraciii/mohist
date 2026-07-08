// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LabelCatalogSection } from './LabelCatalogSection'
import type { LabelDefinition } from '../../../entities/label-catalog'

const useLabelCatalogMock = vi.fn()
const useCreateLabelDefinitionMock = vi.fn()
const useUpdateLabelDefinitionMock = vi.fn()
const useDeleteLabelDefinitionMock = vi.fn()

const createMutateMock = vi.fn()
const updateMutateMock = vi.fn()
const removeMutateMock = vi.fn()

vi.mock('../../../entities/label-catalog', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/label-catalog')>()
  return {
    ...actual,
    useLabelCatalog: () => useLabelCatalogMock(),
    useCreateLabelDefinition: () => useCreateLabelDefinitionMock(),
    useUpdateLabelDefinition: () => useUpdateLabelDefinitionMock(),
    useDeleteLabelDefinition: () => useDeleteLabelDefinitionMock(),
  }
})

function renderSection() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <LabelCatalogSection />
    </QueryClientProvider>,
  )
}

const refactorDef: LabelDefinition = {
  key: 'refactor',
  description: 'A refactoring task',
}

const moduleDef: LabelDefinition = {
  key: 'module',
  description: 'Classifies the subsystem',
  supportedValues: ['auth', 'ui'],
}

describe('LabelCatalogSection', () => {
  beforeEach(() => {
    useLabelCatalogMock.mockReset()
    useCreateLabelDefinitionMock.mockReset()
    useUpdateLabelDefinitionMock.mockReset()
    useDeleteLabelDefinitionMock.mockReset()
    createMutateMock.mockReset()
    updateMutateMock.mockReset()
    removeMutateMock.mockReset()

    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })
    useCreateLabelDefinitionMock.mockReturnValue({
      mutate: createMutateMock,
      isPending: false,
    })
    useUpdateLabelDefinitionMock.mockReturnValue({
      mutate: updateMutateMock,
      isPending: false,
    })
    useDeleteLabelDefinitionMock.mockReturnValue({
      mutate: removeMutateMock,
      isPending: false,
    })
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders the empty state when the catalog has no entries', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    expect(screen.getByText(/Define the labels your project suggests/i)).toBeInTheDocument()
    expect(screen.getAllByText(/No label definitions yet/i).length).toBeGreaterThan(0)
  })

  it('lists every catalog entry with key, description, and supportedValues', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [refactorDef, moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    expect(screen.getByTestId('label-catalog-key-refactor')).toHaveTextContent('refactor')
    expect(screen.getByTestId('label-catalog-description-refactor')).toHaveTextContent('A refactoring task')

    expect(screen.getByTestId('label-catalog-key-module')).toHaveTextContent('module')
    expect(screen.getByTestId('label-catalog-description-module')).toHaveTextContent('Classifies the subsystem')
    expect(screen.getByTestId('label-catalog-value-module-auth')).toBeInTheDocument()
    expect(screen.getByTestId('label-catalog-value-module-ui')).toBeInTheDocument()
  })

  it('shows edit and delete actions for entries', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    expect(screen.getByTestId('label-catalog-delete-button-module')).toBeInTheDocument()
  })

  it('adds a definition via POST when the form is submitted', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'Classifies the subsystem' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-values'), {
      target: { value: 'auth, ui' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(createMutateMock).toHaveBeenCalledTimes(1)
    const call = createMutateMock.mock.calls[0][0]
    expect(call).toEqual({
      key: 'module',
      description: 'Classifies the subsystem',
      supportedValues: ['auth', 'ui'],
    })
  })

  it('rejects an invalid key (uppercase) with an in-page validation error', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'Module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/lowercase alphanumerics/)
    expect(createMutateMock).not.toHaveBeenCalled()
  })

  it('rejects a leading-dash key with an in-page validation error', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: '-mod' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/lowercase alphanumerics/)
    expect(createMutateMock).not.toHaveBeenCalled()
  })

  it('rejects a whitespace-only description with an in-page validation error', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: '   ' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/non-empty/)
    expect(createMutateMock).not.toHaveBeenCalled()
  })

  it('rejects supportedValues containing only empty entries', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-values'), {
      target: { value: ', ,' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/at least one supported value/i)
    expect(createMutateMock).not.toHaveBeenCalled()
  })

  it('rejects supportedValues with mixed empty comma entries before adding', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-values'), {
      target: { value: 'auth,,ui' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/empty entries/i)
    expect(createMutateMock).not.toHaveBeenCalled()
  })

  it('opens the edit form with the key field read-only and pre-filled values', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))

    expect(screen.getByTestId('label-catalog-edit-module')).toBeInTheDocument()
    const keyInput = screen.getByTestId('label-catalog-edit-key-module') as HTMLInputElement
    expect(keyInput.value).toBe('module')
    expect(keyInput.readOnly).toBe(true)
    expect(keyInput.disabled).toBe(true)
    expect((screen.getByTestId('label-catalog-edit-description-module') as HTMLInputElement).value).toBe(
      'Classifies the subsystem',
    )
    expect((screen.getByTestId('label-catalog-edit-values-module') as HTMLTextAreaElement).value).toBe(
      'auth, ui',
    )
  })

  it('PATCHes the description and supportedValues on save', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))

    fireEvent.change(screen.getByTestId('label-catalog-edit-description-module'), {
      target: { value: 'updated description' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-edit-values-module'), {
      target: { value: 'core, infra' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    expect(updateMutateMock).toHaveBeenCalledTimes(1)
    const [payload, opts] = updateMutateMock.mock.calls[0]
    expect(payload).toEqual({
      key: 'module',
      patch: { description: 'updated description', supportedValues: ['core', 'infra'] },
    })
    expect(typeof opts.onSuccess).toBe('function')
  })

  it('rejects a whitespace-only description in edit form before sending PATCH', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.change(screen.getByTestId('label-catalog-edit-description-module'), {
      target: { value: '   ' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    expect(screen.getByTestId('label-catalog-edit-error-module')).toHaveTextContent(/non-empty/)
    expect(updateMutateMock).not.toHaveBeenCalled()
  })

  it('rejects supportedValues with blank newline entries before editing', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.change(screen.getByTestId('label-catalog-edit-values-module'), {
      target: { value: 'auth\n\nui' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    expect(screen.getByTestId('label-catalog-edit-error-module')).toHaveTextContent(/empty entries/i)
    expect(updateMutateMock).not.toHaveBeenCalled()
  })

  it('sends PATCH with supportedValues:[] to clear values when the textarea is emptied', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.change(screen.getByTestId('label-catalog-edit-values-module'), {
      target: { value: '' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    expect(updateMutateMock).toHaveBeenCalledTimes(1)
    const [payload] = updateMutateMock.mock.calls[0]
    expect(payload.patch).toEqual({ description: 'Classifies the subsystem', supportedValues: [] })
  })

  it('deletes an entry via DELETE only after the shared AlertDialog is confirmed (T-002)', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-delete-button-module'))

    const dialog = screen.getByTestId('label-catalog-delete-alert')
    expect(dialog).toBeInTheDocument()
    expect(dialog).toHaveAttribute('data-tone', 'destructive')

    expect(removeMutateMock).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('label-catalog-delete-alert-confirm'))

    expect(removeMutateMock).toHaveBeenCalledTimes(1)
    expect(removeMutateMock.mock.calls[0][0]).toBe('module')
  })

  it('does not invoke the delete mutation when the AlertDialog is cancelled (T-002)', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-delete-button-module'))

    const dialog = screen.getByTestId('label-catalog-delete-alert')
    expect(dialog).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('label-catalog-delete-alert-cancel'))

    expect(removeMutateMock).not.toHaveBeenCalled()
  })

  it('renders a single shared AlertDialog instance for the whole section, not per row (T-002)', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef, refactorDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-delete-button-module'))
    const dialog = screen.getByTestId('label-catalog-delete-alert')
    expect(dialog).toBeInTheDocument()

    const allDialogs = document.querySelectorAll('[data-testid="label-catalog-delete-alert"]')
    expect(allDialogs).toHaveLength(1)
  })

  it('surfaces server errors from create mutation as an in-page alert', async () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })
    createMutateMock.mockImplementation((_input: unknown, opts: { onError?: (err: Error) => void }) => {
      opts.onError?.(new Error("Key 'module' already exists in the project catalog."))
    })
    useCreateLabelDefinitionMock.mockReturnValue({
      mutate: createMutateMock,
      isPending: false,
    })

    renderSection()

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-page-error')).toHaveTextContent(
        "Key 'module' already exists in the project catalog.",
      )
    })
  })

  it('surfaces server errors from update mutation inside the edit form', async () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })
    updateMutateMock.mockImplementation((_payload: unknown, opts: { onError?: (err: Error) => void }) => {
      opts.onError?.(new Error("Key 'module' not found in the project catalog."))
    })
    useUpdateLabelDefinitionMock.mockReturnValue({
      mutate: updateMutateMock,
      isPending: false,
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-error-module')).toHaveTextContent('not found')
    })
  })

  it('shows the loading skeleton while catalog is loading', () => {
    useLabelCatalogMock.mockReturnValue({ data: undefined, isLoading: true, isError: false, refetch: vi.fn() })

    renderSection()

    expect(screen.getByRole('status')).toBeInTheDocument()
  })

  it('wires aria-invalid + aria-describedby only on the invalid add-form field (T-003)', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    const keyInput = screen.getByTestId('label-catalog-add-key')
    const descriptionInput = screen.getByTestId('label-catalog-add-description')
    const valuesInput = screen.getByTestId('label-catalog-add-values')

    expect(keyInput).not.toHaveAttribute('aria-invalid')
    expect(keyInput).not.toHaveAttribute('aria-describedby')

    fireEvent.change(keyInput, { target: { value: 'Module' } })
    fireEvent.change(descriptionInput, { target: { value: 'desc' } })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    const errorEl = screen.getByTestId('label-catalog-add-error')
    expect(errorEl).toHaveAttribute('role', 'alert')
    expect(errorEl.className).toContain('text-red-700')

    expect(keyInput).toHaveAttribute('aria-invalid', 'true')
    expect(keyInput.getAttribute('aria-describedby')).toBe('label-catalog-add-error')
    expect(descriptionInput).not.toHaveAttribute('aria-invalid')
    expect(descriptionInput).not.toHaveAttribute('aria-describedby')
    expect(valuesInput).not.toHaveAttribute('aria-invalid')
    expect(valuesInput).not.toHaveAttribute('aria-describedby')
  })

  it('wires aria-invalid + aria-describedby only on the invalid edit-form field (T-003)', () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.change(screen.getByTestId('label-catalog-edit-description-module'), {
      target: { value: '   ' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    const errorEl = screen.getByTestId('label-catalog-edit-error-module')
    expect(errorEl).toHaveAttribute('role', 'alert')
    expect(errorEl.className).toContain('text-red-700')

    const descriptionInput = screen.getByTestId('label-catalog-edit-description-module')
    const valuesInput = screen.getByTestId('label-catalog-edit-values-module')

    expect(descriptionInput).toHaveAttribute('aria-invalid', 'true')
    expect(descriptionInput.getAttribute('aria-describedby')).toBe('label-catalog-edit-error-module')
    expect(valuesInput).not.toHaveAttribute('aria-invalid')
    expect(valuesInput).not.toHaveAttribute('aria-describedby')
  })

  it('shows edit server errors as form-level alerts without marking every edit field invalid (T-003)', async () => {
    useLabelCatalogMock.mockReturnValue({
      data: [moduleDef],
      isLoading: false,
      isError: false,
      refetch: vi.fn(),
    })
    updateMutateMock.mockImplementation((_payload: unknown, opts: { onError?: (err: Error) => void }) => {
      opts.onError?.(new Error("Key 'module' not found in the project catalog."))
    })

    renderSection()

    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    const errorEl = await screen.findByTestId('label-catalog-edit-error-module')
    expect(errorEl).toHaveTextContent(/not found/i)

    const descriptionInput = screen.getByTestId('label-catalog-edit-description-module')
    const valuesInput = screen.getByTestId('label-catalog-edit-values-module')

    expect(descriptionInput).not.toHaveAttribute('aria-invalid')
    expect(descriptionInput).not.toHaveAttribute('aria-describedby')
    expect(valuesInput).not.toHaveAttribute('aria-invalid')
    expect(valuesInput).not.toHaveAttribute('aria-describedby')
  })

  it('renders an inline New definition CTA when the catalog is empty (T-006)', () => {
    useLabelCatalogMock.mockReturnValue({ data: [], isLoading: false, isError: false, refetch: vi.fn() })

    renderSection()

    const newButton = screen.getByTestId('label-catalog-empty-new-button')
    expect(newButton).toBeInTheDocument()
    expect(newButton).toHaveTextContent(/New definition/)
  })

  describe('Search input (T-007)', () => {
    it('renders a search input with an accessible label when a project is selected', () => {
      useLabelCatalogMock.mockReturnValue({
        data: [moduleDef, refactorDef],
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderSection()

      const search = screen.getByTestId('label-catalog-search')
      expect(search).toBeInTheDocument()
      expect(search.tagName).toBe('INPUT')
      expect(search.getAttribute('aria-label')).toMatch(/search/i)
      expect(search.getAttribute('placeholder')).not.toBeNull()
    })

    it('filters the displayed list by key when a matching query is entered', () => {
      useLabelCatalogMock.mockReturnValue({
        data: [moduleDef, refactorDef],
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderSection()

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'module' },
      })

      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
    })

    it('filters the displayed list by description when a matching query is entered', () => {
      useLabelCatalogMock.mockReturnValue({
        data: [moduleDef, refactorDef],
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderSection()

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'subsystem' },
      })

      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
    })

    it('filters the displayed list by a supported value when a matching query is entered', () => {
      useLabelCatalogMock.mockReturnValue({
        data: [moduleDef, refactorDef],
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderSection()

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'auth' },
      })

      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
    })

    it('restores the full list when the search query is cleared', () => {
      useLabelCatalogMock.mockReturnValue({
        data: [moduleDef, refactorDef],
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderSection()

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'module' },
      })
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: '' },
      })
      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.getByTestId('label-catalog-row-refactor')).toBeInTheDocument()
    })

    it('shows a "no matches" message without the inline New definition CTA when the search filter is the only reason the list is empty', () => {
      useLabelCatalogMock.mockReturnValue({
        data: [moduleDef, refactorDef],
        isLoading: false,
        isError: false,
        refetch: vi.fn(),
      })

      renderSection()

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'no-such-key' },
      })

      expect(screen.queryByTestId('label-catalog-row-module')).not.toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
      expect(screen.getAllByText(/No label definitions match the current search/i).length).toBeGreaterThan(0)
      expect(screen.queryByTestId('label-catalog-empty-new-button')).not.toBeInTheDocument()
    })
  })
})
