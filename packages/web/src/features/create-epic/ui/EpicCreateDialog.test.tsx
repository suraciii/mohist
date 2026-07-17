import '@testing-library/jest-dom'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { toast } from 'sonner'
import { ProjectProvider } from '../../../entities/project'
import { EpicCreateDialog } from './EpicCreateDialog'
import { EPIC_DESCRIPTION_TEMPLATE, hasEpicDescriptionStructure } from '@/shared/lib/epic-description-template'
import { useMswServer } from '../../../../tests/support/msw'
import type { Epic, EpicStatus } from '../../../entities/epic'

let _createResponse: Epic | null = null

function makeEpic(overrides: Partial<Epic> = {}): Epic {
  return {
    number: 42,
    title: 'Created Epic',
    description: '',
    priority: 'p2',
    status: 'idle' as EpicStatus,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
    projectId: overrides.projectId ?? 'proj-create',
  }
}

const createHandler = vi.fn(async (info: { request: Request }) => {
  const body = await info.request.clone().json()
  void body
  if (_createResponse) {
    return HttpResponse.json({ success: true, data: _createResponse })
  }
  return HttpResponse.json({ success: true, data: makeEpic() })
})

useMswServer(
  http.post('*/api/projects/:projectId/epics', createHandler),
)

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

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
  _createResponse = null
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
          <LocationProbe />
          <EpicCreateDialog open={open} onClose={onClose} />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, onClose, ...view }
}

describe('EpicCreateDialog guided template prefill', () => {
  it('prefills the description with the Goal / Background / Non-goals / Scope template when opened empty', () => {
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
    renderDialog()

    expect(screen.queryByRole('button', { name: 'Insert template' })).toBeNull()
  })

  it('exposes the Insert-template action when the user clears the description', () => {
    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: '' } })

    const insertButton = screen.getByRole('button', { name: 'Insert template' })
    expect(insertButton).toBeInTheDocument()
    expect(insertButton).not.toBeDisabled()
  })

  it('does not destroy existing simple text when Insert template is clicked', () => {
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
    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Quick milestone' } })

    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(createHandler).toHaveBeenCalledTimes(1))
    const call = createHandler.mock.calls[0]![0]
    const url = new URL(call.request.url)
    expect(url.pathname).toContain('/epics')
    const body = await call.request.clone().json()
    expect(body.title).toBe('Quick milestone')
    expect(body.description).toBe('')
    expect(body).not.toHaveProperty('goal')
    expect(body).not.toHaveProperty('background')
    expect(body).not.toHaveProperty('nonGoals')
    expect(body).not.toHaveProperty('scope')
  })

  it('submits empty sections as-is so the stored description equals exactly what the user authored', async () => {
    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Drafted milestone' } })

    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(createHandler).toHaveBeenCalledTimes(1))
    const call = createHandler.mock.calls[0]![0]
    const body = await call.request.clone().json()
    expect(body.description).toBe(EPIC_DESCRIPTION_TEMPLATE)
  })

  it('submits a plain non-templated description verbatim and does not inject template content', async () => {
    renderDialog()

    const description = screen.getByLabelText('Description') as HTMLTextAreaElement
    fireEvent.change(description, { target: { value: 'Ship the entry point and unblock the planning flow.' } })
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Plain milestone' } })

    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(createHandler).toHaveBeenCalledTimes(1))
    const call = createHandler.mock.calls[0]![0]
    const body = await call.request.clone().json()
    expect(body.description).toBe('Ship the entry point and unblock the planning flow.')
    expect(body.description).not.toContain('## Goal')
    expect(body.title).toBe('Plain milestone')
  })
})

