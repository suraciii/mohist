// @vitest-environment jsdom
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
} from 'vitest'
import { useState } from 'react'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { CreateProjectDialog } from '..'
import { useProject } from '../../../entities/project'

const createRequests: { method: string; url: string; body: unknown }[] = []

const handlers = [
  http.post('/api/projects', async ({ request }) => {
    const body = await request.json()
    createRequests.push({ method: request.method, url: request.url, body })
    const name = (body as { name: string }).name
    if (name === 'existing') {
      return HttpResponse.json(
        { success: false, error: 'Project name already exists', code: 'conflict' },
        { status: 409 },
      )
    }
    return HttpResponse.json(
      {
        success: true,
        data: {
          id: `proj-${name}`,
          name,
          createdAt: '2026-06-12T00:00:00.000Z',
          updatedAt: '2026-06-12T00:00:00.000Z',
          repositories: [],
        },
      },
      { status: 201 },
    )
  }),
]

const server = setupServer(...handlers)

function ActiveProjectProbe() {
  const { projectId } = useProject()
  return <div data-testid="active-project-id">{projectId ?? ''}</div>
}

function HostDialog() {
  const [open, setOpen] = useState(true)
  return (
    <>
      <ActiveProjectProbe />
      <CreateProjectDialog open={open} onClose={() => setOpen(false)} />
    </>
  )
}

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})

afterAll(() => {
  server.close()
})

beforeEach(() => {
  createRequests.length = 0
  server.resetHandlers(...handlers)
})

afterEach(() => {
  server.resetHandlers(...handlers)
  window.localStorage.clear()
})

function openDialog() {
  return render(<HostDialog />)
}

describe('CreateProjectDialog (name-only)', () => {
  it('submits a name-only POST /api/projects and does not include path or effectivePath', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    expect(dialog).toBeInTheDocument()

    fireEvent.change(within(dialog).getByTestId('create-project-name'), {
      target: { value: 'my-project' },
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => expect(createRequests).toHaveLength(1))
    expect(createRequests[0].method).toBe('POST')
    expect(createRequests[0].url).toContain('/api/projects')
    expect(createRequests[0].body).toEqual({ name: 'my-project' })
    const body = createRequests[0].body as Record<string, unknown>
    expect(body).not.toHaveProperty('path')
    expect(body).not.toHaveProperty('effectivePath')
  })

  it('does not render any local filesystem path input, browse button, or directory browser', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    expect(within(dialog).queryByLabelText(/path/i)).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: /browse/i })).not.toBeInTheDocument()
    expect(within(dialog).queryByPlaceholderText(/select a directory/i)).not.toBeInTheDocument()
    expect(within(dialog).queryByPlaceholderText(/enter a path/i)).not.toBeInTheDocument()
    expect(within(dialog).queryByPlaceholderText(/search.*path|search.*direct/i)).not.toBeInTheDocument()
    expect(within(dialog).queryByTestId('create-project-path')).not.toBeInTheDocument()
  })

  it('does not display "Path is required" when the name field is the only required input', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    const submit = within(dialog).getByTestId('create-project-submit') as HTMLButtonElement
    expect(submit).toBeDisabled()
    expect(within(dialog).queryByText(/path is required/i)).not.toBeInTheDocument()
  })

  it('enables submit when a name is entered and no path is required', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    const nameInput = within(dialog).getByTestId('create-project-name')
    fireEvent.change(nameInput, { target: { value: 'only-name' } })

    const submit = within(dialog).getByTestId('create-project-submit') as HTMLButtonElement
    expect(submit).not.toBeDisabled()
  })

  it('keeps the dialog open and shows "Project name already exists" on 409 conflict', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    fireEvent.change(within(dialog).getByTestId('create-project-name'), {
      target: { value: 'existing' },
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => {
      expect(within(dialog).getByTestId('create-project-conflict')).toHaveTextContent(
        'Project name already exists',
      )
    })
    expect(screen.getByTestId('create-project-dialog')).toBeInTheDocument()
    expect(createRequests).toHaveLength(1)
  })

  it('switches the active project to the newly created project on success', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    expect(screen.getByTestId('active-project-id').textContent).toBe('test-project')

    fireEvent.change(within(dialog).getByTestId('create-project-name'), {
      target: { value: 'switched-project' },
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('active-project-id').textContent).toBe('proj-switched-project')
    })
  })

  it('closes the dialog on successful creation so the project list refreshes', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    fireEvent.change(within(dialog).getByTestId('create-project-name'), {
      target: { value: 'close-on-success' },
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => {
      expect(screen.queryByTestId('create-project-dialog')).not.toBeInTheDocument()
    })
  })

  it('trims the name before submission', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    fireEvent.change(within(dialog).getByTestId('create-project-name'), {
      target: { value: '  trimmed-name  ' },
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => expect(createRequests).toHaveLength(1))
    expect(createRequests[0].body).toEqual({ name: 'trimmed-name' })
  })
})
