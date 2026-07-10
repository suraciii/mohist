// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { http, HttpResponse } from 'msw'
import { ProjectProvider } from '../../../entities/project'
import { EpicStatus } from '../../../entities/epic'
import type { LinkedIssue } from '../../../entities/epic'
import { IssueHealth, IssueStatus, WorkflowStage } from '../../../entities/issue'
import { EpicDetailPage } from './EpicDetailPage'
import { createDependencyGraphTestComponents, LocationProbe } from './_epicDetailPageTestHarness'
import { useMswServer } from '../../../../tests/support/msw'

function linkedIssue(overrides: Pick<LinkedIssue, 'id' | 'number'> & Partial<Omit<LinkedIssue, 'id' | 'number'>>): LinkedIssue {
  return {
    title: 'Issue one',
    status: IssueStatus.Backlog,
    stage: WorkflowStage.Plan,
    health: IssueHealth.Active,
    priority: 'p2',
    canStart: true,
    startBlocker: null,
    prerequisiteNumbers: [],
    externalPrerequisites: [],
    ...overrides,
  }
}

function issue(overrides: Record<string, unknown>) {
  return {
    isDraft: false,
    canStart: true,
    blocker: null,
    status: 'backlog',
    health: 'active',
    ...overrides,
  }
}

let _epicData: unknown = null
let _issuesData: unknown[] = []
const _addEpicIssueHandler = vi.fn()
const _removeEpicIssueHandler = vi.fn()
const _startIssueHandler = vi.fn()
const _startEpicHandler = vi.fn()
const _doneHandler = vi.fn()
const _closeHandler = vi.fn()
const _updateEpicHandler = vi.fn()
const _pauseHandler = vi.fn()
const _resumeHandler = vi.fn()
let _blockUpdate = false
let _updateEpicError: { status: number; error: string } | null = null
let _addEpicIssueError: { status: number; error: string; code: string; details: unknown } | null = null

