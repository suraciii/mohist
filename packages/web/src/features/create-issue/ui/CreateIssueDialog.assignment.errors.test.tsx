import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { toast } from 'sonner'

import { issueCandidateKeys, issueDetailKeys, issueListKeys, IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { ProjectProvider } from '../../../entities/project'
import { CreateIssueDialog } from './CreateIssueDialog'
import { useMswServer } from '../../../../tests/support/msw'

interface RepositorySeed {
  name: string
  isDefault: boolean
}

const _projectId = 'proj_assignment_errors'
let _repositories: RepositorySeed[] = [{ name: 'main', isDefault: true }]
let _issues: Issue[] = []
let _createIssueResponse: Pick<Issue, 'number'> = { number: 120 }
let _createIssueErrorCode: string | null = null
let _createIssueErrorMessage: string | null = null

const createIssueRequests: Array<Record<string, unknown>> = []
const invalidatedQueryKeys: string[][] = []

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

const parentCandidatesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: _issues.filter((issue) => issue.canBeParent).map(({ number, title }) => ({ number, title })) }),
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
  HttpResponse.json({ success: true, data: { projectId: _projectId, defaultWorkflowProfileId: null, disabledWorkflowProfileIds: [] } }),
)

const issueTemplatesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: [] }),
)

useMswServer(
  http.post(`*/api/projects/:projectId/issues`, createIssueHandler),
  http.get(`*/api/projects/:projectId/issues/parent-candidates`, parentCandidatesHandler),
  http.get(`*/api/projects/:projectId/issues`, issuesHandler),
  http.get(`*/api/projects/:projectId/repositories`, repositoriesHandler),
  http.get(`*/api/projects/:projectId/opencode/models`, modelsHandler),
  http.get(`*/api/projects/:projectId/workflow-profiles`, workflowProfilesHandler),
  http.get(`*/api/projects/:projectId/workflow-profile/default`, projectWorkflowProfileHandler),
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
    canBeParent: true,
    blocker: null,
    ...overrides,
  }
}

function renderAssignmentDialog(open = true) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries').mockImplementation(async (filter) => {
    const key = (filter as { queryKey: readonly unknown[] }).queryKey
    invalidatedQueryKeys.push(key.map((part) => String(part)))
    return Promise.resolve()
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
        <CreateIssueDialog open={open} onClose={vi.fn()} />
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, invalidateSpy, ...view }
}

beforeEach(() => {
  setRepositories([{ name: 'main', isDefault: true }])
  setIssues([])
  _createIssueResponse = { number: 120 }
  _createIssueErrorCode = null
  _createIssueErrorMessage = null
  createIssueRequests.length = 0
  invalidatedQueryKeys.length = 0
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('CreateIssueDialog request loading', () => {
  it('does not mount or request issue candidates while closed', () => {
    renderAssignmentDialog(false)

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(parentCandidatesHandler).not.toHaveBeenCalled()
    expect(issuesHandler).not.toHaveBeenCalled()
  })

  it('requests parent candidates once when opened', async () => {
    renderAssignmentDialog()

    await waitFor(() => expect(parentCandidatesHandler).toHaveBeenCalledTimes(1))
    expect(issuesHandler).not.toHaveBeenCalled()
  })

  it('defers the prerequisite issue summary until the picker opens', async () => {
    const user = userEvent.setup()
    renderAssignmentDialog()

    await waitFor(() => expect(parentCandidatesHandler).toHaveBeenCalledTimes(1))
    expect(issuesHandler).not.toHaveBeenCalled()

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    await waitFor(() => expect(issuesHandler).toHaveBeenCalledTimes(1))
  })
})

describe('CreateIssueDialog assignment validation', () => {
  it.each([
    {
      code: 'repository_not_found',
      serverMessage: 'Repository \'gone\' is not declared',
      expectInMessage: /repository/i,
    },
    {
      code: 'parent_not_found',
      serverMessage: 'Issue #99 not found',
      expectInMessage: /parent/i,
    },
    {
      code: 'parent_ineligible',
      serverMessage: 'Parent #50 cannot accept children',
      expectInMessage: /eligible/i,
    },
    {
      code: 'parent_is_sub_issue',
      serverMessage: 'Issue #50 is itself a sub-issue',
      expectInMessage: /sub-issue/i,
    },
  ] as const)('keeps the dialog open and surfaces $code with a specific message', async ({ code, serverMessage, expectInMessage }) => {
    setRepositories([
      { name: 'server', isDefault: true },
      { name: 'web', isDefault: false },
    ])
    setIssues([makeBaseIssue({ number: 50, title: 'Fifty', status: IssueStatus.Backlog })])
    _createIssueErrorCode = code
    _createIssueErrorMessage = serverMessage

    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Will fail (typed)' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: 'Keep me around' } })

    const repoSelect = await screen.findByTestId('create-issue-repository-select')
    await user.selectOptions(repoSelect, 'web')
    const parentSelect = await screen.findByTestId('create-issue-parent-select')
    await waitFor(() => expect(parentSelect.querySelector('option[value="50"]')).toBeInTheDocument())
    await user.selectOptions(parentSelect, '50')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    const errorBanner = await screen.findByTestId('create-issue-assignment-error')
    expect(errorBanner).toHaveTextContent(expectInMessage)

    expect(createIssueHandler).toHaveBeenCalledTimes(1)
    expect(toast.success).not.toHaveBeenCalled()

    expect((screen.getByPlaceholderText('Issue title') as HTMLInputElement).value).toBe('Will fail (typed)')
    expect((screen.getByPlaceholderText('Optional description') as HTMLTextAreaElement).value).toBe('Keep me around')
    expect((screen.getByTestId('create-issue-repository-select') as HTMLSelectElement).value).toBe('web')
    expect((screen.getByTestId('create-issue-parent-select') as HTMLSelectElement).value).toBe('50')
  })

  it('clears the assignment error banner when the user resubmits and the request succeeds', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([makeBaseIssue({ number: 1, title: 'Parent' })])
    _createIssueErrorCode = 'repository_not_found'
    _createIssueErrorMessage = 'Repository \'gone\' is not declared'

    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Recover' } })
    await user.click(screen.getByRole('button', { name: 'Create' }))
    await screen.findByTestId('create-issue-assignment-error')

    _createIssueErrorCode = null
    _createIssueResponse = { number: 999 }

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Issue #999 created'))
    await waitFor(() => expect(screen.queryByTestId('create-issue-assignment-error')).not.toBeInTheDocument())
  })

  it('keeps the form open and never reports success on assignment errors', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([])
    _createIssueErrorCode = 'parent_ineligible'
    _createIssueErrorMessage = 'Parent terminal'

    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No success' } })
    await user.click(screen.getByRole('button', { name: 'Create' }))

    await screen.findByTestId('create-issue-assignment-error')
    expect(screen.getByText('Create Issue')).toBeInTheDocument()
    expect(toast.success).not.toHaveBeenCalled()
  })
})

