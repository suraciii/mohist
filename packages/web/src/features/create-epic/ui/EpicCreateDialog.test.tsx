// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import { EpicCreateDialog } from './EpicCreateDialog'
import { EPIC_DESCRIPTION_TEMPLATE, hasEpicDescriptionStructure } from '@/shared/lib/epic-description-template'

const mocks = vi.hoisted(() => ({
  useCreateEpic: vi.fn(),
}))

const mockNavigate = vi.fn()
const toastMocks = vi.hoisted(() => ({
  success: vi.fn(),
  error: vi.fn(),
  warning: vi.fn(),
  info: vi.fn(),
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useCreateEpic: mocks.useCreateEpic,
  }
})

vi.mock('sonner', () => ({
  toast: toastMocks,
}))

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockNavigate,
  }
})

const project = {
  id: 'proj-create',
  name: 'Project',
  path: '/tmp/project',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
  mockNavigate.mockReset()
})

function renderDialog(props: { open?: boolean; onClose?: () => void } = {}) {
  const open = props.open ?? true
  const onClose = props.onClose ?? vi.fn()
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
        <MemoryRouter initialEntries={['/epics']}>
          <EpicCreateDialog open={open} onClose={onClose} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, onClose, ...view }
}

interface CreatedEpic {
  id: string
  number: number
  title: string
}

/**
 * Returns a mock `mutate` that triggers `useMutation`-style `onSuccess`
 * synchronously with a stubbed Epic. Calling code (EpicCreateDialog.handleSubmit)
 * passes `{ title, description, priority }` plus an options bag; the mock invokes
 * `onSuccess(stubEpic)` so the dialog transitions to its success state inside
 * React Testing Library's event-act boundary.
 */
function makeAutoSuccessMutate(opts: { stubCreated?: CreatedEpic; failOnSuccessWith?: Error } = {}) {
  return (variables: unknown, options?: { onSuccess?: (data: CreatedEpic) => void; onError?: (err: Error) => void }) => {
    if (opts.failOnSuccessWith) {
      options?.onError?.(opts.failOnSuccessWith)
      return
    }
    const stub: CreatedEpic = opts.stubCreated ?? { id: 'epic-new-id', number: 42, title: 'Created Epic' }
    options?.onSuccess?.(stub)
    // Reference variables to silence "unused" lints; the stub echoes the user's title for assertions.
    void variables
  }
}

function mockUseCreateEpic({
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
  mocks.useCreateEpic.mockReturnValue({
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

describe('EpicCreateDialog guided template prefill', () => {
  it('prefills the description with the Goal / Background / Non-goals / Scope template when opened empty', () => {
    mockUseCreateEpic({ mutate: vi.fn() })

    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(description.value).toBe(EPIC_DESCRIPTION_TEMPLATE)
    expect(hasEpicDescriptionStructure(description.value)).toBe(true)
    expect(description.value).toContain('## Goal')
    expect(description.value).toContain('## Background')
    expect(description.value).toContain('## Non-goals')
    expect(description.value).toContain('## Scope')
  })

  it('does not render the Insert-template action when the description already carries the full structure', () => {
    mockUseCreateEpic({ mutate: vi.fn() })

    renderDialog()

    expect(screen.queryByRole('button', { name: 'Insert template' })).toBeNull()
  })

  it('exposes the Insert-template action when the user clears the description', () => {
    mockUseCreateEpic({ mutate: vi.fn() })

    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: '' } })

    const insertButton = screen.getByRole('button', { name: 'Insert template' })
    expect(insertButton).toBeInTheDocument()
    expect(insertButton).not.toBeDisabled()
  })

  it('does not destroy existing simple text when Insert template is clicked', () => {
    mockUseCreateEpic({ mutate: vi.fn() })

    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: 'Just a plain milestone description.' } })

    fireEvent.click(screen.getByRole('button', { name: 'Insert template' }))

    const next = description.value
    expect(next.startsWith('Just a plain milestone description.')).toBe(true)
    expect(next).toContain('## Goal')
    expect(next).toContain('## Scope')
  })
})

