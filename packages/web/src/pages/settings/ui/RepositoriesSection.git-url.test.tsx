import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import { useMutation } from '@tanstack/react-query'
import type { AddRepositoryInput, Project, Repository } from '../../../entities/project'
import { RepositoriesSection, type RepositoriesSectionDataHook } from './RepositoriesSection'

const addRepositoryRequests: { method: string; url: string; body: unknown }[] = []
const setDefaultRequests: { method: string; url: string; body: unknown }[] = []
const removeRepositoryRequests: { method: string; url: string }[] = []

let repositoriesResponse: Repository[] = [
  {
    name: 'frontend',
    gitUrl: 'https://github.com/example/frontend.git',
    baseBranch: 'main',
    isDefault: true,
  },
  {
    name: 'backend',
    gitUrl: 'https://github.com/example/backend.git',
    baseBranch: 'develop',
    isDefault: false,
  },
]

function projectResponse(projectId: string): Project {
  return {
    id: projectId,
    name: 'Test project',
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    repositories: repositoriesResponse,
  }
}

const repositoriesDataHook: RepositoriesSectionDataHook = () => {
  const addRepo = useMutation({
    mutationFn: async ({ projectId, data }: {
      projectId: string
      data: AddRepositoryInput
    }) => {
      addRepositoryRequests.push({
        method: 'POST',
        url: `/api/projects/${projectId}/repositories`,
        body: data,
      })
      repositoriesResponse = [...repositoriesResponse, {
        name: data.name,
        gitUrl: data.gitUrl,
        baseBranch: data.baseBranch ?? 'main',
        isDefault: false,
      }]
      return projectResponse(projectId)
    },
  })
  const removeRepo = useMutation({
    mutationFn: async ({ projectId, repoName }: { projectId: string; repoName: string }) => {
      removeRepositoryRequests.push({
        method: 'DELETE',
        url: `/api/projects/${projectId}/repositories/${repoName}`,
      })
      repositoriesResponse = repositoriesResponse.filter((repo) => repo.name !== repoName)
      return projectResponse(projectId)
    },
  })
  const setDefault = useMutation({
    mutationFn: async ({ projectId, repoName }: { projectId: string; repoName: string }) => {
      setDefaultRequests.push({
        method: 'PATCH',
        url: `/api/projects/${projectId}/repositories/${repoName}`,
        body: { setDefault: true },
      })
      return projectResponse(projectId)
    },
  })

  return { repositories: repositoriesResponse, isLoading: false, addRepo, removeRepo, setDefault }
}

function renderSection() {
  return render(<RepositoriesSection projectId="proj-1" dataHook={repositoriesDataHook} />)
}

beforeEach(() => {
  addRepositoryRequests.length = 0
  setDefaultRequests.length = 0
  removeRepositoryRequests.length = 0
  repositoriesResponse = [
    {
      name: 'frontend',
      gitUrl: 'https://github.com/example/frontend.git',
      baseBranch: 'main',
      isDefault: true,
    },
    {
      name: 'backend',
      gitUrl: 'https://github.com/example/backend.git',
      baseBranch: 'develop',
      isDefault: false,
    },
  ]
})

