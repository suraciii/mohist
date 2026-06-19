// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { CreateIssueDialog } from './CreateIssueDialog'

const TEMPLATE_FIXTURES = {
  default: {
    id: 'mohist/default',
    name: 'Mohist Default',
    about: 'Three-voice PRD template',
    isDefault: true,
    suitableFor: ['prd', 'feature', 'refactor'],
    defaults: { labels: { type: 'prd' }, risk: 'high', workflow: 'mohist/default' },
    sections: [
      { title: 'User Voice', guidance: 'What to write in User Voice', placeholder: '<user voice goes here>' },
      { title: 'Product Shape', guidance: 'What to write in Product Shape', placeholder: '<product shape goes here>' },
      { title: 'Domain Model', guidance: 'What to write in Domain Model', placeholder: '<domain model goes here>' },
      { title: 'Acceptance Criteria', guidance: 'What to write in AC', placeholder: '<ac goes here>' },
      { title: 'Non-Goals', guidance: 'What to write in Non-Goals', placeholder: '<non-goals go here>' },
    ],
    source: 'builtin' as const,
  },
  custom: {
    id: 'team/bug-report',
    name: 'Bug Report',
    about: 'Minimal bug report template',
    isDefault: false,
    suitableFor: ['bug'],
    defaults: { labels: null, risk: null, workflow: null },
    sections: [
      { title: 'Summary', guidance: 'One-paragraph summary guidance', placeholder: '<one-paragraph summary>' },
      { title: 'Repro', guidance: 'Steps to reproduce guidance', placeholder: '<steps to reproduce>' },
    ],
    source: 'custom' as const,
  },
}

const mocks = vi.hoisted(() => ({
  createIssue: vi.fn(),
  useLabels: vi.fn(() => ({ data: [] })),
  useProject: vi.fn(() => ({ projectId: 'proj_create', projects: [{ id: 'proj_create', name: 'Project' }] })),
  useRepositories: vi.fn(() => ({ data: [{ name: 'main', isDefault: true }] })),
  useAvailableModelIds: vi.fn(() => ({ data: [] })),
  useWorkflowProfiles: vi.fn<() => { data: unknown[] }>(() => ({ data: [] })),
  useIssueTemplates: vi.fn<() => { data: unknown[]; isLoading: boolean }>(() => ({ data: [], isLoading: false })),
  useIssueTemplate: vi.fn<(id: string | null) => { data: unknown }>(() => ({ data: undefined })),
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
    expect(labels.some((label) => label?.includes('Mohist Default'))).toBe(true)
    expect(labels.some((label) => label?.includes('Bug Report'))).toBe(true)
    expect(options.find((opt) => opt.getAttribute('value') === 'mohist/default')).toBeDefined()
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

  it('prefills the body with a section skeleton in section order using each placeholder', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    fireEvent.change(selector, { target: { value: 'mohist/default' } })

    const description = await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement

    await waitFor(() => {
      expect(description.value).toBe([
        '## User Voice',
        '<user voice goes here>',
        '',
        '## Product Shape',
        '<product shape goes here>',
        '',
        '## Domain Model',
        '<domain model goes here>',
        '',
        '## Acceptance Criteria',
        '<ac goes here>',
        '',
        '## Non-Goals',
        '<non-goals go here>',
      ].join('\n'))
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

  it('does not include any section guidance in the prefilled body', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    fireEvent.change(selector, { target: { value: 'mohist/default' } })

    const description = await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement

    await waitFor(() => {
      expect(description.value.length).toBeGreaterThan(0)
    })

    for (const section of TEMPLATE_FIXTURES.default.sections) {
      expect(description.value).not.toContain(section.guidance)
    }
  })

  it('preserves the section order from a custom template (smaller section list)', async () => {
    setupTemplates()

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    fireEvent.change(selector, { target: { value: 'team/bug-report' } })

    const description = await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement

    await waitFor(() => {
      expect(description.value).toBe([
        '## Summary',
        '<one-paragraph summary>',
        '',
        '## Repro',
        '<steps to reproduce>',
      ].join('\n'))
    })

    for (const section of TEMPLATE_FIXTURES.custom.sections) {
      expect(description.value).not.toContain(section.guidance)
    }
  })

  it('does not apply advisory defaults from the selected template', async () => {
    setupTemplates()
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })

    renderDialog()

    const selector = await screen.findByTestId('issue-template-selector')
    fireEvent.change(selector, { target: { value: 'mohist/default' } })
    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Templated issue' } })

    await waitFor(() => expect((screen.getByPlaceholderText('Optional description') as HTMLTextAreaElement).value).toContain('## User Voice'))

    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.not.objectContaining({
      risk: 'high',
      workflowProfileId: 'mohist/default',
      labels: { type: 'prd' },
    }))
  })
})
