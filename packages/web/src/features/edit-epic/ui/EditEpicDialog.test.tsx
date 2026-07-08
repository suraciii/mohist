// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EditEpicDialog } from './EditEpicDialog'
import type { EpicDetail, EpicStatus } from '../../../entities/epic'
import { EPIC_DESCRIPTION_TEMPLATE } from '@/shared/lib/epic-description-template'

const mocks = vi.hoisted(() => ({
  useUpdateEpic: vi.fn(),
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useUpdateEpic: mocks.useUpdateEpic,
  }
})

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
    id: 'epic-edit-1',
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
  }
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

function renderDialog(props: {
  open?: boolean
  onClose?: () => void
  epic?: EpicDetail
} = {}) {
  const open = props.open ?? true
  const onClose = props.onClose ?? vi.fn()
  const epic = props.epic ?? makeEpic()
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
        <MemoryRouter>
          <EditEpicDialog open={open} onClose={onClose} epic={epic} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, onClose, epic, ...view }
}

/**
 * Returns a mock `mutate` that triggers `useMutation`-style `onSuccess`
 * synchronously. The dialog's handleSubmit calls `mutate(variables, { onSuccess })`,
 * so we replay that contract in the same way EpicCreateDialog does.
 */
function makeAutoSuccessMutate() {
  return (
    variables: unknown,
    options?: { onSuccess?: (data: { id: string; number: number; title: string }) => void },
  ) => {
    const epic = variables as { id: string }
    options?.onSuccess?.({ id: epic.id, number: 17, title: 'Existing milestone' })
  }
}

function mockUseUpdateEpic({
  mutate,
  isPending = false,
  isError = false,
  error,
}: {
  mutate: ((...args: unknown[]) => void) | ((...args: never[]) => void)
  isPending?: boolean
  isError?: boolean
  error?: Error | null
}) {
  mocks.useUpdateEpic.mockReturnValue({
    mutate: mutate as unknown as (...args: unknown[]) => void,
    isPending,
    isError,
    error: error ?? null,
  })
}

function stubWidth(width: number) {
  Object.defineProperty(document.documentElement, 'scrollWidth', {
    configurable: true,
    get: () => width,
  })
  Object.defineProperty(document.documentElement, 'clientWidth', {
    configurable: true,
    get: () => width,
  })
}

