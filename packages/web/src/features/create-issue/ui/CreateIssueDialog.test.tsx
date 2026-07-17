import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'
import { toast } from 'sonner'

import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { ProjectProvider } from '../../../entities/project'
import { CreateIssueDialog } from './CreateIssueDialog'
import { useMswServer } from '../../../../tests/support/msw'

const TEMPLATE_FIXTURES = {
  default: {
    id: 'feature',
    name: 'Feature',
    description: 'Three-voice PRD template',
    body: [
      '## User Voice',
      '<!-- first-person user need -->',
      '<user voice goes here>',
      '',
      '## Product Shape',
      '<!-- product boundary -->',
      '<product shape goes here>',
      '',
      '## Domain Model',
      '<!-- optional domain context -->',
      '<domain model goes here>',
      '',
      '## Acceptance Criteria',
      '<ac goes here>',
      '',
      '## Non-Goals',
      '<non-goals go here>',
    ].join('\n'),
    source: 'builtin' as const,
  },
  custom: {
    id: 'team/bug-report',
    name: 'Bug Report',
    description: 'Minimal bug report template',
    body: [
      '## Summary',
      '<!-- one-paragraph summary -->',
      '<one-paragraph summary>',
      '',
      '## Repro',
      '<!-- steps to reproduce -->',
      '<steps to reproduce>',
    ].join('\n'),
    source: 'custom' as const,
  },
}

let _issuesData: Issue[] = []
let _createIssueResponse: Pick<Issue, 'number'> = { number: 42 }
const _repositoriesData = [{ name: 'main', isDefault: true }]
const _modelsData = { models: [] as string[], modelVariants: {} as Record<string, string[]> }
const _workflowProfilesData: { id: string; displayName: string; description: string; isDefault: boolean }[] = []
const _projectWorkflowProfile: { projectId: string; defaultTemplateId: string | null; disabledWorkflowProfileIds: string[] } = { projectId: 'proj_create', defaultTemplateId: null, disabledWorkflowProfileIds: [] }
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

const repositoriesHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: _repositoriesData }),
)

const modelsHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: _modelsData }),
)

const workflowProfilesHandler = vi.fn(() =>
  HttpResponse.json({
    success: true,
    data: _workflowProfilesData.map((t) => ({
      id: t.id,
      name: t.displayName,
      description: t.description,
      isDefault: t.isDefault,
    })),
  }),
)

const projectWorkflowProfileHandler = vi.fn(() =>
  HttpResponse.json({ success: true, data: _projectWorkflowProfile }),
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
  http.get('*/api/projects/:projectId/issues', issuesHandler),
  http.get('*/api/projects/:projectId/repositories', repositoriesHandler),
  http.get('*/api/projects/:projectId/opencode/models', modelsHandler),
  http.get('*/api/workflow-templates/system', workflowProfilesHandler),
  http.get('*/api/projects/:projectId/workflow-profile', projectWorkflowProfileHandler),
  http.get('*/api/issue-templates*', issueTemplatesHandler),
)

function setupTemplates(defaultTemplate: typeof TEMPLATE_FIXTURES.default = TEMPLATE_FIXTURES.default, customTemplate: typeof TEMPLATE_FIXTURES.custom | null = TEMPLATE_FIXTURES.custom) {
  _issueTemplatesData.length = 0
  _issueTemplatesData.push(defaultTemplate)
  if (customTemplate) _issueTemplatesData.push(customTemplate)
}

function renderDialog() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj_create" initialProjects={[{ id: 'proj_create', name: 'Project', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
        <CreateIssueDialog open onClose={vi.fn()} />
      </ProjectProvider>
    </QueryClientProvider>,
  )
  return { queryClient, ...view }
}

