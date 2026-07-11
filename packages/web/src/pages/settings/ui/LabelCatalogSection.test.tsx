import '@testing-library/jest-dom'
import { fireEvent, render, screen, waitFor } from '../../../../tests/test-utils'
import { http, HttpResponse } from 'msw'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useMswServer } from '../../../../tests/support/msw'
import { LabelCatalogSection } from './LabelCatalogSection'
import type { LabelDefinition, LabelDefinitionInput, LabelDefinitionPatch } from '../../../entities/label-catalog'

let _catalogData: LabelDefinition[] = []
let _createError: string | null = null
let _updateError: string | null = null
const createCaptures: LabelDefinitionInput[] = []
const updateCaptures: Array<{ key: string; patch: LabelDefinitionPatch }> = []
const deleteCaptures: string[] = []

useMswServer(
  http.get('/api/projects/:projectId/labels/catalog', () =>
    HttpResponse.json({ success: true, data: _catalogData }),
  ),
  http.post('/api/projects/:projectId/labels/catalog', async ({ request }) => {
    const body = await request.json() as LabelDefinitionInput
    createCaptures.push(body)
    if (_createError) {
      return HttpResponse.json({ success: false, error: _createError }, { status: 400 })
    }
    return HttpResponse.json({ success: true, data: body })
  }),
  http.patch('/api/projects/:projectId/labels/catalog/:key', async ({ params, request }) => {
    const key = params.key as string
    const body = await request.json() as LabelDefinitionPatch
    updateCaptures.push({ key, patch: body })
    if (_updateError) {
      return HttpResponse.json({ success: false, error: _updateError }, { status: 400 })
    }
    return HttpResponse.json({ success: true, data: { key, ...body } })
  }),
  http.delete('/api/projects/:projectId/labels/catalog/:key', ({ params }) => {
    deleteCaptures.push(params.key as string)
    return new HttpResponse(null, { status: 204 })
  }),
)

