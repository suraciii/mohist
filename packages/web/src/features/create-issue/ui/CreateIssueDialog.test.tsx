// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

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
  useLabels: vi.fn(() => ({ data: [] })),
  useProject: vi.fn(() => ({ projectId: 'proj_create', projects: [{ id: 'proj_create', name: 'Project' }] })),
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
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    warning: vi.fn(),
    info: vi.fn(),
  },
}))

vi.mock('sonner', () => ({
  toast: mocks.toast,
}))

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  createIssue: mocks.createIssue,
  useLabels: mocks.useLabels,
}))

vi.mock('../../../entities/project', () => ({
  useProject: mocks.useProject,
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
      <CreateIssueDialog open onClose={vi.fn()} />
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
    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledWith('Issue #223 created'))
    expect(mocks.toast.success.mock.calls[0][0]).toBe('Issue #223 created')
    expect(mocks.toast.success.mock.calls[0][0]).not.toMatch(/undefined/)
    expect(mocks.toast.error).not.toHaveBeenCalled()
  })

  it('never reads the number from a { issue } wrapper (success path uses data.number)', async () => {
    // Bare Issue response (matches `createIssue` shape in entities/issue/api/client.ts).
    // If the dialog ever regressed to reading `data.issue.number`, this would render
    // `Issue #undefined created` because `data.issue` does not exist on the bare response.
    mocks.createIssue.mockResolvedValue({ id: 'issue_9', number: 9 } as never)
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No wrapper' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.toast.success).toHaveBeenCalledTimes(1))
    const message = mocks.toast.success.mock.calls[0][0] as string
    expect(message).toBe('Issue #9 created')
    expect(message).not.toMatch(/undefined/)
  })

  it('shows an error toast without any issue number when the create fails', async () => {
    mocks.createIssue.mockRejectedValue(new Error('Server unavailable'))
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Boom' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith('Server unavailable'))
    expect(mocks.toast.success).not.toHaveBeenCalled()
    const errorMessage = mocks.toast.error.mock.calls[0][0] as string
    expect(errorMessage).toBe('Server unavailable')
    expect(errorMessage).not.toMatch(/undefined/)
    expect(errorMessage).not.toMatch(/#\d+/)
  })

  it('falls back to a generic error toast without a number when the failure has no message', async () => {
    mocks.createIssue.mockRejectedValue(new Error(''))
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Empty err' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.toast.error).toHaveBeenCalledWith('Failed to create issue'))
    const errorMessage = mocks.toast.error.mock.calls[0][0] as string
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