describe('CreateIssueDialog', () => {
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

  it('creates issue with attachment ids from the composer body', async () => {
    _createIssueResponse = { number: 1 }
    const { queryClient } = renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'New issue' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: 'See ![screen](att:att_created)' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({
      title: 'New issue',
      body: 'See ![screen](att:att_created)',
      attachmentIds: ['att_created'],
    }))

    queryClient.clear()
  })

  it('does not serialize inherited default workflow as an explicit create selection', async () => {
    _projectWorkflowProfile.defaultTemplateId = 'mohist/local'
    _workflowProfilesData.push({ id: 'mohist/github-pr', displayName: 'GitHub PR', description: '', isDefault: false })
    _workflowProfilesData.push({ id: 'mohist/local', displayName: 'Default', description: '', isDefault: true })

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Disabled default fallback' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).not.toHaveProperty('workflowProfileId')
  })
})

describe('CreateIssueDialog toast feedback', () => {
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

  it('shows a success toast with the new issue number on successful create', async () => {
    _createIssueResponse = { number: 223 }
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Toast test' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Issue #223 created'))
    expect(vi.mocked(toast.success).mock.calls[0][0]).toBe('Issue #223 created')
    expect(vi.mocked(toast.success).mock.calls[0][0]).not.toMatch(/undefined/)
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('never reads the number from a { issue } wrapper (success path uses data.number)', async () => {
    _createIssueResponse = { number: 9 }
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No wrapper' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledTimes(1))
    const message = vi.mocked(toast.success).mock.calls[0][0] as string
    expect(message).toBe('Issue #9 created')
    expect(message).not.toMatch(/undefined/)
  })

  it('shows an error toast without any issue number when the create fails', async () => {
    createIssueHandler.mockImplementationOnce(async () =>
      HttpResponse.json({ success: false, error: 'Server unavailable' }, { status: 500 }),
    )
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Boom' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Server unavailable'))
    expect(toast.success).not.toHaveBeenCalled()
    const errorMessage = vi.mocked(toast.error).mock.calls[0][0] as string
    expect(errorMessage).toBe('Server unavailable')
    expect(errorMessage).not.toMatch(/undefined/)
    expect(errorMessage).not.toMatch(/#\d+/)
  })

  it('falls back to a generic error toast without a number when the failure has no message', async () => {
    createIssueHandler.mockImplementationOnce(async () =>
      HttpResponse.json({ success: false, error: '' }, { status: 500 }),
    )
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Empty err' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Failed to create issue'))
    const errorMessage = vi.mocked(toast.error).mock.calls[0][0] as string
    expect(errorMessage).toBe('Failed to create issue')
    expect(errorMessage).not.toMatch(/undefined/)
    expect(errorMessage).not.toMatch(/#\d+/)
  })
})

