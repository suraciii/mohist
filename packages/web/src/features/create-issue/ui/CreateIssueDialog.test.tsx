// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { toast } from 'sonner'

import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { ProjectProvider } from '../../../entities/project'
import { CreateIssueDialog } from './CreateIssueDialog'

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

const mocks = vi.hoisted(() => ({
  createIssue: vi.fn(),
  useRepositories: vi.fn(() => ({ data: [{ name: 'main', isDefault: true }] })),
  useAvailableModelIds: vi.fn<() => { data: { models: string[]; modelVariants: Record<string, string[]> } | undefined; isLoading: boolean; error: unknown }>(() => ({ data: { models: [], modelVariants: {} }, isLoading: false, error: null })),
  useWorkflowProfiles: vi.fn<() => { data: unknown[] }>(() => ({ data: [] })),
  useEffectiveDefaultWorkflowProfile: vi.fn<() => { effectiveTemplateId: string; source: 'project' | 'system' | 'none'; configuredTemplateId: string | null }>(() => ({
    effectiveTemplateId: 'mohist/local',
    source: 'system',
    configuredTemplateId: null,
  })),
  useIssueTemplates: vi.fn<() => { data: unknown[]; isLoading: boolean }>(() => ({ data: [], isLoading: false })),
  useIssueTemplate: vi.fn<(id: string | null) => { data: unknown }>(() => ({ data: undefined })),
  useIssues: vi.fn((_params?: { stage?: string; label?: string; projectId?: string }) => ({ data: [] as Issue[] | undefined, isLoading: false })),
}))

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  createIssue: mocks.createIssue,
  useIssues: mocks.useIssues,
}))

vi.mock('../../../entities/project', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/project')>()),
  useRepositories: mocks.useRepositories,
}))

vi.mock('../../../entities/settings', () => ({
  useAvailableModelIds: mocks.useAvailableModelIds,
  useEffectiveDefaultWorkflowProfile: mocks.useEffectiveDefaultWorkflowProfile,
  useWorkflowProfiles: mocks.useWorkflowProfiles,
}))

vi.mock('../../../entities/issue-templates', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue-templates')>()),
  useIssueTemplates: mocks.useIssueTemplates,
  useIssueTemplate: mocks.useIssueTemplate,
}))

function setupTemplates(defaultTemplate: typeof TEMPLATE_FIXTURES.default = TEMPLATE_FIXTURES.default, customTemplate: typeof TEMPLATE_FIXTURES.custom | null = TEMPLATE_FIXTURES.custom) {
  const list = [defaultTemplate, ...(customTemplate ? [customTemplate] : [])]
  mocks.useIssueTemplates.mockReturnValue({ data: list, isLoading: false })
  mocks.useIssueTemplate.mockImplementation((id: string | null) => {
    if (!id) return { data: undefined }
    const found = list.find((t) => t.id === id)
    return { data: found }
  })
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
  })

  it('creates issue with attachment ids from the composer body', async () => {
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
    const { queryClient } = renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'New issue' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: 'See ![screen](att:att_created)' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      title: 'New issue',
      body: 'See ![screen](att:att_created)',
      attachmentIds: ['att_created'],
      projectId: 'proj_create',
    }))

    queryClient.clear()
  })

  it('does not serialize inherited default workflow as an explicit create selection', async () => {
    mocks.useEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: 'mohist/local',
    })
    mocks.useWorkflowProfiles.mockReturnValue({
      data: [{ id: 'mohist/github-pr', displayName: 'GitHub PR', description: '', isDefault: false }],
    })
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Disabled default fallback' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      workflowProfileId: 'mohist/local',
    }))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      workflowProfileId: 'mohist/github-pr',
    }))
  })
})

