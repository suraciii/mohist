import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { toast } from 'sonner'

import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { ProjectProvider } from '../../../entities/project'
import { CreateIssueDialog } from './CreateIssueDialog'
import { useMswServer } from '../../../../tests/support/msw'

interface RepositorySeed {
  name: string
  isDefault: boolean
}

const _projectId = 'proj_assignment_repo'
let _repositories: RepositorySeed[] = [{ name: 'main', isDefault: true }]
let _issues: Issue[] = []
let _createIssueResponse: Pick<Issue, 'number'> = { number: 110 }
let _createIssueErrorCode: string | null = null
let _createIssueErrorMessage: string | null = null

const createIssueRequests: Array<Record<string, unknown>> = []

const createIssueHandler = vi.fn(async (info: { request: Request }) => {
  const body = await info.request.clone().json() as Record<string, unknown>
  createIssueRequests.push(body)
  if (_createIssueErrorCode) {
    return HttpResponse.json(
      { success: false, error: _createIssueErrorMessage ?? 'server failure', code: _createIssueErrorCode },
      { status: _createIssueErrorCode === 'parent_ineligible' || _createIssueErrorCode === 'parent_is_sub_issue' ? 409 : 400 },
    )
  }
  return HttpResponse.json({ success: true, data: _createIssueResponse })
})

const issuesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: _issues }),
)

const repositoriesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: _repositories }),
)

const modelsHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } }),
)

const workflowProfilesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: [] }),
)

const projectWorkflowProfileHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: { projectId: _projectId, defaultTemplateId: null, disabledWorkflowProfileIds: [] } }),
)

const issueTemplatesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: [] }),
)

useMswServer(
  http.post(`*/api/projects/:projectId/issues`, createIssueHandler),
  http.get(`*/api/projects/:projectId/issues`, issuesHandler),
  http.get(`*/api/projects/:projectId/repositories`, repositoriesHandler),
  http.get(`*/api/projects/:projectId/opencode/models`, modelsHandler),
  http.get(`*/api/workflow-templates/system`, workflowProfilesHandler),
  http.get(`*/api/projects/:projectId/workflow-profile`, projectWorkflowProfileHandler),
  http.get(`*/api/issue-templates*`, issueTemplatesHandler),
)

function setRepositories(repositories: RepositorySeed[]) {
  _repositories = repositories.map((repo) => ({ ...repo }))
}

function setIssues(issues: Issue[]) {
  _issues = issues.map((issue) => ({ ...issue }))
}

function makeBaseIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Sample',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: _projectId,
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function renderAssignmentDialog() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider
        initialProjectId={_projectId}
        initialProjects={[{
          id: _projectId,
          name: 'Project',
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
          repositories: [],
        }]}
      >
        <CreateIssueDialog open onClose={vi.fn()} />
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, ...view }
}

beforeEach(() => {
  setRepositories([{ name: 'main', isDefault: true }])
  setIssues([])
  _createIssueResponse = { number: 110 }
  _createIssueErrorCode = null
  _createIssueErrorMessage = null
  createIssueRequests.length = 0
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('CreateIssueDialog repository assignment', () => {
  it('pre-selects the declared default repository in a multi-repository project', async () => {
    setRepositories([
      { name: 'server', isDefault: true },
      { name: 'web', isDefault: false },
      { name: 'infra', isDefault: false },
    ])
    renderAssignmentDialog()

    const select = await screen.findByTestId('create-issue-repository-select') as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('server'))
    const optionLabels = Array.from(select.options).map((option) => option.textContent)
    expect(optionLabels).toEqual([
      'server (default)',
      'web ',
      'infra ',
    ])
  })

  it('submits the selected non-default repository when the user changes it', async () => {
    setRepositories([
      { name: 'server', isDefault: true },
      { name: 'web', isDefault: false },
    ])
    _createIssueResponse = { number: 301 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Web feature' } })

    const select = (await screen.findByTestId('create-issue-repository-select')) as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('server'))
    await user.selectOptions(select, 'web')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests[0]).toMatchObject({ title: 'Web feature', repositoryName: 'web' })
    expect(toast.success).toHaveBeenCalledWith('Issue #301 created')
  })

  it('submits the sole repository in a single-repository project without showing a dropdown', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    _createIssueResponse = { number: 7 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    await waitFor(() => expect(screen.queryByTestId('create-issue-repository-select')).not.toBeInTheDocument())
    await waitFor(() => expect(screen.queryByTestId('create-issue-repository-loading')).not.toBeInTheDocument())

    const label = await screen.findByTestId('create-issue-repository-label')
    await waitFor(() => expect(label.textContent).toMatch(/main/))

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Single repo' } })
    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests[0]).toMatchObject({ title: 'Single repo', repositoryName: 'main' })
  })

  it('shows a loading placeholder while the repository list is still loading', () => {
    renderAssignmentDialog()
    const loading = screen.getByTestId('create-issue-repository-loading')
    expect(loading).toHaveTextContent(/loading repository/i)
    expect(screen.queryByTestId('create-issue-repository-select')).not.toBeInTheDocument()
    expect(screen.queryByTestId('create-issue-repository-label')).not.toBeInTheDocument()
  })
})