describe('CreateIssueDialog template selector', () => {
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

  it('populates the selector with available templates (non-disabled default + customs)', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    await waitFor(() => {
      expect(selector.querySelector('option[value="feature"]')).toBeInTheDocument()
    })
    const options = Array.from(selector.querySelectorAll('option'))

    const labels = options.map((opt) => opt.textContent)
    expect(labels.some((label) => label?.includes('Feature'))).toBe(true)
    expect(labels.some((label) => label?.includes('Bug Report'))).toBe(true)
    expect(options.find((opt) => opt.getAttribute('value') === 'feature')).toBeDefined()
    expect(options.find((opt) => opt.getAttribute('value') === 'team/bug-report')).toBeDefined()
  })

  it('shows a loading option and disabled selector while templates are loading', async () => {
    renderDialog()

    const selector = screen.getByTestId('issue-template-selector') as HTMLSelectElement
    expect(selector.disabled).toBe(true)
    const options = Array.from(selector.querySelectorAll('option'))
    expect(options[0]?.textContent).toBe('Loading templates…')
  })

  it('prefills the body with the template body verbatim', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    await waitFor(() => {
      expect(selector.querySelector('option[value="feature"]')).toBeInTheDocument()
    })
    fireEvent.change(selector, { target: { value: 'feature' } })

    const description = await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement

    await waitFor(() => {
      expect(description.value).toBe(TEMPLATE_FIXTURES.default.body)
    })

    const descriptionValue = description.value
    const userVoiceIdx = descriptionValue.indexOf('## User Voice')
    const productShapeIdx = descriptionValue.indexOf('## Product Shape')
    const domainModelIdx = descriptionValue.indexOf('## Domain Model')
    const acIdx = descriptionValue.indexOf('## Acceptance Criteria')
    const nonGoalsIdx = descriptionValue.indexOf('## Non-Goals')
    expect(userVoiceIdx).toBeGreaterThanOrEqual(0)
    expect(productShapeIdx).toBeGreaterThan(userVoiceIdx)
    expect(domainModelIdx).toBeGreaterThan(productShapeIdx)
    expect(acIdx).toBeGreaterThan(domainModelIdx)
    expect(nonGoalsIdx).toBeGreaterThan(acIdx)
  })

  it('preserves the custom template body verbatim', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    await waitFor(() => {
      expect(selector.querySelector('option[value="team/bug-report"]')).toBeInTheDocument()
    })
    fireEvent.change(selector, { target: { value: 'team/bug-report' } })

    const description = await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement

    await waitFor(() => {
      expect(description.value).toBe(TEMPLATE_FIXTURES.custom.body)
    })
  })

  it('does not apply advisory defaults from the selected template', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    await waitFor(() => {
      expect(selector.querySelector('option[value="feature"]')).toBeInTheDocument()
    })
    fireEvent.change(selector, { target: { value: 'feature' } })
    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated issue' } })

    await waitFor(() => expect((screen.getByPlaceholderText('Optional description') as HTMLTextAreaElement).value).toContain('## User Voice'))

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).not.toHaveProperty('risk')
    expect(callBody).not.toHaveProperty('workflowProfileId')
    expect(callBody).not.toHaveProperty('labels')
  })
})

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

  function modelTrigger() {
    const trigger = document.getElementById('create-issue-model-trigger')
    if (!trigger) throw new Error('model trigger not found')
    return trigger
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

    await user.click(modelTrigger())

    for (const variant of ['low', 'medium', 'high']) {
      const chip = document.querySelector(
        `[data-testid="create-issue-model-trigger-row-anthropic/claude-variant-${variant}"]`,
      )
      expect(chip).toBeInTheDocument()
    }
    expect(
      document.querySelector(`[data-testid="create-issue-model-trigger-row-openai/gpt-4-variant-low"]`),
    ).toBeNull()
  })

  it('sends modelVariant alongside model on create when a chip is clicked', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })

    await user.click(modelTrigger())

    const highChip = await screen.findByTestId(
      'create-issue-model-trigger-row-anthropic/claude-variant-high',
    )
    await user.click(highChip)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({
      model: 'anthropic/claude',
      modelVariant: 'high',
    }))
  })

  it('does not transiently clear the variant when a chip is clicked', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })
    await user.click(modelTrigger())
    await user.click(await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-high'))

    await user.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toMatchObject({
      model: 'anthropic/claude',
      modelVariant: 'high',
    })
  })

  it('does not include modelVariant when a model body click selects the default variant', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })

    await user.click(modelTrigger())

    const modelRow = await screen.findByText('claude', { selector: 'span' })
    const rowEl = modelRow.closest('[data-model-id]') as HTMLElement
    expect(rowEl.getAttribute('data-model-id')).toBe('anthropic/claude')
    await user.click(rowEl)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({
      model: 'anthropic/claude',
    }))
    expect(callBody).not.toHaveProperty('modelVariant')
  })

  it('highlights the active variant chip on the selected row', async () => {
    _modelsData.models = ['anthropic/claude']
    _modelsData.modelVariants = { 'anthropic/claude': ['low', 'medium', 'high'] }
    const user = userEvent.setup()
    renderDialog()

    await user.click(modelTrigger())

    const mediumChip = await screen.findByTestId(
      'create-issue-model-trigger-row-anthropic/claude-variant-medium',
    )
    await user.click(mediumChip)

    await user.click(modelTrigger())

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
    await user.click(modelTrigger())

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
    expect(callBody).toEqual(expect.objectContaining({
      model: 'anthropic/claude',
      modelVariant: 'high',
    }))
  })
})