describe('EpicCreateDialog submit flow', () => {
  it('drops the required constraint on description and submits a quick create with no description', async () => {
    const mutate = vi.fn()
    mockUseCreateEpic({ mutate })

    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Quick milestone' } })

    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [{ title: string; description: string; priority: string }]
    expect(payload.title).toBe('Quick milestone')
    expect(payload.description).toBe('')
    // Only the markdown string is sent — no separate goal/background/non-goals/scope fields.
    expect(payload).not.toHaveProperty('goal')
    expect(payload).not.toHaveProperty('background')
    expect(payload).not.toHaveProperty('nonGoals')
    expect(payload).not.toHaveProperty('scope')
  })

  it('submits empty sections as-is so the stored description equals exactly what the user authored', async () => {
    const mutate = vi.fn()
    mockUseCreateEpic({ mutate })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Drafted milestone' } })

    // Leave description untouched so the pre-filled placeholder template is sent verbatim.
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [{ description: string }]
    expect(payload.description).toBe(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('submits a plain non-templated description verbatim and does not inject template content', async () => {
    const mutate = vi.fn()
    mockUseCreateEpic({ mutate })

    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: 'Ship the entry point and unblock the planning flow.' } })
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Plain milestone' } })

    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(mutate).toHaveBeenCalledTimes(1))
    const [payload] = mutate.mock.calls[0] as [{ description: string; title: string }]
    expect(payload.description).toBe('Ship the entry point and unblock the planning flow.')
    expect(payload.description).not.toContain('## Goal')
    expect(payload.title).toBe('Plain milestone')
  })
})