describe('CreateIssueDialog success invalidation', () => {
  it('invalidates the project issue list and the parent detail queries after a successful child create', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([makeBaseIssue({ number: 42, title: 'Parent' })])
    _createIssueResponse = { number: 200 }

    const user = userEvent.setup()
    const { invalidateSpy } = renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'New child' } })

    const parentSelect = (await screen.findByTestId('create-issue-parent-select')) as HTMLSelectElement
    await waitFor(() => expect(parentSelect.querySelector('option[value="42"]')).toBeInTheDocument())
    await user.selectOptions(parentSelect, '42')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(invalidateSpy).toHaveBeenCalled())

    expect(invalidatedQueryKeys).toContainEqual(issueListKeys.project(_projectId).map(String))
    expect(invalidatedQueryKeys).toContainEqual(issueDetailKeys.detail(_projectId, 42).map(String))
    expect(invalidatedQueryKeys).toContainEqual(issueCandidateKeys.project(_projectId).map(String))
  })

  it('invalidates only the active project list when no parent is set', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([])
    _createIssueResponse = { number: 1 }

    const user = userEvent.setup()
    const { invalidateSpy } = renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Lone issue' } })
    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(invalidateSpy).toHaveBeenCalled())

    expect(invalidatedQueryKeys).toContainEqual(issueListKeys.project(_projectId).map(String))

    expect(invalidatedQueryKeys).not.toContainEqual(issueDetailKeys.detail(_projectId, 1).map(String))
  })
})

describe('CreateIssueDialog non-assignment regression coverage', () => {
  it('creates issue with attachment ids from the composer body', async () => {
    _createIssueResponse = { number: 1 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'New issue' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: 'See ![screen](att:att_created)' } })
    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests[0]).toMatchObject({
      title: 'New issue',
      body: 'See ![screen](att:att_created)',
      attachmentIds: ['att_created'],
    })
  })

  it('still attaches prerequisites to the create payload alongside the new parent field', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([
      makeBaseIssue({ number: 5, title: 'Wire up auth', status: IssueStatus.InProgress }),
      makeBaseIssue({ number: 7, title: 'Audit auth tokens', status: IssueStatus.Backlog }),
      makeBaseIssue({ number: 1, title: 'Parent' }),
    ])
    _createIssueResponse = { number: 99 }
    const user = userEvent.setup()
    renderAssignmentDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Plan' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    const options = await screen.findAllByTestId('prerequisite-picker-option')
    const opt5 = options.find((opt) => opt.getAttribute('data-issue-number') === '5')
    await user.click(opt5!)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    expect(createIssueRequests[0]).toMatchObject({
      title: 'Plan',
      prerequisiteNumbers: [5],
    })
    expect(createIssueRequests[0]).not.toHaveProperty('parentIssueNumber')
  })

  it('clears the selected parent when the issue disappears from the eligible list', async () => {
    setRepositories([{ name: 'main', isDefault: true }])
    setIssues([makeBaseIssue({ number: 5, title: 'Transitional parent', status: IssueStatus.Backlog })])
    const user = userEvent.setup()
    const { queryClient } = renderAssignmentDialog()

    const parentSelect = (await screen.findByTestId('create-issue-parent-select')) as HTMLSelectElement
    await waitFor(() => expect(parentSelect.querySelector('option[value="5"]')).toBeInTheDocument())
    await user.selectOptions(parentSelect, '5')
    expect(parentSelect.value).toBe('5')

    setIssues([makeBaseIssue({ number: 5, title: 'Transitional parent', status: IssueStatus.Done, canBeParent: false })])
    await queryClient.refetchQueries({ queryKey: issueCandidateKeys.project(_projectId), exact: true })

    await waitFor(() => {
      const select = screen.getByTestId('create-issue-parent-select') as HTMLSelectElement
      expect(select.value).toBe('')
    }, { timeout: 3000 })
    queryClient.clear()
  })
})