describe('CreateIssueDialog workflow profile default', () => {
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

  function setupProfiles() {
    _workflowProfilesData.length = 0
    _workflowProfilesData.push(
      { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    )
  }

  it('shows the project-configured default workflow profile but does not send it as an explicit selection', async () => {
    setupProfiles()
    _projectWorkflowProfile.defaultTemplateId = 'mohist/github-pr'

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Project default issue' } })
    const select = await screen.findByLabelText('Workflow') as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('mohist/github-pr'))

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).not.toHaveProperty('workflowProfileId')
  })

  it('falls back to the system default workflow profile when the project default is unset', async () => {
    setupProfiles()

    renderDialog()

    const select = await screen.findByLabelText('Workflow') as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('mohist/local'))
  })

  it('does not prefill or submit a frontmatter recommendation that is not enabled', async () => {
    _workflowProfilesData.push({ id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false })
    _projectWorkflowProfile.defaultTemplateId = null

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Stale recommendation' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), {
      target: {
        value: [
          '---',
          'recommended_workflow: mohist/local',
          '---',
          '',
          'Body',
        ].join('\n'),
      },
    })

    expect(await screen.findByTestId('recommended-workflow')).toHaveTextContent('mohist/local')
    expect(screen.getByTestId('workflow-recommendation-unavailable')).toHaveTextContent('not enabled')
    const select = screen.getByLabelText('Workflow') as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('mohist/github-pr'))
    expect([...select.options].map((option) => option.value)).toEqual(['mohist/github-pr'])

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).not.toHaveProperty('workflowProfileId')
  })
})

