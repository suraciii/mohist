// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from './test-utils'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { RepositoriesSection } from '../src/pages/settings/ui/RepositoriesSection'

const addRepositoryRequests: { method: string; url: string; body: unknown }[] = []
const setDefaultRequests: { method: string; url: string; body: unknown }[] = []

let repositoriesResponse: Array<Record<string, unknown>> = [
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

const handlers = [
  http.get('/api/projects/:projectId/repositories', () => {
    return HttpResponse.json({ success: true, data: repositoriesResponse })
  }),
  http.post('/api/projects/:projectId/repositories', async ({ request, params }) => {
    const body = await request.json()
    addRepositoryRequests.push({ method: request.method, url: request.url, body })
    const data = body as { name: string; gitUrl: string; baseBranch?: string }
    if (!data.gitUrl || !data.gitUrl.trim()) {
      return HttpResponse.json(
        { success: false, error: 'gitUrl is required' },
        { status: 400 },
      )
    }
    if (data.name === 'conflict') {
      return HttpResponse.json(
        { success: false, error: 'Repository name already exists' },
        { status: 409 },
      )
    }
    const next = {
      name: data.name,
      gitUrl: data.gitUrl,
      baseBranch: data.baseBranch ?? 'main',
      isDefault: false,
    }
    repositoriesResponse = [...repositoriesResponse, next]
    return HttpResponse.json(
      { success: true, data: { id: params.projectId, repositories: repositoriesResponse } },
      { status: 201 },
    )
  }),
  http.patch('/api/projects/:projectId/repositories/:repoName', async ({ request }) => {
    const body = await request.json()
    setDefaultRequests.push({ method: request.method, url: request.url, body })
    return HttpResponse.json({ success: true, data: { repositories: repositoriesResponse } })
  }),
  http.delete('/api/projects/:projectId/repositories/:repoName', () => {
    return HttpResponse.json({ success: true, data: { repositories: repositoriesResponse } })
  }),
]

const server = setupServer(...handlers)

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})

afterAll(() => {
  server.close()
})

beforeEach(() => {
  addRepositoryRequests.length = 0
  setDefaultRequests.length = 0
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
  server.resetHandlers(...handlers)
})

afterEach(() => {
  server.resetHandlers(...handlers)
})

describe('RepositoriesSection (git-url only)', () => {
  it('renders repository name, Git URL, base branch, and default status from server data', async () => {
    render(<RepositoriesSection projectId="proj-1" />)

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
    render(<RepositoriesSection projectId="proj-1" />)

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
    render(<RepositoriesSection projectId="proj-1" />)

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
    render(<RepositoriesSection projectId="proj-1" />)

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
    render(<RepositoriesSection projectId="proj-1" />)

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
    render(<RepositoriesSection projectId="proj-1" />)

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
    render(<RepositoriesSection projectId="proj-1" />)

    await screen.findByTestId('repository-frontend')
    expect(screen.queryByTestId('repository-set-default-frontend')).not.toBeInTheDocument()
    expect(screen.queryByTestId('repository-remove-frontend')).not.toBeInTheDocument()

    expect(screen.getByTestId('repository-set-default-backend')).toBeInTheDocument()
    expect(screen.getByTestId('repository-remove-backend')).toBeInTheDocument()
  })
})