describe('EditEpicDialog verbatim load', () => {
  it('populates the description textarea with the existing markdown verbatim', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    const existing = 'Pre-existing markdown body.\n\nWith paragraphs.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe(existing)
  })

  it('does not auto-rewrite existing content into the Goal / Background / Non-goals / Scope template', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
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
    mockUseUpdateEpic({ mutate: vi.fn() })
    const existing = '## Goal\nCustom user-authored goal.\n\n## Background\nCustom background.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe(existing)
    // No new headers from the scaffold should be silently added.
    expect(description.value).not.toContain('## Scope')
    expect(description.value).not.toContain('## Non-goals')
  })

  it('loads an empty description verbatim without injecting any template content', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    renderDialog({ epic: makeEpic({ description: '' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe('')
    expect(description.value).not.toContain('## Goal')
  })

  it('reloads state when a different epic is passed in (e.g. reopened for another epic)', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    const first = makeEpic({ id: 'epic-A', description: 'A body' })
    const second = makeEpic({ id: 'epic-B', description: 'B body' })

    const { rerender } = render(
      <QueryClientProvider
        client={new QueryClient({
          defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
        })}
      >
        <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
          <MemoryRouter>
            <EditEpicDialog open={true} onClose={() => {}} epic={first} />
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
            <EditEpicDialog open={true} onClose={() => {}} epic={second} />
          </MemoryRouter>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect((screen.getByLabelText('Description') as HTMLTextAreaElement).value).toBe('B body')
  })
})

describe('EditEpicDialog save preserves content', () => {
  it('submits the pre-existing markdown exactly when saved without edits', async () => {
    const mutate = vi.fn()
    mockUseUpdateEpic({ mutate })
    const existing = 'Pre-existing markdown body.\n\nWith paragraphs.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [
      { id: string; data: { title: string; description: string; priority: string } },
    ]
    expect(payload.id).toBe('epic-edit-1')
    expect(payload.data.description).toBe(existing)
    // No template content is silently injected on save.
    expect(payload.data.description).not.toContain('## Goal')
    expect(payload.data.description).not.toContain('## Background')
    expect(payload.data.description).not.toContain('## Non-goals')
    expect(payload.data.description).not.toContain('## Scope')
  })

  it('submits only the resulting markdown description (no structured fields in payload)', async () => {
    const mutate = vi.fn()
    mockUseUpdateEpic({ mutate })
    const existing = 'Plain authored body.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [{ data: Record<string, unknown> }]
    // Only the resulting markdown string is sent — no separate goal/background/non-goals/scope fields.
    expect(payload.data).not.toHaveProperty('goal')
    expect(payload.data).not.toHaveProperty('background')
    expect(payload.data).not.toHaveProperty('nonGoals')
    expect(payload.data).not.toHaveProperty('scope')
  })

  it('submits the user-authored description verbatim after edits', async () => {
    const mutate = vi.fn()
    mockUseUpdateEpic({ mutate })
    renderDialog({ epic: makeEpic({ description: 'Original body' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: 'Edited body without any scaffolding.' } })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [{ data: { description: string } }]
    expect(payload.data.description).toBe('Edited body without any scaffolding.')
  })

  it('closes the dialog after a successful save', async () => {
    const onClose = vi.fn()
    const mutate = vi.fn(makeAutoSuccessMutate())
    mockUseUpdateEpic({ mutate })
    renderDialog({ onClose })

    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
  })
})

describe('EditEpicDialog opt-in Insert template', () => {
  it('offers the Insert-template action on an empty description (visible when empty)', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    renderDialog({ epic: makeEpic({ description: '' }) })

    const insertButton = screen.getByRole('button', { name: 'Insert template' })
    expect(insertButton).toBeInTheDocument()
    expect(insertButton).not.toBeDisabled()
  })

  it('does not auto-apply the template on an empty description — the field stays empty until the user clicks Insert', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    renderDialog({ epic: makeEpic({ description: '' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe('')
  })

  it('keeps the Insert-template action available on demand when the description already has content', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    renderDialog({ epic: makeEpic({ description: 'Existing notes' }) })

    expect(screen.getByRole('button', { name: 'Insert template' })).toBeInTheDocument()
  })

  it('sets the description to the template when Insert is clicked on an empty value', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    renderDialog({ epic: makeEpic({ description: '' }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    expect(description.value).toBe(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('preserves existing user text when Insert is clicked on a non-empty value', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    const existing = 'Existing notes'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    expect(description.value.startsWith(existing)).toBe(true)
    expect(description.value).toContain(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('does not destroy the pre-existing markdown when Insert is clicked', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })
    const existing = '## Goal\nCustom user-authored goal.\n\n## Background\nCustom background.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    // The original user-authored content must remain intact.
    expect(description.value).toContain('## Goal\nCustom user-authored goal.')
    expect(description.value).toContain('## Background\nCustom background.')
    expect(description.value).toContain(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('does not save the template content on its own — the user must explicitly invoke Insert', async () => {
    const mutate = vi.fn()
    mockUseUpdateEpic({ mutate })
    const existing = 'Pre-existing markdown body.'
    renderDialog({ epic: makeEpic({ description: existing }) })

    // Just open and save without clicking Insert.
    fireEvent.click(screen.getByTestId('edit-epic-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [{ data: { description: string } }]
    expect(payload.data.description).toBe(existing)
    expect(payload.data.description).not.toContain('## Goal')
  })
})

describe('EditEpicDialog mobile-safe layout', () => {
  it('renders without horizontal overflow at 320, 390, and 430 px', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })

    for (const width of [320, 390, 430]) {
      cleanup()
      stubWidth(width)
      renderDialog()

      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(document.documentElement.clientWidth)
    }
  })

  it('keeps the submit action reachable through a sticky footer (footer is outside the scroll region)', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })

    renderDialog()

    const footer = screen.getByTestId('edit-epic-footer')
    const submitButton = screen.getByTestId('edit-epic-submit')
    expect(footer).toBeInTheDocument()
    expect(submitButton).toBeInTheDocument()
    expect(footer.contains(submitButton)).toBe(true)

    const scrollRegion = screen.getByTestId('edit-epic-scroll-region')
    expect(scrollRegion).toBeInTheDocument()
    // The footer must NOT live inside the scroll region.
    expect(scrollRegion.contains(footer)).toBe(false)
  })

  it('keeps the cancel action in the same sticky footer', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })

    renderDialog()

    const footer = screen.getByTestId('edit-epic-footer')
    expect(footer.contains(screen.getByTestId('edit-epic-cancel'))).toBe(true)
  })

  it('keeps the title and description fields inside the scroll region', () => {
    mockUseUpdateEpic({ mutate: vi.fn() })

    renderDialog()

    const scrollRegion = screen.getByTestId('edit-epic-scroll-region')
    expect(scrollRegion.contains(screen.getByLabelText('Title'))).toBe(true)
    expect(scrollRegion.contains(screen.getByLabelText('Description'))).toBe(true)
  })
})