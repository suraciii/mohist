import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render as baseRender, screen, fireEvent, waitFor } from './test-utils'
import { CreateIssueDialog } from '../src/features/create-issue'

vi.mock('../src/entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/issue')>()
  return {
    ...actual,
    createIssue: vi.fn(),
    useLabels: vi.fn(),
  }
})

vi.mock('../src/entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/settings')>()
  return {
    ...actual,
    useWorkflowProfiles: vi.fn(),
    useAvailableModelIds: vi.fn(),
  }
})

vi.mock('../src/entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/project')>()
  return {
    ...actual,
    useRepositories: vi.fn(),
  }
})

const { createIssue, useLabels } = await import('../src/entities/issue')
const { useWorkflowProfiles, useAvailableModelIds } = await import('../src/entities/settings')
const { useRepositories } = await import('../src/entities/project')

const PROFILES = [
  { id: 'mohist/default', displayName: 'Default', description: 'Default profile', isDefault: true },
  { id: 'feature-flow', displayName: 'Feature Flow', description: 'Feature work', isDefault: false },
]

function mockHooks() {
  ;(useRepositories as ReturnType<typeof vi.fn>).mockReturnValue({ data: [] })
  ;(useLabels as ReturnType<typeof vi.fn>).mockReturnValue({ data: [] })
  ;(useWorkflowProfiles as ReturnType<typeof vi.fn>).mockReturnValue({ data: PROFILES })
  ;(useAvailableModelIds as ReturnType<typeof vi.fn>).mockReturnValue({ data: [], isLoading: false })
  ;(createIssue as ReturnType<typeof vi.fn>).mockResolvedValue({
    id: 'issue_1',
    number: 1,
    title: 'T',
    status: 'backlog',
    health: 'active',
    projectId: 'test-project',
    labels: {},
    createdAt: '2026-06-16T00:00:00.000Z',
    updatedAt: '2026-06-16T00:00:00.000Z',
  })
}

function renderDialog(open = true) {
  const onClose = vi.fn()
  const utils = baseRender(<CreateIssueDialog open={open} onClose={onClose} />)
  return { onClose, ...utils }
}

const FRONTMATTER_BODY = [
  '---',
  'recommended_workflow: feature-flow',
  'recommended_workflow_reason: "UI changes match feature-flow"',
  'risk: high',
  '---',
  '',
  '## Background',
  'context',
].join('\n')

beforeEach(() => {
  vi.clearAllMocks()
  mockHooks()
})

afterEach(() => {
  vi.clearAllMocks()
})

describe('CreateIssueDialog frontmatter detection', () => {
  it('shows recommendation panel and pre-fills workflow selector when frontmatter present', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'T' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: FRONTMATTER_BODY } })

    const panel = await screen.findByTestId('workflow-recommendation')
    expect(panel).toBeInTheDocument()
    expect(screen.getByTestId('recommended-workflow')).toHaveTextContent('feature-flow')
    expect(screen.getByTestId('recommended-workflow-reason')).toHaveTextContent('UI changes match feature-flow')

    const workflowSelect = screen.getByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    await waitFor(() => {
      expect(workflowSelect.value).toBe('feature-flow')
    })
  })

  it('pre-fills risk selector from frontmatter', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Optional description'), {
      target: { value: '---\nrisk: high\n---\nbody' },
    })

    const highButton = await screen.findByRole('button', { name: 'high' })
    expect(highButton).toHaveAttribute('aria-pressed', 'true')
  })

  it('does not show recommendation panel when body has no frontmatter', () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Optional description'), {
      target: { value: '## Background\nplain markdown body' },
    })

    expect(screen.queryByTestId('workflow-recommendation')).not.toBeInTheDocument()
  })

  it('silently ignores malformed frontmatter and falls back to defaults', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'T' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), {
      target: { value: '---\nthis line has no colon\n---\nbody' },
    })

    expect(screen.queryByTestId('workflow-recommendation')).not.toBeInTheDocument()

    fireEvent.click(screen.getByText('Create'))
    await waitFor(() => {
      expect(createIssue).toHaveBeenCalledTimes(1)
    })
    const payload = (createIssue as ReturnType<typeof vi.fn>).mock.calls[0][0]
    expect(payload.workflowProfileId).toBeUndefined()
  })
})

describe('CreateIssueDialog recommendation override and acceptance', () => {
  it('one-click submit with recommendation creates issue with recommended workflow and risk', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'T' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: FRONTMATTER_BODY } })

    await screen.findByTestId('workflow-recommendation')

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createIssue).toHaveBeenCalledTimes(1)
    })
    const payload = (createIssue as ReturnType<typeof vi.fn>).mock.calls[0][0]
    expect(payload.workflowProfileId).toBe('feature-flow')
    expect(payload.risk).toBe('high')
  })

  it('manually changing the workflow selector overrides the frontmatter recommendation', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'T' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: FRONTMATTER_BODY } })

    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    await waitFor(() => {
      expect(workflowSelect.value).toBe('feature-flow')
    })

    fireEvent.change(workflowSelect, { target: { value: 'mohist/default' } })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createIssue).toHaveBeenCalledTimes(1)
    })
    const payload = (createIssue as ReturnType<typeof vi.fn>).mock.calls[0][0]
    expect(payload.workflowProfileId).toBe('mohist/default')
  })

  it('omits the workflowProfileId key when no profile is chosen and no frontmatter recommendation is present', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No selection' } })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createIssue).toHaveBeenCalledTimes(1)
    })
    const payload = (createIssue as ReturnType<typeof vi.fn>).mock.calls[0][0]
    expect(payload).not.toHaveProperty('workflowProfileId')
  })

  it('sends workflowProfileId=mohist/pr when the user explicitly selects it', async () => {
    ;(useWorkflowProfiles as ReturnType<typeof vi.fn>).mockReturnValue({
      data: [
        { id: 'mohist/default', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'PR work' } })

    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    fireEvent.change(workflowSelect, { target: { value: 'mohist/pr' } })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createIssue).toHaveBeenCalledTimes(1)
    })
    const payload = (createIssue as ReturnType<typeof vi.fn>).mock.calls[0][0]
    expect(payload.workflowProfileId).toBe('mohist/pr')
  })
})

describe('CreateIssueDialog -> issue detail workflow profile display round-trip', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockHooks()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('reflects the chosen profile on the resulting issue detail read model', async () => {
    const createdIssue = {
      id: 'issue_1',
      number: 1,
      title: 'PR work',
      status: 'backlog',
      health: 'active',
      projectId: 'test-project',
      labels: {},
      createdAt: '2026-06-16T00:00:00.000Z',
      updatedAt: '2026-06-16T00:00:00.000Z',
      workflowProfileId: 'mohist/pr',
    }
    ;(createIssue as ReturnType<typeof vi.fn>).mockResolvedValue(createdIssue)
    ;(useWorkflowProfiles as ReturnType<typeof vi.fn>).mockReturnValue({
      data: [
        { id: 'mohist/default', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'PR work' } })
    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    fireEvent.change(workflowSelect, { target: { value: 'mohist/pr' } })
    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createIssue).toHaveBeenCalledTimes(1)
    })
    const payload = (createIssue as ReturnType<typeof vi.fn>).mock.calls[0][0]
    expect(payload.workflowProfileId).toBe('mohist/pr')

    const returned = await (createIssue as ReturnType<typeof vi.fn>).mock.results[0].value
    expect(returned.workflowProfileId).toBe('mohist/pr')
  })
})
