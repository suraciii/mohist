import '@testing-library/jest-dom'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EditEpicDialog } from './EditEpicDialog'
import type { EpicDetail, EpicPriority, EpicStatus } from '../../../entities/epic'
import { EPIC_DESCRIPTION_TEMPLATE } from '@/shared/lib/epic-description-template'

const updateHandler = vi.fn(async ({ number, data }: {
  number: number
  data: { title: string; description: string; priority: EpicPriority }
}) => makeEpic({ number, ...data }))

const updateHook = () => useMutation({
  mutationFn: (variables: Parameters<typeof updateHandler>[0]) => updateHandler(variables),
}) as never

const project = {
  id: 'proj-edit',
  name: 'Project',
  path: '/tmp/project',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

function makeEpic(overrides: Partial<EpicDetail> = {}): EpicDetail {
  return {
    number: 17,
    title: 'Existing milestone',
    description: 'Pre-existing markdown body.\n\nWith paragraphs.',
    priority: 'p2',
    status: 'idle' as EpicStatus,
    pauseReason: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    linkedIssues: [],
    progress: {
      deliveredCount: 0,
      totalIssueCount: 0,
      blockedIssues: [],
      activeIssues: [],
      nextIssue: null,
      nextIssueReason: null,
      readyToMarkDone: false,
    },
    ...overrides,
    projectId: overrides.projectId ?? 'proj-edit',
  }
}

function renderDialog(props: { open?: boolean; onClose?: () => void; epic?: EpicDetail } = {}) {
  const open = props.open ?? true
  const onClose = props.onClose ?? vi.fn()
  const epic = props.epic ?? makeEpic()
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
        <MemoryRouter>
          <EditEpicDialog open={open} onClose={onClose} epic={epic} updateHook={updateHook} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('EditEpicDialog verbatim load', () => {
  it('populates the description textarea with the existing markdown verbatim', () => {
    const existing = 'Pre-existing markdown body.\n\nWith paragraphs.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe(existing)
  })

  it('does not auto-rewrite existing content into the Goal / Background / Non-goals / Scope template', () => {
    const existing = 'Pre-existing markdown body.\n\nWith paragraphs.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe(existing)
    expect(description.value).not.toContain('## Goal')
    expect(description.value).not.toContain('## Background')
    expect(description.value).not.toContain('## Non-goals')
    expect(description.value).not.toContain('## Scope')
  })

  it('preserves structured markdown that already happens to contain a Goal heading without rewriting it', () => {
    const existing = '## Goal\nCustom user-authored goal.\n\n## Background\nCustom background.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe(existing)
    expect(description.value).not.toContain('## Scope')
    expect(description.value).not.toContain('## Non-goals')
  })

  it('loads an empty description verbatim without injecting any template content', () => {
    renderDialog({ epic: makeEpic({ description: '' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe('')
    expect(description.value).not.toContain('## Goal')
  })

  it('reloads state when a different epic is passed in (e.g. reopened for another epic)', () => {
    const first = makeEpic({ description: 'A body' })
    const second = makeEpic({ description: 'B body' })

    const { rerender } = render(
      <QueryClientProvider
        client={new QueryClient({
          defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
        })}
      >
        <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
          <MemoryRouter>
            <EditEpicDialog open={true} onClose={() => {}} epic={first} updateHook={updateHook} />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect((screen.getByLabelText('Description') as HTMLTextAreaElement).value).toBe('A body')

    rerender(
      <QueryClientProvider
        client={new QueryClient({
          defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
        })}
      >
        <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
          <MemoryRouter>
            <EditEpicDialog open={true} onClose={() => {}} epic={second} updateHook={updateHook} />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect((screen.getByLabelText('Description') as HTMLTextAreaElement).value).toBe('B body')
  })
})

describe('EditEpicDialog save preserves content', () => {
  it('submits the pre-existing markdown exactly when saved without edits', async () => {
    const existing = 'Pre-existing markdown body.\n\nWith paragraphs.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(updateHandler).toHaveBeenCalledTimes(1))
    const { number, data: body } = updateHandler.mock.calls[0]![0]
    expect(number).toBe(17)
    expect(body.description).toBe(existing)
    expect(body.description).not.toContain('## Goal')
    expect(body.description).not.toContain('## Background')
    expect(body.description).not.toContain('## Non-goals')
    expect(body.description).not.toContain('## Scope')
  })

  it('submits only the resulting markdown description (no structured fields in payload)', async () => {
    const existing = 'Plain authored body.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(updateHandler).toHaveBeenCalledTimes(1))
    const body = updateHandler.mock.calls[0]![0].data
    expect(body).not.toHaveProperty('goal')
    expect(body).not.toHaveProperty('background')
    expect(body).not.toHaveProperty('nonGoals')
    expect(body).not.toHaveProperty('scope')
  })

  it('submits the user-authored description verbatim after edits', async () => {
    renderDialog({ epic: makeEpic({ description: 'Original body' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: 'Edited body without any scaffolding.' } })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(updateHandler).toHaveBeenCalledTimes(1))
    const body = updateHandler.mock.calls[0]![0].data
    expect(body.description).toBe('Edited body without any scaffolding.')
  })

  it('closes the dialog after a successful save', async () => {
    const onClose = vi.fn()
    renderDialog({ onClose })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
  })
})

describe('EditEpicDialog opt-in Insert template', () => {
  it('offers the Insert-template action on an empty description (visible when empty)', () => {
    renderDialog({ epic: makeEpic({ description: '' }) })

    const insertButton = screen.getByRole('button', { name: 'Insert template' })
    expect(insertButton).toBeInTheDocument()
    expect(insertButton).not.toBeDisabled()
  })

  it('does not auto-apply the template on an empty description — the field stays empty until the user clicks Insert', () => {
    renderDialog({ epic: makeEpic({ description: '' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe('')
  })

  it('keeps the Insert-template action available on demand when the description already has content', () => {
    renderDialog({ epic: makeEpic({ description: 'Existing notes' }) })

    expect(screen.getByRole('button', { name: 'Insert template' })).toBeInTheDocument()
  })

  it('sets the description to the template when Insert is clicked on an empty value', () => {
    renderDialog({ epic: makeEpic({ description: '' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    expect(description.value).toBe(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('preserves existing user text when Insert is clicked on a non-empty value', () => {
    const existing = 'Existing notes'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    expect(description.value.startsWith(existing)).toBe(true)
    expect(description.value).toContain(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('does not destroy the pre-existing markdown when Insert is clicked', () => {
    const existing = '## Goal\nCustom user-authored goal.\n\n## Background\nCustom background.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    expect(description.value).toContain('## Goal\nCustom user-authored goal.')
    expect(description.value).toContain('## Background\nCustom background.')
    expect(description.value).toContain(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('does not save the template content on its own — the user must explicitly invoke Insert', async () => {
    const existing = 'Pre-existing markdown body.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(updateHandler).toHaveBeenCalledTimes(1))
    const body = updateHandler.mock.calls[0]![0].data
    expect(body.description).toBe(existing)
    expect(body.description).not.toContain('## Goal')
  })
})

describe('EditEpicDialog footer structure', () => {
  it('places the submit action in a footer outside the scroll region', () => {
    renderDialog()

    const footer = screen.getByTestId('edit-epic-footer')
    const submitButton = screen.getByTestId('edit-epic-submit')
    expect(footer).toBeInTheDocument()
    expect(submitButton).toBeInTheDocument()
    expect(footer.contains(submitButton)).toBe(true)

    const scrollRegion = screen.getByTestId('edit-epic-scroll-region')
    expect(scrollRegion).toBeInTheDocument()
    expect(scrollRegion.contains(footer)).toBe(false)
  })

  it('places the cancel action in the same footer', () => {
    renderDialog()

    const footer = screen.getByTestId('edit-epic-footer')
    expect(footer.contains(screen.getByTestId('edit-epic-cancel'))).toBe(true)
  })

  it('keeps both actions in the same footer outside the scroll region', () => {
    renderDialog()

    const footer = screen.getByTestId('edit-epic-footer')
    const scrollRegion = screen.getByTestId('edit-epic-scroll-region')
    expect(footer.contains(screen.getByTestId('edit-epic-submit'))).toBe(true)
    expect(footer.contains(screen.getByTestId('edit-epic-cancel'))).toBe(true)
    expect(scrollRegion.contains(footer)).toBe(false)
  })
})

describe('EditEpicDialog error handling', () => {
  it('does not close the dialog when the mutation handler reports a failure', async () => {
    const onClose = vi.fn()
    updateHandler.mockRejectedValueOnce(new Error('Server unavailable'))
    renderDialog({ onClose })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(screen.getByTestId('edit-epic-error')).toHaveTextContent('Server unavailable'))
    expect(onClose).not.toHaveBeenCalled()
  })
})
