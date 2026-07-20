import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage, type IssueDetailPageComponents } from './IssueDetailPage'
import { RuntimeToastHost, useRuntimeToast } from '../../../shared/ui/toast'
import { mockIssue, mockIssueCommits, mockIssueDiff, mockWorkflowTimeline, mockWorkspaceStatus, mountIssueDetail } from './_issueDetailMsw'
import { setScopedValue } from '../../../../tests/support/scoped-property'

function LocationProbe() {
  const location = useLocation()
  return <div data-testid="current-path">{location.pathname}{location.search}</div>
}

const components: IssueDetailPageComponents = {
  EventTimelinePanel: (props) => (
    <div
      data-testid="event-timeline-panel-mock"
      data-issue-number={props.issueNumber}
      data-workflow-status={props.workflowStatus ?? ''}
      data-enabled={props.enabled === undefined ? '' : String(props.enabled)}
    />
  ),
}

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
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

mountIssueDetail({ issue: makeIssue() })

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <LocationProbe />
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage components={components} />} />
            <Route path="/:projectName/agent-sessions/new" element={<div>Agent Session Composer</div>} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage primaryEpic numbered display', () => {
  it('renders #N as the primary epic identifier on the issue detail page when number is present', async () => {
    mockIssue(makeIssue({
      primaryEpic: {
        number: 7,
        title: 'Numbered epic',
        status: 'active',
        priority: 'p1',
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('primary-epic-label')).toBeTruthy())
    const label = screen.getByTestId('primary-epic-number')
    expect(label).toHaveTextContent('#7')
  })

  it('does not display a truncated UUID as the primary epic identifier on the issue detail page when number is present', async () => {
    mockIssue(makeIssue({
      primaryEpic: {
        number: 7,
        title: 'Numbered epic',
        status: 'active',
        priority: 'p1',
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('primary-epic-label')).toBeTruthy())
    const label = screen.getByTestId('primary-epic-number')
    const text = label.textContent ?? ''
    expect(text).not.toContain('epic-uuid-')
    expect(text).not.toContain('aaaa-bbbb')
    expect(text).not.toContain('cccccccccccc')
  })

  it('uses the generic epic label when the epic has no number', async () => {
    mockIssue(makeIssue({
      primaryEpic: {
        number: null,
        title: 'Legacy epic',
        status: 'active',
        priority: 'p1',
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('primary-epic-label')).toBeTruthy())
    const label = screen.getByTestId('primary-epic-number')
    expect(label).toHaveTextContent('Epic')
  })

  it('renders the activity dialog entry in the header without rendering the inline timeline panel', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))
    mockIssueDiff({
      available: true,
      files: [],
      summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('activity-entry')).toBeTruthy())
    expect(container.querySelector('[data-testid="event-timeline-panel-mock"]')).toBeNull()
  })
})

describe('IssueDetailPage runtime decision surface', () => {
  it('mounts the runtime decision surface above the workflow stage bar', async () => {
    mockIssue(makeIssue({
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
    }))

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
    mockIssue(makeIssue({
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
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())
    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('approval-required')
    expect(surface.querySelector('[data-testid="runtime-action-approve"]')).toBeTruthy()
    expect(surface.querySelector('[data-testid="runtime-action-send-back"]')).toBeTruthy()
    const workflowFrame = screen.getByTestId('workflow-view-frame')
    expect(workflowFrame.querySelector('[data-testid="approve-button"]')).toBeNull()
    expect(workflowFrame.querySelector('[data-testid="request-changes-button"]')).toBeNull()
  })

  it('renders one Stop control on the page and it belongs to the runtime decision surface', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockWorkflowTimeline({
      workflowRunId: 'wr-1',
      status: 'running',
      currentStage: 'build',
      pendingWork: null,
      stages: [],
      availableActions: [{ name: 'stop', label: 'Stop', target: null }],
    })

    renderPage()

    await waitFor(() => expect(screen.getByTestId('runtime-decision-surface')).toBeTruthy())
    const stopButtons = screen.getAllByRole('button', { name: 'Stop' })
    expect(stopButtons).toHaveLength(1)
    expect(stopButtons[0]).toBe(screen.getByTestId('runtime-action-stop'))
    expect(screen.getByTestId('reference-rail').querySelector('[data-testid="runtime-action-stop"]')).toBeNull()
  })

  it('keeps the sessions panel reachable as supporting evidence beneath the surface', async () => {
    mockIssue(makeIssue({
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
    }))

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
    setScopedValue(window, 'innerWidth', 1280)
    window.dispatchEvent(new Event('resize'))
  })

  it('bounds long repository metadata within the details column at desktop width', async () => {
    const gitUrl = 'https://github.com/suraciii/mohist.git'
    mockIssue(makeIssue({
      projectName: 'mohist-local',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl,
      },
    }))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('repository-metadata-row')).toBeTruthy())
    expect(screen.getByTestId('issue-detail-page-container')).toHaveClass('min-w-0')
    expect(screen.getByTestId('issue-detail-page-container')).not.toHaveClass('overflow-x-hidden')
    expect(screen.getByTestId('issue-detail-content-grid')).toHaveClass('min-w-0')
    expect(screen.getByTestId('reference-rail')).toHaveClass('min-w-0')
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
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
    }))
    mockIssueDiff({
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
  it('exposes an accessible name and baseline icon-button sizing for the edit issue control', async () => {
    mockIssue(makeIssue())

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
          <LocationProbe />
          <RuntimeToastHost>
            <Routes>
              <Route path="/issues/:number" element={<IssueDetailPage components={components} />} />
              <Route path="/:projectName/agent-sessions/new" element={<div>Agent Session Composer</div>} />
            </Routes>
            <TransportNoticeTrigger />
          </RuntimeToastHost>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('IssueDetailPage disconnected-runtime-notice routing', () => {
  it('does not render transport-disconnect text inline between Description, Commits, or Comments when a runtime notice is dispatched', async () => {
    mockIssue(makeIssue({
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
    }))

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

describe('IssueDetailPage activity dialog', () => {
  it('renders the activity entry in the header with aria-label and a min hit-target baseline', async () => {
    mockIssue(makeIssue())

    renderPage()

    const entry = await waitFor(() => screen.getByTestId('activity-entry'))
    expect(entry).toHaveAttribute('aria-label', 'Activity')
    expect(screen.getByRole('button', { name: 'Activity' })).toBe(entry)
    expect(entry).toHaveClass('min-h-11')
    expect(entry).toHaveClass('min-w-11')
  })

  it('does not render the inline activity panel in the main content column', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('activity-entry')).toBeTruthy())
    expect(container.querySelector('[data-testid="event-timeline-panel-mock"]')).toBeNull()
  })

  it('does not mount the timeline panel (and so does not enable the events fetch) before the dialog opens', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('activity-entry')).toBeTruthy())
    expect(container.querySelector('[data-testid="event-timeline-panel-mock"]')).toBeNull()
    expect(screen.queryByTestId('event-timeline-panel-mock')).toBeNull()
  })

  it('mounts the timeline panel only after the entry opens the dialog and the dialog is unmounted on close', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('activity-entry')).toBeTruthy())
    expect(container.querySelector('[data-testid="event-timeline-panel-mock"]')).toBeNull()

    fireEvent.click(screen.getByTestId('activity-entry'))

    const panel = await waitFor(() => screen.getByTestId('event-timeline-panel-mock'))
    expect(panel).toHaveAttribute('data-issue-number', '14')
    expect(panel).toHaveAttribute('data-workflow-status', 'running')
    expect(panel).toHaveAttribute('data-enabled', 'true')

    const dialogContent = screen.getByTestId('activity-dialog-content')
    expect(dialogContent).toBeTruthy()

    fireEvent.keyDown(dialogContent, { key: 'Escape' })

    await waitFor(() => {
      expect(container.querySelector('[data-testid="event-timeline-panel-mock"]')).toBeNull()
    })
  })

  it('does not display a precise event count or fetch events before the dialog is first opened', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))

    renderPage()

    const entry = await waitFor(() => screen.getByTestId('activity-entry'))
    expect(entry.textContent ?? '').not.toMatch(/\b\d+\b/)
    expect(entry).not.toHaveTextContent(/\bcount\b/i)
  })

  it('renders the dialog as a near-fullscreen sheet on mobile width', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))

    setScopedValue(window, 'innerWidth', 375)
    window.dispatchEvent(new Event('resize'))

    renderPage()

    await waitFor(() => expect(screen.getByTestId('activity-entry')).toBeTruthy())
    fireEvent.click(screen.getByTestId('activity-entry'))

    const dialogContent = await waitFor(() => screen.getByTestId('activity-dialog-content'))
    expect(dialogContent).toHaveClass('h-[100dvh]')
    expect(dialogContent).toHaveClass('w-full')
    expect(dialogContent).toHaveClass('max-w-full')
    expect(dialogContent).toHaveClass('rounded-none')
  })

  it('passes enabled=true to the timeline panel only after the dialog opens, and unmounts the panel on close', async () => {
    mockIssue(makeIssue({
      number: 14,
      workflowStatus: 'running',
    }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('activity-entry')).toBeTruthy())
    expect(screen.queryByTestId('event-timeline-panel-mock')).toBeNull()

    fireEvent.click(screen.getByTestId('activity-entry'))

    const panel = await waitFor(() => screen.getByTestId('event-timeline-panel-mock'))
    expect(panel).toHaveAttribute('data-enabled', 'true')
    expect(panel).toHaveAttribute('data-issue-number', '14')
    expect(panel).toHaveAttribute('data-workflow-status', 'running')

    const dialogContent = screen.getByTestId('activity-dialog-content')
    fireEvent.keyDown(dialogContent, { key: 'Escape' })

    await waitFor(() => {
      expect(container.querySelector('[data-testid="event-timeline-panel-mock"]')).toBeNull()
    })
  })
})