describe('CreateIssueDialog toast feedback', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('shows a success toast with the new issue number on successful create', async () => {
    mocks.createIssue.mockResolvedValue({ id: 'issue_223', number: 223 } as never)
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Toast test' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('Issue #223 created'))
    expect(vi.mocked(toast.success).mock.calls[0][0]).toBe('Issue #223 created')
    expect(vi.mocked(toast.success).mock.calls[0][0]).not.toMatch(/undefined/)
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('never reads the number from a { issue } wrapper (success path uses data.number)', async () => {
    // Bare Issue response (matches `createIssue` shape in entities/issue/api/client.ts).
    // If the dialog ever regressed to reading `data.issue.number`, this would render
    // `Issue #undefined created` because `data.issue` does not exist on the bare response.
    mocks.createIssue.mockResolvedValue({ id: 'issue_9', number: 9 } as never)
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No wrapper' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(toast.success).toHaveBeenCalledTimes(1))
    const message = vi.mocked(toast.success).mock.calls[0][0] as string
    expect(message).toBe('Issue #9 created')
    expect(message).not.toMatch(/undefined/)
  })

  it('shows an error toast without any issue number when the create fails', async () => {
    mocks.createIssue.mockRejectedValue(new Error('Server unavailable'))
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
    mocks.createIssue.mockRejectedValue(new Error(''))
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
  })

  it('populates the selector with available templates (non-disabled default + customs)', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    const options = Array.from(selector.querySelectorAll('option'))

    const labels = options.map((opt) => opt.textContent)
    expect(labels.some((label) => label?.includes('Feature'))).toBe(true)
    expect(labels.some((label) => label?.includes('Bug Report'))).toBe(true)
    expect(options.find((opt) => opt.getAttribute('value') === 'feature')).toBeDefined()
    expect(options.find((opt) => opt.getAttribute('value') === 'team/bug-report')).toBeDefined()
  })

  it('shows a loading option and disabled selector while templates are loading', () => {
    mocks.useIssueTemplates.mockReturnValue({ data: [], isLoading: true })

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
    fireEvent.change(selector, { target: { value: 'team/bug-report' } })

    const description = await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement

    await waitFor(() => {
      expect(description.value).toBe(TEMPLATE_FIXTURES.custom.body)
    })
  })

  it('does not apply advisory defaults from the selected template', async () => {
    setupTemplates()
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    fireEvent.change(selector, { target: { value: 'feature' } })
    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated issue' } })

    await waitFor(() => expect((screen.getByPlaceholderText('Optional description') as HTMLTextAreaElement).value).toContain('## User Voice'))

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      risk: expect.anything(),
      workflowProfileId: 'mohist/local',
      labels: expect.anything(),
    }))
  })
})

describe('CreateIssueDialog model + variant chips', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  function modelTrigger() {
    const trigger = document.getElementById('create-issue-model-trigger')
    if (!trigger) throw new Error('model trigger not found')
    return trigger
  }

  it('does not render a standalone variant picker anywhere', () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: { models: ['anthropic/claude'], modelVariants: { 'anthropic/claude': ['low', 'high'] } },
      isLoading: false,
      error: null,
    })
    renderDialog()
    expect(screen.queryByTestId('create-issue-model-variant-variant-trigger')).not.toBeInTheDocument()
  })

  it('renders inline variant chips on a variant-capable model row', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude', 'openai/gpt-4'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
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
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })

    await user.click(modelTrigger())

    const highChip = await screen.findByTestId(
      'create-issue-model-trigger-row-anthropic/claude-variant-high',
    )
    await user.click(highChip)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      model: 'anthropic/claude',
      modelVariant: 'high',
    }))
  })

  it('does not transiently clear the variant when a chip is clicked', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })
    await user.click(modelTrigger())
    await user.click(await screen.findByTestId('create-issue-model-trigger-row-anthropic/claude-variant-high'))

    await user.click(screen.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue.mock.calls[0][0]).toMatchObject({
      model: 'anthropic/claude',
      modelVariant: 'high',
    })
  })

  it('does not include modelVariant when a model body click selects the default variant', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated' } })

    await user.click(modelTrigger())

    const modelRow = await screen.findByText('claude', { selector: 'span' })
    const rowEl = modelRow.closest('[data-model-id]') as HTMLElement
    expect(rowEl.getAttribute('data-model-id')).toBe('anthropic/claude')
    await user.click(rowEl)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      model: 'anthropic/claude',
    }))
    expect(mocks.createIssue.mock.calls[0][0]).not.toHaveProperty('modelVariant')
  })

  it('highlights the active variant chip on the selected row', async () => {
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'medium', 'high'] },
      },
      isLoading: false,
      error: null,
    })
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
    mocks.useAvailableModelIds.mockReturnValue({
      data: {
        models: ['anthropic/claude'],
        modelVariants: { 'anthropic/claude': ['low', 'high'] },
      },
      isLoading: false,
      error: null,
    })
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
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
    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      model: 'anthropic/claude',
      modelVariant: 'high',
    })))
  })
})

