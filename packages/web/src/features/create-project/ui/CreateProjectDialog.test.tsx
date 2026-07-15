import {
  afterEach,
  beforeEach,
  describe,
  expect,
  it,
} from 'vitest'
import { useState } from 'react'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import { CreateProjectDialog } from '..'
import { useProject, type ProjectCreator } from '../../../entities/project'

const createRequests: { name: string; repository: { name: string; gitUrl: string; baseBranch?: string } }[] = []

const createProject: ProjectCreator = async (request) => {
  createRequests.push(request)
  if (request.name === 'existing') {
    throw new Error('Project name already exists')
  }
  return {
    id: `proj-${request.name}`,
    name: request.name,
    createdAt: '2026-06-12T00:00:00.000Z',
    updatedAt: '2026-06-12T00:00:00.000Z',
    repositories: [],
  }
}

function ActiveProjectProbe() {
  const { projectId } = useProject()
  return <div data-testid="active-project-id">{projectId ?? ''}</div>
}

function HostDialog() {
  const [open, setOpen] = useState(true)
  return (
    <>
      <ActiveProjectProbe />
      <CreateProjectDialog
        open={open}
        onClose={() => setOpen(false)}
        projectCreator={createProject}
      />
    </>
  )
}

beforeEach(() => {
  createRequests.length = 0
})

afterEach(() => {
  window.localStorage.clear()
})

function openDialog() {
  return render(<HostDialog />)
}

function fillCreateProjectForm(
  dialog: HTMLElement,
  name: string,
  repository = {
    name: 'server',
    gitUrl: 'https://github.com/example/server.git',
    baseBranch: 'main',
  },
) {
  fireEvent.change(within(dialog).getByTestId('create-project-name'), {
    target: { value: name },
  })
  fireEvent.change(within(dialog).getByTestId('create-project-repository-name'), {
    target: { value: repository.name },
  })
  fireEvent.change(within(dialog).getByTestId('create-project-repository-git-url'), {
    target: { value: repository.gitUrl },
  })
  fireEvent.change(within(dialog).getByTestId('create-project-repository-base-branch'), {
    target: { value: repository.baseBranch },
  })
}

describe('CreateProjectDialog', () => {
  it('submits the project name and a repository declaration', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    expect(dialog).toBeInTheDocument()

    fillCreateProjectForm(dialog, 'my-project', {
      name: 'server',
      gitUrl: 'git@github.com:example/server.git',
      baseBranch: 'trunk',
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => expect(createRequests).toHaveLength(1))
    expect(createRequests[0].name).toBe('my-project')
    expect(createRequests[0].repository).toEqual({
      name: 'server',
      gitUrl: 'git@github.com:example/server.git',
      baseBranch: 'trunk',
    })
    const body = createRequests[0] as Record<string, unknown>
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

  it('requires complete repository metadata without requiring a local path', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    const submit = within(dialog).getByTestId('create-project-submit') as HTMLButtonElement
    expect(submit).toBeDisabled()
    expect(within(dialog).queryByText(/path is required/i)).not.toBeInTheDocument()
  })

  it('enables submit when the project and required repository fields are entered', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')
    fillCreateProjectForm(dialog, 'project-with-repository')

    const submit = within(dialog).getByTestId('create-project-submit') as HTMLButtonElement
    expect(submit).not.toBeDisabled()
  })

  it('keeps the dialog open and shows "Project name already exists" on 409 conflict', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    fillCreateProjectForm(dialog, 'existing')
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

    fillCreateProjectForm(dialog, 'switched-project')
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => {
      expect(screen.getByTestId('active-project-id').textContent).toBe('proj-switched-project')
    })
  })

  it('closes the dialog on successful creation so the project list refreshes', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    fillCreateProjectForm(dialog, 'close-on-success')
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => {
      expect(screen.queryByTestId('create-project-dialog')).not.toBeInTheDocument()
    })
  })

  it('trims the name before submission', async () => {
    openDialog()
    const dialog = await screen.findByTestId('create-project-dialog')

    fillCreateProjectForm(dialog, '  trimmed-name  ', {
      name: '  backend  ',
      gitUrl: '  https://github.com/example/backend.git  ',
      baseBranch: '  main  ',
    })
    fireEvent.click(within(dialog).getByTestId('create-project-submit'))

    await waitFor(() => expect(createRequests).toHaveLength(1))
    expect(createRequests[0].name).toBe('trimmed-name')
    expect(createRequests[0].repository).toEqual({
      name: 'backend',
      gitUrl: 'https://github.com/example/backend.git',
      baseBranch: 'main',
    })
  })
})