describe('EpicCreateDialog success state', () => {
  it('transitions to a success state on mutate success and does not auto-close the dialog', async () => {
    const onClose = vi.fn()
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-new-id', number: 42, title: 'Created Epic' } }))
    mockUseCreateEpic({ mutate })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Success flow' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(screen.queryByTestId('epic-create-success')).toBeInTheDocument())
    expect(onClose).not.toHaveBeenCalled()
  })

  it('renders the idle-aware success message and never uses started/running wording', async () => {
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-idle', number: 7, title: 'Idle wording' } }))
    mockUseCreateEpic({ mutate })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Idle wording' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    const dialog = await screen.findByTestId('epic-create-dialog')
    const text = dialog.textContent ?? ''

    expect(text.toLowerCase()).toContain('idle')
    expect(text.toLowerCase()).toContain('ready to plan')
    expect(text.toLowerCase()).not.toContain('started')
    expect(text.toLowerCase()).not.toContain('running')
  })

  it('navigates to /epics/<id> via useProjectPath when Open Epic is chosen', async () => {
    const onClose = vi.fn()
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-open-target', number: 9, title: 'Open flow' } }))
    mockUseCreateEpic({ mutate })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Open flow' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    const openButton = await screen.findByTestId('epic-create-open')
    expect(openButton).toBeInTheDocument()
    expect(openButton).not.toBeDisabled()

    fireEvent.click(openButton)

    await waitFor(() => expect(mockNavigate).toHaveBeenCalledWith('/Project/epics/epic-open-target'))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('shows both Stay and Open Epic choices and never hides or disables either', async () => {
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-both', number: 3, title: 'Both choices' } }))
    mockUseCreateEpic({ mutate })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Both choices' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    const stay = await screen.findByTestId('epic-create-stay')
    const open = await screen.findByTestId('epic-create-open')
    expect(stay).toBeInTheDocument()
    expect(open).toBeInTheDocument()
    expect(stay).not.toBeDisabled()
    expect(open).not.toBeDisabled()
  })

  it('treating Stay as the dialog close keeps the user on the current page and does not navigate', async () => {
    const onClose = vi.fn()
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-stay', number: 11, title: 'Stay flow' } }))
    mockUseCreateEpic({ mutate })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Stay flow' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    fireEvent.click(await screen.findByTestId('epic-create-stay'))

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
    expect(mockNavigate).not.toHaveBeenCalled()
  })

  it('treats Cancel as Stay (closes without navigation) and the success state never fires a toast', async () => {
    const onClose = vi.fn()
    const mutate = vi.fn()
    mockUseCreateEpic({ mutate })

    renderDialog({ onClose })

    // Cancel path uses the same handler as overlay / X close (treated as Stay).
    fireEvent.click(screen.getByTestId('epic-create-cancel'))

    expect(onClose).toHaveBeenCalledTimes(1)
    expect(mockNavigate).not.toHaveBeenCalled()
  })

  it('does not fire any create-success toast (useCreateEpic slimmed) and the dialog owns all success UX', async () => {
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-no-toast', number: 1, title: 'No toast' } }))
    mockUseCreateEpic({ mutate })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'No toast' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(screen.queryByTestId('epic-create-success')).toBeInTheDocument())
    expect(toastMocks.success).not.toHaveBeenCalled()
  })

  it('still closes cleanly when an X-style cancel happens from the success state (treat as Stay)', async () => {
    const onClose = vi.fn()
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-cancel-success', number: 2, title: 'Cancel after success' } }))
    mockUseCreateEpic({ mutate })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Cancel after success' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    // After success, only Open Epic / Stay buttons render; verify they include Stay which always closes.
    const stay = await screen.findByTestId('epic-create-stay')
    fireEvent.click(stay)

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
    expect(mockNavigate).not.toHaveBeenCalled()
  })
})

describe('EpicCreateDialog error handling', () => {
  it('does not transition to the success state when the mutation handler reports a failure', async () => {
    const failure = new Error('Server unavailable')
    const mutate = makeAutoSuccessMutate({ failOnSuccessWith: failure })
    mockUseCreateEpic({ mutate })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Will fail' } })

    await act(async () => {
      fireEvent.click(screen.getByTestId('epic-create-submit'))
    })

    // No success transition should occur; the form remains reachable.
    await waitFor(() => expect(screen.queryByTestId('epic-create-success')).toBeNull())
    expect(screen.queryByTestId('epic-create-cancel')).toBeInTheDocument()
    expect(screen.queryByTestId('epic-create-submit')).toBeInTheDocument()
  })
})

describe('EpicCreateDialog mobile-safe layout', () => {
  it('renders without horizontal overflow at 320, 390, and 430 px', () => {
    mockUseCreateEpic({ mutate: vi.fn() })

    for (const width of [320, 390, 430]) {
      cleanup()
      stubWidth(width)
      renderDialog()

      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(document.documentElement.clientWidth)
    }
  })

  it('keeps the submit action reachable through a sticky footer (footer is outside the scroll region)', () => {
    mockUseCreateEpic({ mutate: vi.fn() })

    renderDialog()

    const footer = screen.getByTestId('epic-create-footer')
    const submitButton = screen.getByTestId('epic-create-submit')
    expect(footer).toBeInTheDocument()
    expect(submitButton).toBeInTheDocument()
    expect(footer.contains(submitButton)).toBe(true)

    const scrollRegion = screen.getByTestId('epic-create-scroll-region')
    expect(scrollRegion).toBeInTheDocument()
    // The footer must NOT live inside the scroll region.
    expect(scrollRegion.contains(footer)).toBe(false)
  })

  it('keeps both success actions reachable through the same footer after a successful create', async () => {
    const mutate = vi.fn(makeAutoSuccessMutate({ stubCreated: { id: 'epic-reach', number: 4, title: 'Reachability' } }))
    mockUseCreateEpic({ mutate })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Reachability' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await screen.findByTestId('epic-create-stay')
    await screen.findByTestId('epic-create-open')

    const footer = screen.getByTestId('epic-create-footer')
    expect(footer.contains(screen.getByTestId('epic-create-stay'))).toBe(true)
    expect(footer.contains(screen.getByTestId('epic-create-open'))).toBe(true)

    const scrollRegion = screen.getByTestId('epic-create-scroll-region')
    expect(scrollRegion.contains(footer)).toBe(false)
  })
})
