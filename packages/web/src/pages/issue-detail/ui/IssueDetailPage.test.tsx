// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { RuntimeToastHost, useRuntimeToast } from '../../../shared/ui/toast'

const mockUseNavigate = vi.fn()

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => mockUseNavigate,
  }
})

const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseWorkflowTimeline = vi.fn()
const mockUseWorkflowYaml = vi.fn()
const mockUseAgentStatus = vi.fn()
const mockUseIssue = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: (...args: unknown[]) => mockUseIssue(...args),
    useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
    useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
    useWorkflowTimeline: (...args: unknown[]) => mockUseWorkflowTimeline(...args),
    useWorkflowYaml: (...args: unknown[]) => mockUseWorkflowYaml(...args),
  }
})

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: (...args: unknown[]) => mockUseAgentStatus(...args),
  }
})

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-1',
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('IssueDetailPage primaryEpic numbered display', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders #N as the primary epic identifier on the issue detail page when number is present', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        primaryEpic: {
          id: 'epic-uuid-aaaa-bbbb-cccccccccccc',
          number: 7,
          title: 'Numbered epic',
          status: 'active',
          priority: 'p1',
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('primary-epic-label')).toBeTruthy())
    const label = screen.getByTestId('primary-epic-number')
    expect(label).toHaveTextContent('#7')
  })

  it('does not display a truncated UUID as the primary epic identifier on the issue detail page when number is present', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        primaryEpic: {
          id: 'epic-uuid-aaaa-bbbb-cccccccccccc',
          number: 7,
          title: 'Numbered epic',
          status: 'active',
          priority: 'p1',
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('primary-epic-label')).toBeTruthy())
    const label = screen.getByTestId('primary-epic-number')
    const text = label.textContent ?? ''
    expect(text).not.toContain('epic-uuid-')
    expect(text).not.toContain('aaaa-bbbb')
    expect(text).not.toContain('cccccccccccc')
  })

  it('falls back to the truncated UUID for the primary epic label when number is null', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        primaryEpic: {
          id: 'epic-legacy-1234567890',
          number: null,
          title: 'Legacy epic',
          status: 'active',
          priority: 'p1',
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('primary-epic-label')).toBeTruthy())
    const label = screen.getByTestId('primary-epic-number')
    expect(label).toHaveTextContent('#epic-leg')
  })
})

describe('IssueDetailPage runtime decision surface', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  })

  afterEach(() => {
    cleanup()
  })

  it('mounts the runtime decision surface above the workflow stage bar', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'inspect'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())
    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('running')

    const surfaceRect = surface.getBoundingClientRect()
    const stageBar = container.querySelector('[data-testid="workflow-stage-bar"]')
    expect(stageBar).toBeTruthy()
    const stageRect = stageBar!.getBoundingClientRect()
    expect(surfaceRect.top).toBeLessThanOrEqual(stageRect.top)
  })

  it('exposes a single approval-required primary summary with approve/send-back inside the surface', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'check',
        health: 'paused',
        approvalState: {
          status: 'awaiting',
          stage: 'check',
          requestedAt: '2026-01-01T00:00:00.000Z',
        },
        recovery: {
          currentWorkItem: null,
          latestAttemptState: null,
          workflowSummaryState: 'awaiting-approval',
          allowedActions: ['approve', 'reject'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())
    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('approval-required')
    expect(surface.querySelector('[data-testid="runtime-action-approve"]')).toBeTruthy()
    expect(surface.querySelector('[data-testid="runtime-action-send-back"]')).toBeTruthy()
  })

  it('keeps the sessions panel reachable as supporting evidence beneath the surface', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
        health: 'active',
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())

    const surface = screen.getByTestId('runtime-decision-surface')
    const sessions = container.querySelector('[data-testid="workflow-sessions-panel"]')
    if (sessions) {
      const surfaceRect = surface.getBoundingClientRect()
      const sessionsRect = sessions.getBoundingClientRect()
      expect(sessionsRect.top).toBeGreaterThanOrEqual(surfaceRect.top)
    }
  })
})

function TransportNoticeTrigger() {
  const toast = useRuntimeToast()
  return (
    <button
      type="button"
      data-testid="trigger-disconnected-notice"
      onClick={() => {
        toast.push({
          tone: 'transport',
          title: 'Live events disconnected',
          body: 'Connection dropped. Activity continues to update in the background.',
          testId: 'runtime-toast-connection-disconnected',
          ttlMs: 30_000,
        })
      }}
    >
      Disconnect
    </button>
  )
}

function renderPageWithToastHost() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <RuntimeToastHost>
            <Routes>
              <Route path="/issues/:number" element={<IssueDetailPage />} />
            </Routes>
            <TransportNoticeTrigger />
          </RuntimeToastHost>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('IssueDetailPage disconnected-runtime-notice routing', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  })

  afterEach(() => {
    cleanup()
  })

  it('does not render transport-disconnect text inline between Description, Commits, or Comments when a runtime notice is dispatched', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        body: 'Issue description content for the test fixture.',
        comments: [
          {
            id: 'c1',
            author: 'tester',
            body: 'A reviewer comment that should remain free of connection state messaging.',
            createdAt: '2026-01-01T00:00:00Z',
          },
        ],
        recovery: {
          currentWorkItem: null,
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPageWithToastHost()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())

    fireEvent.click(screen.getByTestId('trigger-disconnected-notice'))

    await waitFor(() => expect(screen.getByTestId('runtime-toast-connection-disconnected')).toBeTruthy())

    const surface = screen.getByTestId('runtime-decision-surface')
    const description = Array.from(container.querySelectorAll('h2'))
      .find((heading) => heading.textContent === 'Description')
    const commitsHeading = Array.from(container.querySelectorAll('h2'))
      .find((heading) => (heading.textContent ?? '').startsWith('Commits'))
    const commentsHeading = Array.from(container.querySelectorAll('h2'))
      .find((heading) => (heading.textContent ?? '').startsWith('Comments'))

    expect(description).toBeTruthy()
    expect(commitsHeading).toBeFalsy()
    expect(commentsHeading).toBeTruthy()

    const surfaceRegion = surface
    const descriptionRegion = description!.closest('div')
    const commentsRegion = commentsHeading!.closest('div')

    const inlineTransportPhrases = [
      'Live events disconnected',
      'Connection dropped',
      'connection-disconnect',
      'reconnect',
      'transport',
    ]

    for (const phrase of inlineTransportPhrases) {
      expect(surfaceRegion.textContent ?? '').not.toContain(phrase)
      expect(descriptionRegion?.textContent ?? '').not.toContain(phrase)
      expect(commentsRegion?.textContent ?? '').not.toContain(phrase)
    }

    const toastHost = screen.getByTestId('runtime-toast-host')
    expect(toastHost.textContent).toContain('Live events disconnected')
    expect(toastHost.textContent).toContain('Connection dropped')
  })
})
