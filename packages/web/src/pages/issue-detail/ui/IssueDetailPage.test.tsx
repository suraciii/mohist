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
    useIssueEvents: () => ({ data: undefined, isLoading: false }),
  }
})

vi.mock('../../../widgets/issue-event-timeline', () => ({
  EventTimelinePanel: vi.fn((props: { issueNumber: number; issueId?: string | null; workflowStatus?: string | null }) => (
    <div data-testid="event-timeline-panel-mock" data-issue-number={props.issueNumber} data-issue-id={props.issueId ?? ''} data-workflow-status={props.workflowStatus ?? ''} />
  )),
}))

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

  it('renders the event timeline panel between diff/commits and comments', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        id: 'issue-14',
        number: 14,
        workflowStatus: 'running',
      }),
      isLoading: false,
      isError: false,
    })
    mockUseIssueDiff.mockReturnValue({
      data: {
        available: true,
        files: [],
        summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
      },
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('event-timeline-panel-mock')).toBeTruthy())
    const panel = screen.getByTestId('event-timeline-panel-mock')
    expect(panel).toHaveAttribute('data-issue-number', '14')
    expect(panel).toHaveAttribute('data-issue-id', 'issue-14')
    expect(panel).toHaveAttribute('data-workflow-status', 'running')
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

describe('IssueDetailPage repository metadata containment', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    window.innerWidth = 1280
    window.dispatchEvent(new Event('resize'))
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
  })

  afterEach(() => {
    cleanup()
  })

  it('bounds long repository metadata within the details column at desktop width', async () => {
    const gitUrl = 'https://github.com/suraciii/mohist.git'
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        projectName: 'mohist-local',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl,
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('repository-metadata-row')).toBeTruthy())
    expect(screen.getByTestId('issue-detail-page-container')).toHaveClass('min-w-0')
    expect(screen.getByTestId('issue-detail-page-container')).not.toHaveClass('overflow-x-hidden')
    expect(screen.getByTestId('issue-detail-content-grid')).toHaveClass('min-w-0')
    expect(screen.getByTestId('issue-detail-right-rail')).toHaveClass('min-w-0')
    expect(screen.getByTestId('issue-detail-details-metadata')).toHaveClass('min-w-0')
    expect(screen.getByTestId('repository-metadata-row')).toHaveClass('min-w-0')
    expect(screen.getByTestId('repository-metadata-value')).toHaveClass('min-w-0')
    expect(screen.getByTestId('repository-name')).toHaveTextContent('master')
    expect(screen.getByTestId('repository-base-branch')).toHaveTextContent('master')

    const url = screen.getByTestId('repository-git-url')
    expect(url).toHaveTextContent(gitUrl)
    expect(url).toHaveAttribute('title', gitUrl)
    expect(url).toHaveClass('block', 'min-w-0', 'break-all')
  })

  it('contains long diff branch names without page-level hidden overflow', async () => {
    const head = 'feature/super-long-branch-name-that-would-otherwise-force-horizontal-page-scroll-at-desktop-width'
    const base = 'release/equally-long-target-branch-name-that-needs-local-wrapping-not-page-clipping'
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
      }),
      isLoading: false,
      isError: false,
    })
    mockUseIssueDiff.mockReturnValue({
      data: {
        available: true,
        reason: null,
        base,
        head,
        mergeBase: 'abc123',
        ahead: 2,
        behind: 1,
        canFastForward: false,
        comparison: 'merge-base',
        summary: { filesChanged: 3, commits: 2, additions: 10, deletions: 4 },
        files: [],
      },
    })

    renderPage()

    const banner = await waitFor(() => screen.getByTestId('diff-summary-banner'))
    expect(screen.getByTestId('issue-detail-page-container')).not.toHaveClass('overflow-x-hidden')
    expect(banner).toHaveClass('min-w-0')
    expect(screen.getByTestId('diff-summary-head')).toHaveClass('break-all')
    expect(screen.getByTestId('diff-summary-head')).toHaveAttribute('title', head)
    expect(screen.getByTestId('diff-summary-base')).toHaveClass('break-all')
    expect(screen.getByTestId('diff-summary-base')).toHaveAttribute('title', base)
  })
})

describe('IssueDetailPage icon-only controls', () => {
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

  it('exposes an accessible name and baseline icon-button sizing for the edit issue control', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue(),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const editButton = await waitFor(() => screen.getByTestId('edit-issue-button'))
    expect(editButton).toHaveAttribute('aria-label', 'Edit issue')
    expect(screen.getByRole('button', { name: 'Edit issue' })).toBe(editButton)
    expect(editButton).toHaveClass('size-8')
    expect(editButton).not.toHaveClass('size-7')
    expect(editButton).not.toHaveClass('size-6')
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