describe('EpicCreateDialog success state', () => {
  it('transitions to a success state on mutate success and does not auto-close the dialog', async () => {
    const onClose = vi.fn()
    _createResponse = makeEpic({ number: 42, title: 'Created Epic' })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Success flow' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(screen.queryByTestId('epic-create-success')).toBeInTheDocument())
    expect(onClose).not.toHaveBeenCalled()
  })

  it('renders the idle-aware success message and never uses started/running wording', async () => {
    _createResponse = makeEpic({ number: 7, title: 'Idle wording' })

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

  it('navigates to /epics/<number> via useProjectPath when Open Epic is chosen', async () => {
    const onClose = vi.fn()
    _createResponse = makeEpic({ number: 9, title: 'Open flow' })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Open flow' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    const openButton = await screen.findByTestId('epic-create-open')
    expect(openButton).toBeInTheDocument()
    expect(openButton).not.toBeDisabled()

    fireEvent.click(openButton)

    await waitFor(() => expect(screen.getByTestId('current-path').textContent).toBe('/Project/epics/9'))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('shows both Stay and Open Epic choices and never hides or disables either', async () => {
    _createResponse = makeEpic({ number: 3, title: 'Both choices' })

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
    _createResponse = makeEpic({ number: 11, title: 'Stay flow' })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Stay flow' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    fireEvent.click(await screen.findByTestId('epic-create-stay'))

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
    expect(screen.getByTestId('current-path').textContent).toBe('/epics')
  })

  it('treats Cancel as Stay (closes without navigation) and the success state never fires a toast', async () => {
    const onClose = vi.fn()

    renderDialog({ onClose })

    fireEvent.click(screen.getByTestId('epic-create-cancel'))

    expect(onClose).toHaveBeenCalledTimes(1)
    expect(screen.getByTestId('current-path').textContent).toBe('/epics')
  })

  it('does not fire any create-success toast (useCreateEpic slimmed) and the dialog owns all success UX', async () => {
    _createResponse = makeEpic({ number: 1, title: 'No toast' })

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'No toast' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    await waitFor(() => expect(screen.queryByTestId('epic-create-success')).toBeInTheDocument())
    expect(toast.success).not.toHaveBeenCalled()
  })

  it('still closes cleanly when an X-style cancel happens from the success state (treat as Stay)', async () => {
    const onClose = vi.fn()
    _createResponse = makeEpic({ number: 2, title: 'Cancel after success' })

    renderDialog({ onClose })

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Cancel after success' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    const stay = await screen.findByTestId('epic-create-stay')
    fireEvent.click(stay)

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1))
    expect(screen.getByTestId('current-path').textContent).toBe('/epics')
  })
})

describe('EpicCreateDialog error handling', () => {
  it('does not transition to the success state when the mutation handler reports a failure', async () => {
    createHandler.mockImplementationOnce(async () =>
      HttpResponse.json({ success: false, error: 'Server unavailable' }, { status: 500 }),
    )

    renderDialog()

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Will fail' } })
    fireEvent.click(screen.getByTestId('epic-create-submit'))

    expect(await screen.findByTestId('epic-create-error')).toHaveTextContent('Server unavailable')
    expect(screen.queryByTestId('epic-create-success')).not.toBeInTheDocument()
    expect(screen.queryByTestId('epic-create-cancel')).toBeInTheDocument()
    expect(screen.queryByTestId('epic-create-submit')).toBeInTheDocument()
  })
})

describe('EpicCreateDialog footer structure', () => {
  it('places the submit action in a footer outside the scroll region', () => {
    renderDialog()

    const footer = screen.getByTestId('epic-create-footer')
    const submitButton = screen.getByTestId('epic-create-submit')
    expect(footer).toBeInTheDocument()
    expect(submitButton).toBeInTheDocument()
    expect(footer.contains(submitButton)).toBe(true)

    const scrollRegion = screen.getByTestId('epic-create-scroll-region')
    expect(scrollRegion).toBeInTheDocument()
    expect(scrollRegion.contains(footer)).toBe(false)
  })

  it('places both success actions in the same footer after a successful create', async () => {
    _createResponse = makeEpic({ number: 4, title: 'Reachability' })

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