describe('IssueDetailPage density and whitespace rhythm', () => {
  beforeEach(() => {
    setScopedValue(window, 'innerWidth', 1280)
    window.dispatchEvent(new Event('resize'))
  })

  function expectUsesUnifiedSpacingScale(element: HTMLElement) {
    const allowed = new Set([
      'space-y-1',
      'space-y-2',
      'space-y-3',
      'space-y-4',
      'space-y-6',
      'space-y-8',
    ])
    const match = element.className.match(/space-y-(\d+(?:\.\d+)?)/g) ?? []
    for (const cls of match) {
      expect(allowed.has(cls), `${element.tagName} uses ad-hoc spacing class ${cls}`).toBe(true)
    }
  }

  it('separates the main content column with a single group-level gap (no ad-hoc 5/7/9 values)', async () => {
    mockIssue(makeIssue({ number: 14 }))

    renderPage()

    const mainColumn = await waitFor(() => {
      const grid = screen.getByTestId('issue-detail-content-grid')
      const main = grid.querySelector(':scope > .lg\\:col-span-2') as HTMLElement | null
      expect(main).toBeTruthy()
      return main!
    })

    expectUsesUnifiedSpacingScale(mainColumn)
    expect(mainColumn.className).toContain('space-y-8')
    expect(mainColumn.className).not.toContain('space-y-5')
    expect(mainColumn.className).not.toContain('space-y-6')
    expect(mainColumn.className).not.toContain('space-y-7')

    const grid = screen.getByTestId('issue-detail-content-grid')
    expect(grid.className).toContain('gap-8')
    expect(grid.className).not.toContain('gap-6')
  })

  it('separates right-rail cards with a group-level gap and no ad-hoc 5/7/9 values', async () => {
    mockIssue(makeIssue({ number: 14 }))

    renderPage()

    const rightRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expectUsesUnifiedSpacingScale(rightRail)
    expect(rightRail.className).toContain('space-y-6')
    expect(rightRail.className).not.toContain('space-y-4')
    expect(rightRail.className).not.toContain('space-y-5')
  })

  it('gives the first-screen runtime decision surface breathing room rather than sitting flush against neighbors', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface).toBeTruthy()

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(surface)).toBe(true)
    expect(headerTier.className).toMatch(/space-y-/)
  })

  it('lets the header sit inside the spaced status-header tier so the next region is not flush', async () => {
    mockIssue(makeIssue({ number: 14 }))

    renderPage()

    const header = await waitFor(() => screen.getByTestId('issue-detail-header'))
    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(header)).toBe(true)
    expect(headerTier.className).toMatch(/space-y-/)
  })

  it('keeps the data-testid of every major section stable for downstream density regression checks', async () => {
    mockIssue(makeIssue({ number: 14, body: 'Issue body content.' }))

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('issue-detail-header')).toBeTruthy())
    expect(screen.getByTestId('status-header-tier')).toBeTruthy()
    expect(screen.getByTestId('reading-flow')).toBeTruthy()
    expect(screen.getByTestId('reference-rail')).toBeTruthy()
    expect(screen.getByTestId('runtime-decision-surface-frame')).toBeTruthy()
    expect(screen.getByTestId('workflow-view-frame')).toBeTruthy()
    expect(screen.getByTestId('workflow-profile-editor-frame')).toBeTruthy()
    expect(screen.getByTestId('issue-detail-content-grid')).toBeTruthy()
    expect(screen.getByTestId('description-section')).toBeTruthy()
    expect(screen.getByTestId('comments-section')).toBeTruthy()
    expect(screen.getByTestId('activity-entry')).toBeTruthy()

    expect(container.querySelector('[data-testid="issue-detail-header"]')).toBeTruthy()
    expect(container.querySelector('[data-testid="diff-summary-banner"]')).toBeFalsy()
  })

  it('places the branch rebase status above the workflow view instead of burying it below long runtime details', async () => {
    mockIssue(makeIssue({
      number: 14,
      body: 'Issue body content.',
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run-test',
      baseBranch: 'master',
      ahead: 1,
      behind: 2,
      rebaseInProgress: false,
      conflictingFiles: [],
    })

    const { container } = renderPage()

    const branchFrame = await waitFor(() => screen.getByTestId('branch-bar-frame'))
    expect(within(branchFrame).getByTestId('branch-bar')).toBeTruthy()
    await waitFor(() => expect(within(branchFrame).getByText('Rebase onto master')).toBeTruthy())

    const workflowFrame = screen.getByTestId('workflow-view-frame')
    const readingFlow = screen.getByTestId('reading-flow')
    expect(
      (branchFrame.compareDocumentPosition(workflowFrame) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
    ).toBe(true)
    expect(readingFlow.contains(branchFrame)).toBe(true)
    expect(readingFlow.contains(workflowFrame)).toBe(true)
    expect(container.querySelector('[data-testid="reference-rail"] [data-testid="branch-bar"]')).toBeNull()
  })

  it('does not render an empty PR delivery summary frame when the workflow has no PR delivery metadata', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
    }))
    mockWorkflowTimeline({
      stages: [{ id: 'build', tasks: [], checks: [] }],
      availableActions: [],
    })

    const { container } = renderPage()

    await waitFor(() => expect(screen.getByTestId('workflow-view-frame')).toBeTruthy())
    expect(container.querySelector('[data-testid="pr-delivery-summary-frame"]')).toBeNull()
  })

  it('removes decorative borders from plain section cards (Description, Comments, Commits) in favor of whitespace grouping', async () => {
    mockIssue(makeIssue({
      number: 14,
      body: 'Issue body content for the description test.',
      comments: [
        {
          id: 'c1',
          author: 'tester',
          body: 'A reviewer comment for the comments test.',
          createdAt: '2026-01-01T00:00:00Z',
        },
      ],
    }))
    mockIssueCommits({
      available: true,
      reason: null,
      head: 'feature/head',
      base: 'main',
      mergeBase: 'abc',
      ahead: 1,
      behind: 0,
      canFastForward: true,
      comparison: 'merge-base',
      summary: { filesChanged: 1, commits: 1, additions: 1, deletions: 0 },
      files: [],
      commits: [
        {
          hash: 'abcdef1234567890',
          shortHash: 'abcdef1',
          message: 'Test commit',
          author: 'tester',
          date: '2026-01-01T00:00:00Z',
        },
      ],
    })

    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(description.className).not.toContain('border-gray-200')

    const comments = await waitFor(() => screen.getByTestId('comments-section'))
    expect(comments.className).not.toContain('border-gray-200')

    const commits = await waitFor(() => screen.getByTestId('commits-section'))
    expect(commits.className).not.toContain('border-gray-200')
  })
})