describe('CreateIssueDialog workflow profile default', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  function setupProfiles() {
    mocks.useWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })
  }

  it('shows the project-configured default workflow profile but does not send it as an explicit selection', async () => {
    setupProfiles()
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
    mocks.useEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'project',
      configuredTemplateId: 'mohist/github-pr',
    })

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Project default issue' } })
    const select = await screen.findByLabelText('Workflow') as HTMLSelectElement
    expect(select.value).toBe('mohist/github-pr')

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      workflowProfileId: 'mohist/github-pr',
    }))
  })

  it('falls back to the system default workflow profile when the project default is unset', async () => {
    setupProfiles()
    mocks.useEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/local',
      source: 'system',
      configuredTemplateId: null,
    })

    renderDialog()

    const select = await screen.findByLabelText('Workflow') as HTMLSelectElement
    expect(select.value).toBe('mohist/local')
  })

  it('does not prefill or submit a frontmatter recommendation that is not enabled', async () => {
    mocks.useWorkflowProfiles.mockReturnValue({
      data: [{ id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false }],
    })
    mocks.useEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'system',
      configuredTemplateId: null,
    })
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })

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
    expect(select.value).toBe('mohist/github-pr')
    expect([...select.options].map((option) => option.value)).toEqual(['mohist/github-pr'])

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      workflowProfileId: 'mohist/local',
    }))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      workflowProfileId: 'mohist/github-pr',
    }))
  })
})

describe('CreateIssueDialog prerequisites', () => {
  const PICKER_PROJECT_ISSUES: Issue[] = [
    {
      id: 'issue_5',
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
      id: 'issue_7',
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
      id: 'issue_99',
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
    mocks.useIssues.mockImplementation((params: { projectId?: string } | undefined) => ({
      data: params?.projectId === 'proj_create' ? issues : [],
      isLoading: false,
    }))
  }

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders the Prerequisites picker in buffer mode and sends the selected numbers on submit', async () => {
    setupPickerIssues()
    mocks.createIssue.mockResolvedValue({ id: 'issue_new', number: 42 } as never)
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

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Plan with deps',
      prerequisiteNumbers: [5, 7],
    }))
  })

  it('removes a buffered chip from the local selection without sending the removed number', async () => {
    setupPickerIssues()
    mocks.createIssue.mockResolvedValue({ id: 'issue_new', number: 42 } as never)
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Trim deps' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    let options = await screen.findAllByTestId('prerequisite-picker-option')
    let opt5 = options.find((opt) => opt.getAttribute('data-issue-number') === '5')
    let opt7 = options.find((opt) => opt.getAttribute('data-issue-number') === '7')
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

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      prerequisiteNumbers: [7],
    }))

    // after submit, the picker re-opened from scratch (dialog was reset); confirm chip state was cleared.
    cleanup()
    mocks.createIssue.mockClear()
    mocks.useIssues.mockClear()
    setupPickerIssues()
    renderDialog()
    expect(screen.queryByTestId('prerequisite-picker-chip')).not.toBeInTheDocument()
  })

  it('omits prerequisiteNumbers from the create body when no prerequisites are selected', async () => {
    setupPickerIssues()
    mocks.createIssue.mockResolvedValue({ id: 'issue_new', number: 42 } as never)
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No deps' } })

    expect(screen.queryByTestId('prerequisite-picker-chip')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    const payload = mocks.createIssue.mock.calls[0][0] as Record<string, unknown>
    expect(payload).not.toHaveProperty('prerequisiteNumbers')
    expect(payload.title).toBe('No deps')
  })

  it('clears the prerequisite buffer after a successful create so reopening starts empty', async () => {
    setupPickerIssues()
    mocks.createIssue.mockResolvedValue({ id: 'issue_new', number: 42 } as never)
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

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
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
    mocks.createIssue.mockResolvedValue({ id: 'issue_201', number: 201 } as never)
    const user = userEvent.setup()
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Success path' } })

    await user.click(screen.getByTestId('prerequisite-picker-trigger'))
    const options = await screen.findAllByTestId('prerequisite-picker-option')
    const opt7 = options.find((opt) => opt.getAttribute('data-issue-number') === '7')
    await user.click(opt7!)

    await user.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
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
