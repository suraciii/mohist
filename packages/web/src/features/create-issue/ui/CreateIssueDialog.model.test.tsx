import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'

import { type Issue } from '../../../entities/issue'
import { ProjectProvider } from '../../../entities/project'
import { CreateIssueDialog } from './CreateIssueDialog'
import { useMswServer } from '../../../../tests/support/msw'

let _issuesData: Issue[] = []
let _createIssueResponse: Pick<Issue, 'number'> = { number: 42 }
const _repositoriesData = [{ name: 'main', isDefault: true }]
const _modelsData = { models: [] as string[], modelVariants: {} as Record<string, string[]> }
const _workflowProfilesData: {
  id: string
  displayName: string
  description: string
  isDefault: boolean
  agentRuntime?: 'opencode' | 'pi' | null
}[] = []
const _projectWorkflowProfile: {
  projectId: string
  defaultTemplateId: string | null
  disabledWorkflowProfileIds: string[]
} = {
  projectId: 'proj_create',
  defaultTemplateId: null,
  disabledWorkflowProfileIds: [],
}
const _issueTemplatesData: { id: string; name: string; description: string; body: string; source: string }[] = []

const createIssueHandler = vi.fn(async (info: { request: Request }) => {
  const body = await info.request.clone().json()
  void body
  return HttpResponse.json({ success: true, data: _createIssueResponse })
})

const issuesHandler = vi.fn((info: { request: Request }) => {
  void info.request.url
  return HttpResponse.json({ success: true, data: _issuesData })
})

const parentCandidatesHandler = vi.fn(() => {
  return HttpResponse.json({
    success: true,
    data: _issuesData.filter((issue) => issue.canBeParent).map(({ number, title }) => ({ number, title })),
  })
})

const repositoriesHandler = vi.fn(() => HttpResponse.json({ success: true, data: _repositoriesData }))

const modelsHandler = vi.fn((info: { request: Request }) => {
  void info.request.url
  return HttpResponse.json({ success: true, data: _modelsData })
})

const workflowProfilesHandler = vi.fn(() =>
  HttpResponse.json({
    success: true,
    data: (_workflowProfilesData.length > 0
      ? _workflowProfilesData
      : [
          {
            id: 'mohist/local',
            displayName: 'Default',
            description: '',
            isDefault: true,
            agentRuntime: 'opencode' as const,
          },
        ]
    ).map((profile) => ({
      projectId: 'proj_create',
      profileId: profile.id,
      name: profile.displayName,
      description: profile.description,
      sourceProvenance: 'BuiltIn',
      isBuiltIn: true,
      definitionSource: null,
      agentRuntime: profile.agentRuntime ?? null,
    })),
  }),
)

const projectWorkflowProfileHandler = vi.fn(() =>
  HttpResponse.json({
    success: true,
    data: {
      projectId: _projectWorkflowProfile.projectId,
      defaultWorkflowProfileId: _projectWorkflowProfile.defaultTemplateId,
      disabledWorkflowProfileIds: _projectWorkflowProfile.disabledWorkflowProfileIds,
    },
  }),
)

const issueTemplatesHandler = vi.fn((info: { request: Request }) => {
  const url = new URL(info.request.url)
  const namePath = url.pathname.match(/\/api\/issue-templates\/(.+)/)?.[1]
  if (namePath) {
    const found = _issueTemplatesData.find((t) => t.id === decodeURIComponent(namePath))
    return HttpResponse.json({ success: true, data: found ?? null })
  }
  return HttpResponse.json({ success: true, data: _issueTemplatesData })
})

useMswServer(
  http.post('*/api/projects/:projectId/issues', createIssueHandler),
  http.get('*/api/projects/:projectId/issues/parent-candidates', parentCandidatesHandler),
  http.get('*/api/projects/:projectId/issues', issuesHandler),
  http.get('*/api/projects/:projectId/repositories', repositoriesHandler),
  http.get('*/api/projects/:projectId/opencode/models', modelsHandler),
  http.get('*/api/projects/:projectId/workflow-profiles', workflowProfilesHandler),
  http.get('*/api/projects/:projectId/workflow-profile/default', projectWorkflowProfileHandler),
  http.get('*/api/issue-templates*', issueTemplatesHandler),
)

function renderDialog(open = true) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider
        initialProjectId="proj_create"
        initialProjects={[
          {
            id: 'proj_create',
            name: 'Project',
            createdAt: '2026-01-01T00:00:00Z',
            updatedAt: '2026-01-01T00:00:00Z',
            repositories: [],
          },
        ]}
      >
        <CreateIssueDialog open={open} onClose={vi.fn()} />
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, ...view }
}