describe('IssueDetailPage Ask Agent entry', () => {
  it('renders an Ask Agent button in the Actions card section', async () => {
    mockIssue(makeIssue())

    renderPage()

    const button = await waitFor(() => screen.getByTestId('ask-agent-issue'))
    expect(button).toBeTruthy()
    expect(button.textContent).toContain('Ask Agent')
  })

  it('navigates to the composer with ?issue=<number> on click', async () => {
    mockIssue(makeIssue({ number: 14 }))

    renderPage()

    const button = await waitFor(() => screen.getByTestId('ask-agent-issue'))
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toContain('/agent-sessions/new?issue=14')
  })

  it('uses encodeURIComponent for the issue number in the navigation URL', async () => {
    mockIssue(makeIssue({ number: 14 }))

    renderPage()

    const button = await waitFor(() => screen.getByTestId('ask-agent-issue'))
    fireEvent.click(button)

    expect(screen.getByTestId('current-path').textContent).toMatch(/\/agent-sessions\/new\?issue=14(&|$)/)
  })
})

describe('IssueDetailPage runtime status badges', () => {
  it('keeps identity metadata visible and shows the runtime summary only inside the headline (no separate runtime badge row)', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      priority: 'p1',
      isDraft: true,
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockWorkflowTimeline({
      workflowRunId: 'wr-1',
      status: 'running',
      currentStage: 'build',
      pendingWork: null,
      stages: [],
      availableActions: [{ name: 'stop', label: 'Stop', target: null }],
    })

    renderPage()

    const identity = await waitFor(() => screen.getByTestId('status-badges-identity'))
    expect(within(identity).getByTestId('priority-chip')).toBeTruthy()
    expect(within(identity).getByTestId('draft-pill')).toBeTruthy()
    expect(screen.queryByTestId('status-badges-runtime')).toBeNull()
    expect(screen.queryByTestId('runtime-status-pill')).toBeNull()
    expect(screen.queryByTestId('workflow-run-status-running')).not.toBeInTheDocument()
    expect(screen.queryByTestId('health-pill')).not.toBeInTheDocument()

    const headline = screen.getByTestId('status-headline')
    expect(headline.dataset.summary).toBe('running')
  })

  it('renders the queued summary inside the headline for backlog issues waiting on a prerequisite', async () => {
    mockIssue(makeIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      blocker: { kind: 'waiting-for', issue: { number: 9, title: 'Prerequisite' } },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('queued')
    expect(screen.queryByTestId('status-badges-runtime')).toBeNull()
    expect(screen.queryByTestId('runtime-status-pill')).toBeNull()
  })
})