describe('CreateIssueDialog parent assignment happy path', () => {
  it('offers the eligible parent issues and excludes terminal and child issues', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([
      makeBaseIssue({ number: 1, title: 'Done parent', status: IssueStatus.Done }),
      makeBaseIssue({ number: 2, title: 'Cancelled parent', status: IssueStatus.Cancelled }),
      makeBaseIssue({ number: 3, title: 'Child of another', parentIssueRef: { number: 9, title: 'Outer' } }),
      makeBaseIssue({ number: 4, title: 'Eligible parent', status: IssueStatus.Backlog }),
      makeBaseIssue({ number: 5, title: 'In-progress parent', status: IssueStatus.InProgress }),
    ])
    renderAssignmentDialog()

    const select = (await screen.findByTestId('create-issue-parent-select')) as HTMLSelectElement
    await waitFor(() => {
      const labels = Array.from(select.options).map((option) => option.textContent)
      expect(labels).toEqual([
        'No parent (ordinary issue)',
        '#4 · Eligible parent',
        '#5 · In-progress parent',
      ])
    })
  })

  it('submits the selected parentIssueNumber alongside the repositoryName in a single POST', async () => {
    setRepositories([
      { name: 'server', isDefault: true },
      { name: 'web', isDefault: false },
    ])
    setIssues([makeBaseIssue({ number: 42, title: 'Parent forty-two', status: IssueStatus.Backlog })])
    _createIssueResponse = { number: 200 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Child of 42' } })

    const repoSelect = await screen.findByTestId('create-issue-repository-select')
    await user.selectOptions(repoSelect, 'web')

    const parentSelect = (await screen.findByTestId('create-issue-parent-select')) as HTMLSelectElement
    await waitFor(() => expect(parentSelect.querySelector('option[value="42"]')).toBeInTheDocument())
    await user.selectOptions(parentSelect, '42')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests).toHaveLength(1)
    expect(createIssueRequests[0]).toMatchObject({
      title: 'Child of 42',
      repositoryName: 'web',
      parentIssueNumber: 42,
    })
  })

  it('omits parentIssueNumber when the parent selection is empty', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([makeBaseIssue({ number: 1, title: 'Parent' })])
    _createIssueResponse = { number: 11 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Ordinary' } })
    const parentSelect = await screen.findByTestId('create-issue-parent-select')
    await waitFor(() => expect(parentSelect.querySelector('option[value="1"]')).toBeInTheDocument())

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests[0]).not.toHaveProperty('parentIssueNumber')
    expect(createIssueRequests[0]).toMatchObject({ title: 'Ordinary', repositoryName: 'main' })
  })

  it('does not inherit the parent repository when the user selects a different one', async () => {
    setRepositories([
      { name: 'server', isDefault: true },
      { name: 'web', isDefault: false },
    ])
    setIssues([makeBaseIssue({
      number: 42,
      title: 'Parent on server',
      status: IssueStatus.Backlog,
      repositoryName: 'server',
    })])
    _createIssueResponse = { number: 201 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Cross-repo child' } })

    const repoSelect = await screen.findByTestId('create-issue-repository-select')
    await user.selectOptions(repoSelect, 'web')
    const parentSelect = await screen.findByTestId('create-issue-parent-select')
    await user.selectOptions(parentSelect, '42')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests[0]).toMatchObject({
      title: 'Cross-repo child',
      parentIssueNumber: 42,
      repositoryName: 'web',
    })
  })
})
