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
import { createDependencyGraphTestComponents, LocationProbe } from './_epicDetailPageTestUtils'
import { useMswServer } from '../../../../tests/support/msw'

function linkedIssue(overrides: Pick<LinkedIssue, 'number'> & Partial<Omit<LinkedIssue, 'number'>>): LinkedIssue {
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
  http.get('*/api/projects/:projectId/epics/:epicNumber', () =>
    HttpResponse.json({ success: true, data: _epicData }),
  ),
  http.get('*/api/projects/:projectId/epics/:epicNumber/events', () =>
    HttpResponse.json({ success: true, data: [] }),
  ),
  http.get('*/api/projects/:projectId/issues', () =>
    HttpResponse.json({ success: true, data: _issuesData }),
  ),
  http.post('*/api/projects/:projectId/epics/:epicNumber/issues', async ({ request, params }) => {
    const body = await request.json() as { issueNumber: number }
    _addEpicIssueHandler({ epicNumber: Number(params.epicNumber), issueNumber: body.issueNumber })
    if (_addEpicIssueError) {
      return HttpResponse.json(
        { success: false, error: _addEpicIssueError.error, code: _addEpicIssueError.code, details: _addEpicIssueError.details },
        { status: _addEpicIssueError.status },
      )
    }
    return HttpResponse.json({ success: true, data: { epicNumber: Number(params.epicNumber), issueNumber: body.issueNumber } })
  }),
  http.delete('*/api/projects/:projectId/epics/:epicNumber/issues/:issueNumber', ({ params }) => {
    _removeEpicIssueHandler({ epicNumber: Number(params.epicNumber), issueNumber: Number(params.issueNumber) })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/issues/:issueNumber/start', ({ params }) => {
    _startIssueHandler(Number(params.issueNumber))
    return HttpResponse.json({ success: true, data: { issue: {}, message: '' } })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/start', ({ params }) => {
    _startEpicHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/done', ({ params }) => {
    _doneHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/close', ({ params }) => {
    _closeHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.patch('*/api/projects/:projectId/epics/:epicNumber', async ({ request, params }) => {
    const body = await request.json() as Record<string, unknown>
    _updateEpicHandler(Number(params.epicNumber), body)
    if (_blockUpdate) return new Promise(() => {})
    if (_updateEpicError) {
      return HttpResponse.json(
        { success: false, error: _updateEpicError.error },
        { status: _updateEpicError.status },
      )
    }
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/pause', async ({ request, params }) => {
    let reason: string | null = null
    try { const body = await request.json() as Record<string, unknown>; reason = (body.reason as string) ?? null } catch { /* empty body */ }
    _pauseHandler({ number: Number(params.epicNumber), reason })
    return HttpResponse.json({ success: true, data: {} })
  }),
  http.post('*/api/projects/:projectId/epics/:epicNumber/resume', ({ params }) => {
    _resumeHandler(Number(params.epicNumber))
    return HttpResponse.json({ success: true, data: {} })
  }),
)

const widgetBehavior = {
  mode: 'default' as 'default' | 'empty' | 'error',
}

const components = createDependencyGraphTestComponents(() => widgetBehavior.mode)

const epic = {
  projectId: 'proj-1',
  number: 123,
  title: 'Epic title',
  description: 'Epic description',
  priority: 'p1',
  status: 'active',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 2,
    blockedIssues: [{ number: 2, title: 'Blocked issue', health: 'blocked' }],
    activeIssues: [],
    nextIssue: { number: 2, title: 'Blocked issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
  linkedIssues: [
    linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
    linkedIssue({ number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p1' }),
  ],
}

const issues = [
  issue({ number: 1, title: 'Done issue', canStart: false, status: 'done', health: 'done' }),
  issue({ number: 2, title: 'Blocked issue', canStart: false, status: 'in_progress', health: 'blocked' }),
  issue({ number: 3, title: 'Candidate issue' }),
]

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const ui = () => (
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/epic/123']}>
          <LocationProbe />
          <Routes>
            <Route path="/epic/:number" element={<EpicDetailPage components={components} />} />
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
        { epicNumber: 123, issueNumber: 3 },
      )
    })
  })

  it('renders structured duplicate membership errors from the API', async () => {
    _addEpicIssueError = {
      status: 409,
      error: 'Issue already belongs to Epic "Runtime model"',
      code: 'DUPLICATE_EPIC_MEMBERSHIP',
      details: { existingEpicNumber: 9, existingEpicTitle: 'Runtime model' },
    }

    renderPage()

    await screen.findByTestId('epic-issue-selector-trigger')
    fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
    await waitFor(() => expect(screen.getByTestId('epic-issue-search')).toBeTruthy())
    const option = screen.getByTestId('epic-issue-option')
    fireEvent.click(option)
    fireEvent.click(screen.getByRole('button', { name: 'Add Issue' }))

    await waitFor(() => {
      expect(screen.getByText('Issue already belongs to Epic #9 Runtime model.')).toBeTruthy()
    })
  })

  it('removes a linked issue from the detail page', async () => {
    renderPage()

    await screen.findByTestId('linked-issues-list-region')

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0])

    expect(_removeEpicIssueHandler).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId('linked-issue-remove-confirm'))

    await waitFor(() => {
      expect(_removeEpicIssueHandler).toHaveBeenCalledWith({ epicNumber: 123, issueNumber: 1 })
    })
  })
})
const numberedEpic = {
  projectId: 'proj-1',
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
    linkedIssue({ number: 1, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
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

  it('does not display a surrogate identifier when number is present', async () => {
    renderPage()

    const label = await screen.findByTestId('epic-number')
    const text = label.textContent ?? ''
    expect(text).toBe('#12')
  })
})

describe('EpicDetailPage edit flow', () => {
  function defaultEpic() {
    return {
      projectId: 'proj-1',
      number: 123,
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
    activeIssues: [{ number: 1, title: 'Active issue', health: 'active' }],
    nextIssue: { number: 1, title: 'Active issue' },
    nextIssueReason: null,
    readyToMarkDone: false,
  },
      linkedIssues: [
        linkedIssue({ number: 1, title: 'Member issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p2' }),
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
      expect(callId).toBe(123)
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
      projectId: 'proj-1', number: 123,
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
describe('EpicDetailPage Ask Agent entry', () => {
  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
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
        blockedIssues: [{ number: 2, title: 'Blocked issue', health: 'blocked' }],
        activeIssues: [],
        nextIssue: { number: 2, title: 'Blocked issue' },
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

  it('navigates to the composer with the epic number on click', async () => {
    _epicData = makeEpic({ number: 123 })

    renderPage()

    const button = await screen.findByTestId('ask-agent-epic')
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toContain('/agent-sessions/new?epic=')
  })

  it('includes the epic number in the navigation URL', async () => {
    _epicData = makeEpic({ number: 321 })

    renderPage()

    const button = await screen.findByTestId('ask-agent-epic')
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toContain('epic=321')
  })
})