function renderSection() {
  return render(<LabelCatalogSection />)
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
    _catalogData = []
    _createError = null
    _updateError = null
    createCaptures.length = 0
    updateCaptures.length = 0
    deleteCaptures.length = 0
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('renders the empty state when the catalog has no entries', async () => {
    renderSection()

    expect(await screen.findByText(/Define the labels your project suggests/i)).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getAllByText(/No label definitions yet/i).length).toBeGreaterThan(0)
    })
  })

  it('lists every catalog entry with key, description, and supportedValues', async () => {
    _catalogData = [refactorDef, moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-key-refactor')).toBeInTheDocument()
    })

    expect(screen.getByTestId('label-catalog-key-refactor')).toHaveTextContent('refactor')
    expect(screen.getByTestId('label-catalog-description-refactor')).toHaveTextContent('A refactoring task')

    expect(screen.getByTestId('label-catalog-key-module')).toHaveTextContent('module')
    expect(screen.getByTestId('label-catalog-description-module')).toHaveTextContent('Classifies the subsystem')
    expect(screen.getByTestId('label-catalog-value-module-auth')).toBeInTheDocument()
    expect(screen.getByTestId('label-catalog-value-module-ui')).toBeInTheDocument()
  })

  it('shows edit and delete actions for entries', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
    expect(screen.getByTestId('label-catalog-delete-button-module')).toBeInTheDocument()
  })

  it('adds a definition via POST when the form is submitted', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

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

    await waitFor(() => expect(createCaptures).toHaveLength(1))
    expect(createCaptures[0]).toEqual({
      key: 'module',
      description: 'Classifies the subsystem',
      supportedValues: ['auth', 'ui'],
    })
  })

  it('rejects an invalid key (uppercase) with an in-page validation error', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'Module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/lowercase alphanumerics/)
    expect(createCaptures).toHaveLength(0)
  })

  it('rejects a leading-dash key with an in-page validation error', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: '-mod' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: 'desc' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/lowercase alphanumerics/)
    expect(createCaptures).toHaveLength(0)
  })

  it('rejects a whitespace-only description with an in-page validation error', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

    fireEvent.change(screen.getByTestId('label-catalog-add-key'), {
      target: { value: 'module' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-add-description'), {
      target: { value: '   ' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-add-submit'))

    expect(screen.getByTestId('label-catalog-add-error')).toHaveTextContent(/non-empty/)
    expect(createCaptures).toHaveLength(0)
  })

  it('rejects supportedValues containing only empty entries', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

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
    expect(createCaptures).toHaveLength(0)
  })

  it('rejects supportedValues with mixed empty comma entries before adding', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

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
    expect(createCaptures).toHaveLength(0)
  })

  it('opens the edit form with the key field read-only and pre-filled values', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
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

  it('PATCHes the description and supportedValues on save', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))

    fireEvent.change(screen.getByTestId('label-catalog-edit-description-module'), {
      target: { value: 'updated description' },
    })
    fireEvent.change(screen.getByTestId('label-catalog-edit-values-module'), {
      target: { value: 'core, infra' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    await waitFor(() => expect(updateCaptures).toHaveLength(1))
    expect(updateCaptures[0]).toEqual({
      key: 'module',
      patch: { description: 'updated description', supportedValues: ['core', 'infra'] },
    })
  })

  it('rejects a whitespace-only description in edit form before sending PATCH', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))

    fireEvent.change(screen.getByTestId('label-catalog-edit-description-module'), {
      target: { value: '   ' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    expect(screen.getByTestId('label-catalog-edit-error-module')).toHaveTextContent(/non-empty/)
    expect(updateCaptures).toHaveLength(0)
  })

  it('rejects supportedValues with blank newline entries before editing', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))

    fireEvent.change(screen.getByTestId('label-catalog-edit-values-module'), {
      target: { value: 'auth\n\nui' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    expect(screen.getByTestId('label-catalog-edit-error-module')).toHaveTextContent(/empty entries/i)
    expect(updateCaptures).toHaveLength(0)
  })

  it('sends PATCH with supportedValues:[] to clear values when the textarea is emptied', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))

    fireEvent.change(screen.getByTestId('label-catalog-edit-values-module'), {
      target: { value: '' },
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    await waitFor(() => expect(updateCaptures).toHaveLength(1))
    expect(updateCaptures[0].patch).toEqual({ description: 'Classifies the subsystem', supportedValues: [] })
  })

  it('deletes an entry via DELETE only after the shared AlertDialog is confirmed', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-delete-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-delete-button-module'))

    const dialog = screen.getByTestId('label-catalog-delete-alert')
    expect(dialog).toBeInTheDocument()
    expect(dialog).toHaveAttribute('data-tone', 'destructive')

    expect(deleteCaptures).toHaveLength(0)

    fireEvent.click(screen.getByTestId('label-catalog-delete-alert-confirm'))

    await waitFor(() => expect(deleteCaptures).toHaveLength(1))
    expect(deleteCaptures[0]).toBe('module')
  })

  it('does not invoke the delete mutation when the AlertDialog is cancelled', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-delete-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-delete-button-module'))

    const dialog = screen.getByTestId('label-catalog-delete-alert')
    expect(dialog).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('label-catalog-delete-alert-cancel'))

    expect(deleteCaptures).toHaveLength(0)
  })

  it('renders a single shared AlertDialog instance for the whole section, not per row', async () => {
    _catalogData = [moduleDef, refactorDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-delete-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-delete-button-module'))

    const dialog = screen.getByTestId('label-catalog-delete-alert')
    expect(dialog).toBeInTheDocument()

    const allDialogs = document.querySelectorAll('[data-testid="label-catalog-delete-alert"]')
    expect(allDialogs).toHaveLength(1)
  })

  it('surfaces server errors from create mutation as an in-page alert', async () => {
    _createError = "Key 'module' already exists in the project catalog."

    renderSection()

    await screen.findByTestId('label-catalog-add-key')

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
    _catalogData = [moduleDef]
    _updateError = "Key 'module' not found in the project catalog."

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('label-catalog-edit-button-module'))
    fireEvent.click(screen.getByTestId('label-catalog-edit-save-module'))

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-error-module')).toHaveTextContent('not found')
    })
  })

  it('shows the loading skeleton while catalog is loading', () => {
    renderSection()

    expect(screen.getByRole('status')).toBeInTheDocument()
  })

  it('wires aria-invalid + aria-describedby only on the invalid add-form field', async () => {
    renderSection()

    await screen.findByTestId('label-catalog-add-key')

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

  it('wires aria-invalid + aria-describedby only on the invalid edit-form field', async () => {
    _catalogData = [moduleDef]

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
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

  it('shows edit server errors as form-level alerts without marking every edit field invalid', async () => {
    _catalogData = [moduleDef]
    _updateError = "Key 'module' not found in the project catalog."

    renderSection()

    await waitFor(() => {
      expect(screen.getByTestId('label-catalog-edit-button-module')).toBeInTheDocument()
    })
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

  it('renders an inline New definition CTA when the catalog is empty', async () => {
    renderSection()

    const newButton = await screen.findByTestId('label-catalog-empty-new-button')
    expect(newButton).toBeInTheDocument()
    expect(newButton).toHaveTextContent(/New definition/)
  })

  describe('Search input', () => {
    it('renders a search input with an accessible label when a project is selected', async () => {
      _catalogData = [moduleDef, refactorDef]

      renderSection()

      const search = await screen.findByTestId('label-catalog-search')
      expect(search).toBeInTheDocument()
      expect(search.tagName).toBe('INPUT')
      expect(search.getAttribute('aria-label')).toMatch(/search/i)
      expect(search.getAttribute('placeholder')).not.toBeNull()
    })

    it('filters the displayed list by key when a matching query is entered', async () => {
      _catalogData = [moduleDef, refactorDef]

      renderSection()

      await screen.findByTestId('label-catalog-search')

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'module' },
      })

      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
    })

    it('filters the displayed list by description when a matching query is entered', async () => {
      _catalogData = [moduleDef, refactorDef]

      renderSection()

      await screen.findByTestId('label-catalog-search')

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'subsystem' },
      })

      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
    })

    it('filters the displayed list by a supported value when a matching query is entered', async () => {
      _catalogData = [moduleDef, refactorDef]

      renderSection()

      await screen.findByTestId('label-catalog-search')

      fireEvent.change(screen.getByTestId('label-catalog-search'), {
        target: { value: 'auth' },
      })

      expect(screen.getByTestId('label-catalog-row-module')).toBeInTheDocument()
      expect(screen.queryByTestId('label-catalog-row-refactor')).not.toBeInTheDocument()
    })

    it('restores the full list when the search query is cleared', async () => {
      _catalogData = [moduleDef, refactorDef]

      renderSection()

      await screen.findByTestId('label-catalog-search')

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

    it('shows a "no matches" message without the inline New definition CTA when the search filter is the only reason the list is empty', async () => {
      _catalogData = [moduleDef, refactorDef]

      renderSection()

      await screen.findByTestId('label-catalog-search')

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