describe('CreateIssueDialog prerequisites', () => {
  const PICKER_PROJECT_ISSUES: Issue[] = [
    {
      number: 5,
      title: 'Wire up auth',
      status: IssueStatus.InProgress,
      health: IssueHealth.Active,
      projectId: 'proj_create',
      labels: {},
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      isDraft: false,
      canStart: true,
      blocker: null,
    },
    {
      number: 7,
      title: 'Audit auth tokens',
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      projectId: 'proj_create',
      labels: {},
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      isDraft: false,
      canStart: true,
      blocker: null,
    },
    {
      number: 99,
      title: 'Other issue',
      status: IssueStatus.Backlog,
      health: IssueHealth.Active,
      projectId: 'proj_create',
      labels: {},
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      isDraft: false,
      canStart: true,
      blocker: null,
    },
  ]

  function setupPickerIssues(issues: Issue[] = PICKER_PROJECT_ISSUES) {
    _issuesData = issues
  }

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

  it('renders the Prerequisites picker in buffer mode and sends the selected numbers on submit', async () => {
    setupPickerIssues()
    _createIssueResponse = { number: 42 }
    const user = userEvent.setup()
    renderDialog()

    const picker = await screen.findByTestId('issue-prerequisite-picker')
    expect(picker).toBeInTheDocument()
    expect(within(picker).getByTestId('prerequisite-picker-trigger')).toBeInTheDocument()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Plan with deps' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    const options = await screen.findAllByTestId('prerequisite-picker-option')
    const opt5 = options.find((opt) => opt.getAttribute('data-issue-number') === '5')
    const opt7 = options.find((opt) => opt.getAttribute('data-issue-number') === '7')
    expect(opt5).toBeDefined()
    expect(opt7).toBeDefined()
    await user.click(opt5!)
    await user.click(opt7!)

    const chips = await screen.findAllByTestId('prerequisite-picker-chip')
    expect(chips.map((c) => c.getAttribute('data-issue-number'))).toEqual(['5', '7'])
    expect(within(picker).getByTestId('prerequisite-picker-chips')).toHaveAttribute('data-mode', 'buffer')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({
      title: 'Plan with deps',
      prerequisiteNumbers: [5, 7],
    }))
  })

  it('removes a buffered chip from the local selection without sending the removed number', async () => {
    setupPickerIssues()
    _createIssueResponse = { number: 42 }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Trim deps' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    const options = await screen.findAllByTestId('prerequisite-picker-option')
    const opt5 = options.find((opt) => opt.getAttribute('data-issue-number') === '5')
    const opt7 = options.find((opt) => opt.getAttribute('data-issue-number') === '7')
    await user.click(opt5!)
    await user.click(opt7!)

    let chips = screen.getAllByTestId('prerequisite-picker-chip')
    expect(chips).toHaveLength(2)

    const chip5 = chips.find((chip) => chip.getAttribute('data-issue-number') === '5')
    await user.click(within(chip5!).getByTestId('prerequisite-picker-chip-remove'))

    chips = screen.getAllByTestId('prerequisite-picker-chip')
    expect(chips).toHaveLength(1)
    expect(chips[0]).toHaveAttribute('data-issue-number', '7')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({
      prerequisiteNumbers: [7],
    }))

    // after submit, the picker re-opened from scratch (dialog was reset); confirm chip state was cleared.
    cleanup()
    vi.clearAllMocks()
    setupPickerIssues()
    renderDialog()
    expect(screen.queryByTestId('prerequisite-picker-chip')).not.toBeInTheDocument()
  })

  it('omits prerequisiteNumbers from the create body when no prerequisites are selected', async () => {
    setupPickerIssues()
    _createIssueResponse = { number: 42 }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No deps' } })

    expect(screen.queryByTestId('prerequisite-picker-chip')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).not.toHaveProperty('prerequisiteNumbers')
    expect(callBody.title).toBe('No deps')
  })

  it('clears the prerequisite buffer after a successful create so reopening starts empty', async () => {
    setupPickerIssues()
    _createIssueResponse = { number: 42 }
    const onClose = vi.fn()
    const user = userEvent.setup()
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })

    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj_create" initialProjects={[{ id: 'proj_create', name: 'Project', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
          <CreateIssueDialog open onClose={onClose} />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Reset deps' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    const options = await screen.findAllByTestId('prerequisite-picker-option')
    const opt5 = options.find((opt) => opt.getAttribute('data-issue-number') === '5')
    await user.click(opt5!)

    expect(screen.getByTestId('prerequisite-picker-chip')).toHaveAttribute('data-issue-number', '5')

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    const callBody = await createIssueHandler.mock.calls[0][0].request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({
      prerequisiteNumbers: [5],
    }))
    expect(onClose).toHaveBeenCalledTimes(1)

    rerender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj_create" initialProjects={[{ id: 'proj_create', name: 'Project', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
          <CreateIssueDialog open={false} onClose={onClose} />
        </ProjectProvider>
      </QueryClientProvider>,
    )
    rerender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="proj_create" initialProjects={[{ id: 'proj_create', name: 'Project', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
          <CreateIssueDialog open onClose={onClose} />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(screen.queryByTestId('prerequisite-picker-chip')).not.toBeInTheDocument()

    queryClient.clear()
  })

  it('create-issue mutation onSuccess still reads data.number off the bare Issue shape', async () => {
    setupPickerIssues()
    _createIssueResponse = { number: 201 }
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Success path' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    const options = await screen.findAllByTestId('prerequisite-picker-option')
    const opt7 = options.find((opt) => opt.getAttribute('data-issue-number') === '7')
    await user.click(opt7!)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(createIssueHandler).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Issue #201 created'))
    expect(vi.mocked(toast.success).mock.calls[0][0]).toBe('Issue #201 created')
  })
})

function within(el: HTMLElement) {
  return {
    getByTestId: (testId: string) => {
      const found = el.querySelector(`[data-testid="${testId}"]`)
      if (!found) throw new Error(`[data-testid="${testId}"] not found within ${el.outerHTML}`)
      return found as HTMLElement
    },
    queryByTestId: (testId: string) => el.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null,
  }
}