describe('CreateIssueDialog model + variant chips', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
    _issuesData = []
    _createIssueResponse = { number: 1 }
    _modelsData.models = []
    _modelsData.modelVariants = {}
    _workflowProfilesData.length = 0
    _projectWorkflowProfile.defaultTemplateId = null
    _projectWorkflowProfile.disabledWorkflowProfileIds = []
    _issueTemplatesData.length = 0
  })

  async function modelTrigger() {
    return waitFor(() => {
      const trigger = document.getElementById('create-issue-model-trigger')
      if (!trigger) throw new Error('model trigger not found')
      return trigger
    })
  }

  it('does not render a standalone variant picker anywhere', () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    renderDialog()
    expect(screen.queryByTestId('create-issue-model-variant-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders inline variant chips on a variant-capable model row', async () => {
    _modelsData.models = ['anthropic/claude', 'openai/gpt-4']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'medium', 'high'] }
    const user = userEvent.setup()
    renderDialog()
    await user.click(await modelTrigger())

    for (const variant of ['low', 'medium', 'high']) {
      const chip = document.querySelector(
        `[data-testid="create-issue-model-trigger-row-anthropic/claude-variant-${variant}"]`,
      )
      expect(chip).toBeInTheDocument()
    }
    expect(document.querySelector(`[data-testid="create-issue-model-trigger-row-openai/gpt-4-variant-low"]`)).toBeNull()
  })

  it('sends modelVariant alongside model on create when a chip is clicked', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()
    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })

    await user.click(await modelTrigger())

    const highChip = await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-high')
    await user.click(highChip)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(
      expect.objectContaining({
        model: 'anthropic/claude',
        modelVariant: 'high',
      }),
    )
  })

  it('does not expose or submit an Issue Runtime override', async () => {
    _modelsData.models = ['openai/gpt-4']
    renderDialog()
    expect(screen.queryByTestId('create-issue-runtime')).not.toBeInTheDocument()
    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Issue' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const body = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(body.agentConfig).not.toHaveProperty('runtime')
  })

  it('does not transiently clear the variant when a chip is clicked', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })
    await user.click(await modelTrigger())
    await user.click(await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-high'))

    await user.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toMatchObject({
      model: 'anthropic/claude',
      modelVariant: 'high',
    })
  })

  it('uses the selected profile runtime without clearing the chosen model', async () => {
    _workflowProfilesData.push(
      { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true, agentRuntime: 'opencode' },
      { id: 'team/pi', displayName: 'Pi', description: '', isDefault: false, agentRuntime: 'pi' },
    )
    _modelsData.models = ['anthropic/claude']
    const user = userEvent.setup()
    renderDialog()

    const workflow = (await screen.findByLabelText('Workflow')) as HTMLSelectElement
    await user.click(await modelTrigger())
    await user.click(await waitFor(() => document.querySelector('[data-model-id="anthropic/claude"]') as HTMLElement))
    expect(await modelTrigger()).toHaveTextContent('anthropic/claude')

    fireEvent.change(workflow, { target: { value: 'team/pi' } })

    expect(await modelTrigger()).toHaveTextContent('anthropic/claude')
    const runtimes = modelsHandler.mock.calls.map(([call]) => new URL(call.request.url).searchParams.get('runtime'))
    expect(runtimes).toEqual(expect.arrayContaining(['opencode', 'pi']))
  })

  it('does not render a model selector when the selected profile has no runtime', async () => {
    _workflowProfilesData.push({
      id: 'team/unknown',
      displayName: 'Unknown',
      description: '',
      isDefault: true,
      agentRuntime: null,
    })
    _projectWorkflowProfile.defaultTemplateId = 'team/unknown'
    _modelsData.models = ['vendor/custom-model']
    renderDialog()

    const workflow = (await screen.findByLabelText('Workflow')) as HTMLSelectElement
    await waitFor(() => expect(workflow.value).toBe('team/unknown'))
    expect(screen.queryByRole('button', { name: 'Coder Model' })).not.toBeInTheDocument()
  })

  it('does not include modelVariant when a model body click selects the default variant', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })

    await user.click(await modelTrigger())

    const modelRow = await screen.findByText('claude', { selector: 'span' })
    const rowEl = modelRow.closest('[data-model-id]') as HTMLElement
    expect(rowEl.getAttribute('data-model-id')).toBe('anthropic/claude')
    await user.click(rowEl)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(
      expect.objectContaining({
        model: 'anthropic/claude',
      }),
    )
    expect(callBody).not.toHaveProperty('modelVariant')
  })

  it('highlights the active variant chip on the selected row', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'medium', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    await user.click(await modelTrigger())

    const mediumChip = await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-medium')
    await user.click(mediumChip)

    await user.click(await modelTrigger())

    const active = document.querySelector(
      '[data-testid="create-issue-model-trigger-row-anthropic/claude-variant-medium"][data-variant-active="true"]',
    )
    expect(active).toBeInTheDocument()
    expect(
      document.querySelector(
        '[data-testid="create-issue-model-trigger-row-anthropic/claude-variant-low"][data-variant-active="true"]',
      ),
    ).toBeNull()
  })

  it('uses shared keyboard navigation to select a variant chip', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Keyboard' } })
    await user.click(await modelTrigger())

    const search = await screen.findByPlaceholderText('Search models...')
    fireEvent.keyDown(search, { key: 'ArrowRight' })
    const lowChip = await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-low')
    await waitFor(() => expect(lowChip).toHaveFocus())
    fireEvent.keyDown(lowChip, { key: 'ArrowRight' })
    const highChip = await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-high')
    await waitFor(() => expect(highChip).toHaveFocus())
    fireEvent.keyDown(highChip, { key: 'Enter' })

    await user.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(
      expect.objectContaining({
        model: 'anthropic/claude',
        modelVariant: 'high',
      }),
    )
  })
})
