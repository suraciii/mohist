import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render as baseRender, screen, fireEvent, waitFor } from './test-utils'
import { http, HttpResponse } from 'msw'
import { CreateIssueDialog } from '../src/features/create-issue'
import { server, useMswServer } from './support/msw'

const PROFILES = [
  { id: 'mohist/local', name: 'Default', description: 'Default profile', isDefault: true },
  { id: 'feature-flow', name: 'Feature Flow', description: 'Feature work', isDefault: false },
]

const PROFILES_PATH = '*/api/workflow-templates/system*'
const PROJECT_PROFILE_PATH = '*/api/projects/:projectId/workflow-profile'
const MODELS_PATH = '*/api/projects/:projectId/opencode/models'
const REPOS_PATH = '*/api/projects/:projectId/repositories'
const ISSUE_TEMPLATES_PATH = '*/api/issue-templates*'
const ISSUES_PATH = '*/api/projects/:projectId/issues*'

let createRequests: Array<Record<string, unknown>> = []
let createdIssue: Record<string, unknown>

const defaultHandlers = [
  http.get(PROFILES_PATH, () => HttpResponse.json({ success: true, data: PROFILES })),
  http.get(PROJECT_PROFILE_PATH, () =>
    HttpResponse.json({
      success: true,
      data: { projectId: 'test-project', defaultTemplateId: null, disabledWorkflowProfileIds: [] },
    }),
  ),
  http.get(MODELS_PATH, () => HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } })),
  http.get(REPOS_PATH, () => HttpResponse.json({ success: true, data: [] })),
  http.get(ISSUE_TEMPLATES_PATH, () => HttpResponse.json({ success: true, data: [] })),
  http.post(ISSUES_PATH, async ({ request }) => {
    const body = await request.json() as Record<string, unknown>
    createRequests.push(body)
    return HttpResponse.json({ success: true, data: createdIssue })
  }),
  http.get(ISSUES_PATH, () => HttpResponse.json({ success: true, data: [] })),
] as const

useMswServer(...defaultHandlers)

function mockProfiles(profiles: { id: string; name: string; description: string; isDefault: boolean }[]) {
  server.use(http.get(PROFILES_PATH, () => HttpResponse.json({ success: true, data: profiles })))
}

function mockProjectDefault(templateId: string) {
  server.use(
    http.get(PROJECT_PROFILE_PATH, () =>
      HttpResponse.json({
        success: true,
        data: { projectId: 'test-project', defaultTemplateId: templateId, disabledWorkflowProfileIds: [] },
      }),
    ),
  )
}

function resetCreateIssueResponse() {
  createRequests = []
  createdIssue = {
    number: 1,
    title: 'T',
    status: 'backlog',
    health: 'active',
    projectId: 'test-project',
    labels: {},
    createdAt: '2026-06-16T00:00:00Z',
    updatedAt: '2026-06-16T00:00:00Z',
  }
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
  resetCreateIssueResponse()
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
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
    expect(payload.workflowProfileId).toBeUndefined()
  })
})

describe('CreateIssueDialog recommendation override and acceptance', () => {
  it('one-click submit with recommendation creates issue with recommended workflow and risk', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'T' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: FRONTMATTER_BODY } })

    await screen.findByTestId('workflow-recommendation')

    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    await waitFor(() => {
      expect(workflowSelect.value).toBe('feature-flow')
    })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
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

    fireEvent.change(workflowSelect, { target: { value: 'mohist/local' } })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
    expect(payload.workflowProfileId).toBe('mohist/local')
  })

  it('shows the displayed effective workflowProfileId but lets the server inherit it when no frontmatter recommendation is present', async () => {
    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'No selection' } })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
    expect(payload.workflowProfileId).toBeUndefined()
  })

  it('shows the project-configured default workflowProfileId but does not serialize it unless manually changed', async () => {
    mockProfiles([
      { id: 'mohist/local', name: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', name: 'PR', description: '', isDefault: false },
    ])
    mockProjectDefault('mohist/github-pr')

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'Project default' } })
    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    await waitFor(() => {
      expect(workflowSelect.value).toBe('mohist/github-pr')
    })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
    expect(payload.workflowProfileId).toBeUndefined()
  })

  it('sends workflowProfileId=mohist/github-pr when the user explicitly selects it', async () => {
    mockProfiles([
      { id: 'mohist/local', name: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', name: 'PR', description: '', isDefault: false },
    ])

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'PR work' } })

    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    await waitFor(() => {
      expect(workflowSelect.querySelector('option[value="mohist/github-pr"]')).toBeTruthy()
    })
    fireEvent.change(workflowSelect, { target: { value: 'mohist/github-pr' } })

    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
    expect(payload.workflowProfileId).toBe('mohist/github-pr')
  })
})

describe('CreateIssueDialog -> issue detail workflow profile display round-trip', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    resetCreateIssueResponse()
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  it('reflects the chosen profile on the resulting issue detail read model', async () => {
    createdIssue = {
      number: 1,
      title: 'PR work',
      status: 'backlog',
      health: 'active',
      projectId: 'test-project',
      labels: {},
      createdAt: '2026-06-16T00:00:00.000Z',
      updatedAt: '2026-06-16T00:00:00.000Z',
      workflowProfileId: 'mohist/github-pr',
    }
    mockProfiles([
      { id: 'mohist/local', name: 'Default', description: '', isDefault: true },
      { id: 'mohist/github-pr', name: 'PR', description: '', isDefault: false },
    ])

    renderDialog()

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'PR work' } })
    const workflowSelect = await screen.findByRole('combobox', { name: 'Workflow' }) as HTMLSelectElement
    await waitFor(() => {
      expect(workflowSelect.querySelector('option[value="mohist/github-pr"]')).toBeTruthy()
    })
    fireEvent.change(workflowSelect, { target: { value: 'mohist/github-pr' } })
    fireEvent.click(screen.getByText('Create'))

    await waitFor(() => {
      expect(createRequests).toHaveLength(1)
    })
    const payload = createRequests[0]
    expect(payload.workflowProfileId).toBe('mohist/github-pr')

    expect(createdIssue.workflowProfileId).toBe('mohist/github-pr')
  })
})