useMswServer(
  http.get('*/api/projects/:projectId/epics/:epicId', () =>
    HttpResponse.json({ success: true, data: _epicData }),
  ),
  http.get('*/api/projects/:projectId/epics/:epicId/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issuesData }),
  ),
  http.post('*/api/projects/:projectId/epics/:epicId/issues', async ({ request, params }) => {
    const body = await request.json() as { issueId: string }
    _addEpicIssueHandler({ epicId: params.epicId, issueId: body.issueId })
    if (_addEpicIssueError) {
      return HttpResponse.json(
        { success: false, error: _addEpicIssueError.error, code: _addEpicIssueError.code, details: _addEpicIssueError.details },
        { status: _addEpicIssueError.status },
      )
    }
    return HttpResponse.json({ success: true, data: { epicId: params.epicId, issueId: body.issueId } })
  }),
  http.delete('*/api/projects/:projectId/epics/:epicId/issues/:issueId', ({ params }) => {
    _removeEpicIssueHandler({ epicId: params.epicId, issueId: params.issueId })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:issueNumber/start', ({ params }) => {
    _startIssueHandler(Number(params.issueNumber))
    return HttpResponse.json({ success: true, data: { issue: {}, message: '' } })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/start', ({ params }) => {
    _startEpicHandler(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/done', ({ params }) => {
    _doneHandler(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/close', ({ params }) => {
    _closeHandler(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.patch('*/api/projects/:projectId/epics/:epicId', async ({ request, params }) => {
    const body = await request.json() as Record<string, unknown>
    _updateEpicHandler(params.epicId, body)
    if (_blockUpdate) return new Promise(() => {})
    if (_updateEpicError) {
      return HttpResponse.json(
        { success: false, error: _updateEpicError.error },
        { status: _updateEpicError.status },
      )
    }
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/pause', async ({ request, params }) => {
    let reason: string | null = null
    try { const body = await request.json() as Record<string, unknown>; reason = (body.reason as string) ?? null } catch { /* empty body */ }
    _pauseHandler({ id: params.epicId, reason })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicId/resume', ({ params }) => {
    _resumeHandler(params.epicId)
    return HttpResponse.json({ success: true, data: {} })
  }),
)

const widgetBehavior = {
  mode: 'default' as 'default' | 'empty' | 'error',
}

const components = createDependencyGraphTestComponents(() => widgetBehavior.mode)

const epic = {
  id: 'epic-12345678',
  title: 'Epic title',
  description: 'Epic description',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 2,
    blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
    activeIssues: [],
    nextIssue: { id: 'issue-2', number: 2, title: 'Blocked issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
  linkedIssues: [
    linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
    linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p1' }),
  ],
}

const issues = [
  issue({ id: 'issue-1', number: 1, title: 'Done issue', canStart: false, status: 'done', health: 'done' }),
  issue({ id: 'issue-2', number: 2, title: 'Blocked issue', canStart: false, status: 'in_progress', health: 'blocked' }),
  issue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
]

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const ui = () => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/epic/epic-12345678']}>
          <LocationProbe />
          <Routes>
            <Route path="/epic/:id" element={<EpicDetailPage components={components} />} />
            <Route path="/epics" element={<div>Epics</div>} />
            <Route path="/issues/:number" element={<div>Issue</div>} />
            <Route path="/agent-sessions/new" element={<div>Agent Session Composer</div>} />
            <Route path="/:projectName/agent-sessions/:sessionId" element={<div>Agent Session</div>} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>
  )
  const result = render(ui())
  return { ...result, rerenderPage: () => result.rerender(ui()) }
}

describe('EpicDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _blockUpdate = false
    _updateEpicError = null
    _addEpicIssueError = null
    _epicData = epic
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('renders epic progress and linked issues', async () => {
    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.getByText('Epic title')).toBeTruthy()
    expect(screen.getByText('Epic description')).toBeTruthy()
    expect(screen.getByText(/1 \/ 2/)).toBeTruthy()
    expect(screen.getByText(/#2 Blocked issue/)).toBeTruthy()
    expect(screen.getByText('Done issue')).toBeTruthy()
  })

  it('adds an available issue from the detail page', async () => {
    renderPage()

    await screen.findByTestId('epic-issue-selector-trigger')
    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    await waitFor(() => expect(screen.getByTestId('epic-issue-search')).toBeTruthy())
    const option = screen.getByTestId('epic-issue-option')
    fireEvent.click(option)
    fireEvent.click(screen.getByRole('button', { name: 'Add Issue' }))

    await waitFor(() => {
      expect(_addEpicIssueHandler).toHaveBeenCalledWith(
        { epicId: 'epic-12345678', issueId: 'issue-3' },
      )
    })
  })

  it('renders structured duplicate membership errors from the API', async () => {
    _addEpicIssueError = {
      status: 409,
      error: 'Issue already belongs to Epic "Runtime model"',
      code: 'DUPLICATE_EPIC_MEMBERSHIP',
      details: { existingEpicId: 'epic-runtime', existingEpicTitle: 'Runtime model' },
    }

    renderPage()

    await screen.findByTestId('epic-issue-selector-trigger')
    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    await waitFor(() => expect(screen.getByTestId('epic-issue-search')).toBeTruthy())
    const option = screen.getByTestId('epic-issue-option')
    fireEvent.click(option)
    fireEvent.click(screen.getByRole('button', { name: 'Add Issue' }))

    await waitFor(() => {
      expect(screen.getByText('Issue already belongs to Epic #epic-run Runtime model.')).toBeTruthy()
    })
  })

  it('removes a linked issue from the detail page', async () => {
    renderPage()

    await screen.findByTestId('linked-issues-list-region')

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0])

    expect(_removeEpicIssueHandler).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))

    await waitFor(() => {
      expect(_removeEpicIssueHandler).toHaveBeenCalledWith({ epicId: 'epic-12345678', issueId: 'issue-1' })
    })
  })
})
const numberedEpic = {
  id: 'epic-uuid-aaaa-bbbb-cccccccccccc',
  number: 12,
  title: 'Numbered Epic',
  description: 'Has a number',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
  linkedIssues: [
    linkedIssue({ id: 'issue-1', number: 1, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
  ],
}

describe('EpicDetailPage numbered display', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    _blockUpdate = false
    _updateEpicError = null
    _addEpicIssueError = null
    _epicData = numberedEpic
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('renders #N as the primary epic identifier when number is present', async () => {
    renderPage()

    const label = await screen.findByTestId('epic-number')
    expect(label).toHaveTextContent('#12')
  })

  it('does not display a truncated UUID as the primary epic identifier when number is present', async () => {
    renderPage()

    const label = await screen.findByTestId('epic-number')
    const text = label.textContent ?? ''
    expect(text).not.toContain('epic-uuid-')
    expect(text).not.toContain('aaaa-bbbb')
    expect(text).not.toContain('cccccccccccc')
  })

  it('falls back to the truncated UUID when epic number is null', async () => {
    _epicData = { ...epic, number: null }
    renderPage()

    const label = await screen.findByTestId('epic-number')
    expect(label).toHaveTextContent('#epic-123')
  })
})

describe('EpicDetailPage edit flow', () => {
  function defaultEpic() {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: 'active',
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
progress: {
    deliveredCount: 0,
    totalIssueCount: 1,
    blockedIssues: [],
    activeIssues: [{ id: 'issue-1', number: 1, title: 'Active issue', health: 'active' }],
    nextIssue: { id: 'issue-1', number: 1, title: 'Active issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Member issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p2' }),
      ],
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    _blockUpdate = false
    _updateEpicError = null
    _addEpicIssueError = null
    _epicData = defaultEpic()
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('opens the edit dialog prefilled with current epic metadata', async () => {
    renderPage()

    await screen.findByTestId('edit-epic-button')
    fireEvent.click(screen.getByTestId('edit-epic-button'))

    const titleInput = screen.getByLabelText('Title') as HTMLInputElement
    const descriptionInput = screen.getByLabelText('Description') as HTMLTextAreaElement
    expect(titleInput.value).toBe('Epic title')
    expect(descriptionInput.value).toBe('Epic description')
  })

  it('saves the edit through the PATCH API and refreshes displayed metadata', async () => {
    const refreshedEpic = {
      ...defaultEpic(),
      title: 'Renamed Epic',
      description: 'Updated description',
      priority: 'p0',
      updatedAt: '2026-01-02T00:00:00Z',
    }

    renderPage()

    await screen.findByTestId('edit-epic-button')
    fireEvent.click(screen.getByTestId('edit-epic-button'))
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Renamed Epic' } })
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'Updated description' } })
    fireEvent.click(screen.getByRole('combobox', { name: 'Priority' }))

    const highOption = await screen.findByText('P0 - Critical')
    const optionEl = highOption.closest('[data-slot="select-item"]') as HTMLElement
    fireEvent.pointerDown(optionEl)
    fireEvent.pointerUp(optionEl)
    fireEvent.click(optionEl)

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(_updateEpicHandler).toHaveBeenCalledTimes(1)
      const [callId, callBody] = _updateEpicHandler.mock.calls[0]
      expect(callId).toBe('epic-12345678')
      expect(callBody).toEqual({
        title: 'Renamed Epic',
        description: 'Updated description',
        priority: 'p0',
      })
    })

    _epicData = refreshedEpic
  })

  it('reflects updated title, description, and priority when useEpic returns refreshed data', async () => {
    const refreshedEpic = {
      ...defaultEpic(),
      title: 'Renamed Epic',
      description: 'Updated description',
      priority: 'p0',
      updatedAt: '2026-01-02T00:00:00Z',
    }

    _epicData = refreshedEpic

    renderPage()

    await screen.findByTestId('epic-number')
    expect(screen.getByRole('heading', { name: 'Renamed Epic' })).toBeTruthy()
    expect(screen.getByText('Updated description')).toBeTruthy()
    const updatedBadges = screen.getAllByText('P0')
    expect(updatedBadges.length).toBeGreaterThan(0)
  })

  it('does not change linked issue membership or lifecycle status in the UI during the edit', async () => {
    renderPage()

    await screen.findByTestId('linked-issues-list-region')
    expect(screen.getByText('Member issue')).toBeTruthy()
    expect(screen.getByTestId('linked-issues-list-region')).toHaveTextContent('active')

    fireEvent.click(screen.getByTestId('edit-epic-button'))
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Renamed' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(_updateEpicHandler).toHaveBeenCalled())

    expect(screen.getByText('Member issue')).toBeTruthy()
    expect(screen.getByTestId('linked-issues-list-region')).toHaveTextContent('active')
  })

  it('disables the save button while the update is pending', async () => {
    _blockUpdate = true

    renderPage()

    await screen.findByTestId('edit-epic-button')
    fireEvent.click(screen.getByTestId('edit-epic-button'))

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      const saveButton = screen.getByRole('button', { name: 'Saving...' })
      expect(saveButton).toBeDisabled()
    })
  })

  it('shows update errors from the API in the dialog', async () => {
    _updateEpicError = { status: 400, error: 'Update failed: invalid priority' }

    renderPage()

    await screen.findByTestId('edit-epic-button')
    fireEvent.click(screen.getByTestId('edit-epic-button'))

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(screen.getByText('Update failed: invalid priority')).toBeTruthy()
    })
  })
})
describe('EpicDetailPage markdown description', () => {
  const markdownDescription = [
    '## Goal',
    '',
    'Ship the epic board fix with:',
    '',
    '- priority ordering',
    '- **accurate** progress',
    '- and *next* issue',
    '',
    'See [the design](./design.md).',
  ].join('\n')

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: markdownDescription,
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: 0,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [],
      ...overrides,
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    _blockUpdate = false
    _updateEpicError = null
    _addEpicIssueError = null
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('renders headings, lists, and emphasis as formatted content via MarkdownReader', async () => {
    _epicData = makeEpic()

    renderPage()

    const container = await screen.findByTestId('epic-description')
    expect(container.querySelector('.markdown-reader')).toBeTruthy()

    const heading = screen.getByRole('heading', { name: 'Goal' })
    expect(heading).toBeTruthy()
    expect(heading.tagName).toBe('H4')
    expect(container.textContent).not.toContain('## Goal')
    expect(container.textContent).not.toContain('- priority ordering')

    const listItems = container.querySelectorAll('li')
    expect(listItems.length).toBe(3)

    const boldNodes = container.querySelectorAll('strong')
    const emphasisNodes = container.querySelectorAll('em')
    expect(boldNodes.length).toBeGreaterThan(0)
    expect(emphasisNodes.length).toBeGreaterThan(0)
    expect(container.textContent).not.toContain('**accurate**')
  })

  it('renders a plain description readably through MarkdownReader without spurious formatting', async () => {
    _epicData = makeEpic({ description: 'Just a plain description with no markdown.' })

    renderPage()

    const container = await screen.findByTestId('epic-description')
    expect(container.querySelector('.markdown-reader')).toBeTruthy()
    expect(container.textContent).toContain('Just a plain description with no markdown.')
    expect(container.querySelectorAll('h1, h2, h3, h4, h5, h6').length).toBe(0)
  })
})
describe('EpicDetailPage Ask Agent entry (T-005)', () => {
  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: 7,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: 'active',
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 1,
        totalIssueCount: 2,
        blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
        activeIssues: [],
        nextIssue: { id: 'issue-2', number: 2, title: 'Blocked issue' },
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [],
      ...overrides,
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    _blockUpdate = false
    _updateEpicError = null
    _addEpicIssueError = null
    _issuesData = issues
  })

  afterEach(() => {
    cleanup()
  })

  it('renders an Ask Agent button in the action group', async () => {
    _epicData = makeEpic()

    renderPage()

    const button = await screen.findByTestId('ask-agent-epic')
    expect(button).toBeTruthy()
    expect(button.textContent).toContain('Ask Agent')
  })

  it('navigates to the composer with ?epic=<id> on click', async () => {
    _epicData = makeEpic({ id: 'epic-12345678' })

    renderPage()

    const button = await screen.findByTestId('ask-agent-epic')
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toContain('/agent-sessions/new?epic=')
  })

  it('includes the epic id in the navigation URL', async () => {
    _epicData = makeEpic({ id: 'epic-abc-def' })

    renderPage()

    const button = await screen.findByTestId('ask-agent-epic')
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toContain('epic=epic-abc-def')
  })
})