describe('RepositoriesSection (git-url only)', () => {
  it('renders repository name, Git URL, base branch, and default status from server data', async () => {
    renderSection()

    const frontendRow = await screen.findByTestId('repository-frontend')
    expect(within(frontendRow).getByTestId('repository-name-frontend')).toHaveTextContent('frontend')
    expect(within(frontendRow).getByTestId('repository-giturl-frontend')).toHaveTextContent(
      'https://github.com/example/frontend.git',
    )
    expect(within(frontendRow).getByTestId('repository-default-badge-frontend')).toBeInTheDocument()
    expect(within(frontendRow).getByTestId('repository-default-badge-frontend')).toHaveTextContent('default')
    expect(frontendRow.dataset.repositoryDefault).toBe('true')

    const backendRow = screen.getByTestId('repository-backend')
    expect(within(backendRow).getByTestId('repository-name-backend')).toHaveTextContent('backend')
    expect(within(backendRow).getByTestId('repository-giturl-backend')).toHaveTextContent(
      'https://github.com/example/backend.git',
    )
    expect(within(backendRow).getByTestId('repository-basebranch-backend')).toHaveTextContent('develop')
    expect(within(backendRow).queryByTestId('repository-default-badge-backend')).not.toBeInTheDocument()
    expect(backendRow.dataset.repositoryDefault).toBe('false')
  })

  it('does not show a Local Path input or Remote URL input in the add form', async () => {
    renderSection()

    await screen.findByTestId('repository-add-form')
    const form = screen.getByTestId('repository-add-form')
    expect(within(form).getByTestId('repository-add-name')).toBeInTheDocument()
    expect(within(form).getByTestId('repository-add-giturl')).toBeInTheDocument()
    expect(within(form).getByTestId('repository-add-branch')).toBeInTheDocument()
    expect(within(form).queryByTestId('repository-add-path')).not.toBeInTheDocument()
    expect(within(form).queryByTestId('repository-add-local-path')).not.toBeInTheDocument()
    expect(within(form).queryByTestId('repository-add-remote')).not.toBeInTheDocument()
    expect(within(form).queryByTestId('repository-add-remote-url')).not.toBeInTheDocument()
    expect(within(form).queryByLabelText(/local path/i)).not.toBeInTheDocument()
    expect(within(form).queryByLabelText(/remote url/i)).not.toBeInTheDocument()
  })

  it('submits a POST with gitUrl and never sends path or remote', async () => {
    renderSection()

    const form = await screen.findByTestId('repository-add-form')
    fireEvent.change(within(form).getByTestId('repository-add-name'), {
      target: { value: 'new-repo' },
    })
    fireEvent.change(within(form).getByTestId('repository-add-giturl'), {
      target: { value: 'https://github.com/example/new-repo.git' },
    })
    fireEvent.change(within(form).getByTestId('repository-add-branch'), {
      target: { value: 'trunk' },
    })
    fireEvent.click(within(form).getByTestId('repository-add-submit'))

    await waitFor(() => expect(addRepositoryRequests).toHaveLength(1))
    const request = addRepositoryRequests[0]
    expect(request.method).toBe('POST')
    expect(request.url).toContain('/api/projects/proj-1/repositories')
    expect(request.body).toEqual({
      name: 'new-repo',
      gitUrl: 'https://github.com/example/new-repo.git',
      baseBranch: 'trunk',
    })
    const body = request.body as Record<string, unknown>
    expect(body).not.toHaveProperty('path')
    expect(body).not.toHaveProperty('remote')
    expect(body).not.toHaveProperty('localPath')
    expect(body).not.toHaveProperty('remoteUrl')
  })

  it('disables the submit button when Git URL is empty and shows no path-required error', async () => {
    renderSection()

    const form = await screen.findByTestId('repository-add-form')
    fireEvent.change(within(form).getByTestId('repository-add-name'), {
      target: { value: 'no-url' },
    })

    const submit = within(form).getByTestId('repository-add-submit') as HTMLButtonElement
    expect(submit).toBeDisabled()
    expect(within(form).queryByText(/path is required/i)).not.toBeInTheDocument()
    expect(within(form).queryByText(/git url is required/i)).not.toBeInTheDocument()
  })

  it('enables submit when name and Git URL are filled and no path is required', async () => {
    renderSection()

    const form = await screen.findByTestId('repository-add-form')
    fireEvent.change(within(form).getByTestId('repository-add-name'), {
      target: { value: 'ready' },
    })
    fireEvent.change(within(form).getByTestId('repository-add-giturl'), {
      target: { value: 'https://github.com/example/ready.git' },
    })

    const submit = within(form).getByTestId('repository-add-submit') as HTMLButtonElement
    expect(submit).not.toBeDisabled()
  })

  it('omits removed path/remote fields even when the add form is submitted with extra fields', async () => {
    renderSection()

    const form = await screen.findByTestId('repository-add-form')
    fireEvent.change(within(form).getByTestId('repository-add-name'), {
      target: { value: 'minimal' },
    })
    fireEvent.change(within(form).getByTestId('repository-add-giturl'), {
      target: { value: 'https://github.com/example/minimal.git' },
    })
    fireEvent.click(within(form).getByTestId('repository-add-submit'))

    await waitFor(() => expect(addRepositoryRequests).toHaveLength(1))
    const body = addRepositoryRequests[0].body as Record<string, unknown>
    const keys = Object.keys(body)
    expect(keys).toContain('name')
    expect(keys).toContain('gitUrl')
    expect(keys).toContain('baseBranch')
    expect(keys).not.toContain('path')
    expect(keys).not.toContain('remote')
    expect(keys).not.toContain('resolvedPath')
    expect(keys).not.toContain('localPath')
  })

  it('renders set-default and remove controls only for non-default repositories', async () => {
    renderSection()

    await screen.findByTestId('repository-frontend')
    expect(screen.queryByTestId('repository-set-default-frontend')).not.toBeInTheDocument()
    expect(screen.queryByTestId('repository-remove-frontend')).not.toBeInTheDocument()

    expect(screen.getByTestId('repository-set-default-backend')).toBeInTheDocument()
    expect(screen.getByTestId('repository-remove-backend')).toBeInTheDocument()
  })

  describe('Remove confirmation flow', () => {
    it('opens the shared AlertDialog when Remove is clicked and does not send DELETE before confirm', async () => {
      renderSection()

      await screen.findByTestId('repository-backend')

      fireEvent.click(screen.getByTestId('repository-remove-backend'))

      const dialog = await screen.findByTestId('repository-remove-alert')
      expect(dialog).toBeInTheDocument()
      expect(dialog).toHaveAttribute('data-tone', 'destructive')

      expect(removeRepositoryRequests).toHaveLength(0)

      fireEvent.click(screen.getByTestId('repository-remove-alert-cancel'))

      await waitFor(() =>
        expect(screen.queryByTestId('repository-remove-alert')).not.toBeInTheDocument(),
      )

      expect(removeRepositoryRequests).toHaveLength(0)
    })

    it('does not invoke the remove mutation until the user confirms', async () => {
      renderSection()

      await screen.findByTestId('repository-backend')

      fireEvent.click(screen.getByTestId('repository-remove-backend'))

      await screen.findByTestId('repository-remove-alert')
      expect(removeRepositoryRequests).toHaveLength(0)

      fireEvent.click(screen.getByTestId('repository-remove-alert-confirm'))

      await waitFor(() => expect(removeRepositoryRequests).toHaveLength(1))
      expect(removeRepositoryRequests[0].method).toBe('DELETE')
      expect(removeRepositoryRequests[0].url).toContain('/api/projects/proj-1/repositories/backend')

      await waitFor(() =>
        expect(screen.queryByTestId('repository-remove-alert')).not.toBeInTheDocument(),
      )
    })

    it('renders a single AlertDialog instance for the section, not per row', async () => {
      const initialRepos: Repository[] = [
        {
          name: 'a',
          gitUrl: 'https://example.com/a.git',
          baseBranch: 'main',
          isDefault: false,
        },
        {
          name: 'b',
          gitUrl: 'https://example.com/b.git',
          baseBranch: 'main',
          isDefault: false,
        },
      ]
      repositoriesResponse = initialRepos

      renderSection()

      await screen.findByTestId('repository-a')
      await screen.findByTestId('repository-b')

      fireEvent.click(screen.getByTestId('repository-remove-a'))

      const dialog = await screen.findByTestId('repository-remove-alert')
      expect(dialog).toBeInTheDocument()

      const allDialogs = document.querySelectorAll('[data-testid="repository-remove-alert"]')
      expect(allDialogs).toHaveLength(1)

      fireEvent.click(screen.getByTestId('repository-remove-alert-cancel'))
      await waitFor(() =>
        expect(screen.queryByTestId('repository-remove-alert')).not.toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('repository-remove-b'))
      const dialog2 = await screen.findByTestId('repository-remove-alert')
      expect(dialog2).toBeInTheDocument()
    })

    it('does not call DELETE on the initial trigger click across multiple rows', async () => {
      const initialRepos: Repository[] = [
        {
          name: 'a',
          gitUrl: 'https://example.com/a.git',
          baseBranch: 'main',
          isDefault: false,
        },
        {
          name: 'b',
          gitUrl: 'https://example.com/b.git',
          baseBranch: 'main',
          isDefault: false,
        },
      ]
      repositoriesResponse = initialRepos

      renderSection()

      await screen.findByTestId('repository-a')
      await screen.findByTestId('repository-b')

      fireEvent.click(screen.getByTestId('repository-remove-a'))
      expect(removeRepositoryRequests).toHaveLength(0)
      fireEvent.click(screen.getByTestId('repository-remove-b'))
      expect(removeRepositoryRequests).toHaveLength(0)

      expect(screen.getByTestId('repository-remove-alert')).toBeInTheDocument()
    })
  })
})
