// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'

const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseWorkflowTimeline = vi.fn()
const mockUseWorkflowYaml = vi.fn()
const mockUseAgentStatus = vi.fn()
const mockUseIssue = vi.fn()
const mockUseWorkspaceStatus = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: (...args: unknown[]) => mockUseIssue(...args),
    useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
    useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
    useWorkflowTimeline: (...args: unknown[]) => mockUseWorkflowTimeline(...args),
    useWorkflowYaml: (...args: unknown[]) => mockUseWorkflowYaml(...args),
    useWorkspaceStatus: (...args: unknown[]) => mockUseWorkspaceStatus(...args),
    useIssueEvents: () => ({ data: undefined, isLoading: false }),
    getIssueWorkflowVariables: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
  }
})

vi.mock('../../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings')>()
  return {
    ...actual,
    useWorkflowProfiles: () => ({ data: [] }),
    useAvailableModelIds: () => ({ data: [] }),
    useOpencodeModel: () => ({ data: null }),
    useModelVariants: () => ({ data: [] }),
    useEffectiveDefaultWorkflowProfile: () => ({ data: null }),
  }
})

vi.mock('../../../widgets/issue-event-timeline/ui/EventTimelinePanel', () => ({
  EventTimelinePanel: vi.fn((props: { issueNumber: number; issueId?: string | null; workflowStatus?: string | null; enabled?: boolean }) => (
    <div
      data-testid="event-timeline-panel-mock"
      data-issue-number={props.issueNumber}
      data-issue-id={props.issueId ?? ''}
      data-workflow-status={props.workflowStatus ?? ''}
      data-enabled={props.enabled === undefined ? '' : String(props.enabled)}
    />
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
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

const FULL_DIFF_DATA = {
  available: true as const,
  reason: null,
  head: 'feature/issue-14-reading-flow',
  base: 'master',
  mergeBase: 'abc123',
  ahead: 4,
  behind: 0,
  canFastForward: true,
  comparison: 'merge-base' as const,
  summary: { filesChanged: 7, commits: 3, additions: 142, deletions: 38 },
  files: [],
}

const FULL_COMMITS_DATA = {
  available: true as const,
  reason: null,
  head: 'feature/issue-14-reading-flow',
  base: 'master',
  mergeBase: 'abc123',
  ahead: 4,
  behind: 0,
  canFastForward: true,
  comparison: 'merge-base' as const,
  summary: { filesChanged: 7, commits: 3, additions: 142, deletions: 38 },
  commits: [
    {
      hash: 'abcdef1234567890',
      shortHash: 'abcdef1',
      message: 'Add reading-flow tier chrome',
      author: 'tester',
      date: '2026-01-02T00:00:00Z',
      filesChanged: 4,
      additions: 60,
      deletions: 12,
      files: [],
    },
    {
      hash: 'fedcba0987654321',
      shortHash: 'fedcba0',
      message: 'Add collapsible key-signal',
      author: 'tester',
      date: '2026-01-03T00:00:00Z',
      filesChanged: 3,
      additions: 82,
      deletions: 26,
      files: [],
    },
  ],
}

const LONG_BODY = `# Reading Flow Specification

This issue tracks the layout reorganization for the issue detail page. The reading flow tier is the main content column, attention-ordered and content-forward.

## Goal

Apply the lightest possible chrome to purely-content blocks (description, comments), keep collapsible long blocks preserving their key signal when collapsed, and order the work content so the reader sees workflow progress and outputs first, then changes/diff, then commits, then description, then comments.

## Acceptance

- Description section has no heavy card chrome.
- Comments section has no heavy card chrome.
- Diff files summary stays visible when collapsed (file/addition/deletion counts).
- Reading flow column is the widest body column.

${'Lorem ipsum dolor sit amet consectetur adipiscing elit. '.repeat(40)}`

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

function expectPreceding(a: Element, b: Element) {
  const relationship = a.compareDocumentPosition(b)
  expect(
    (relationship & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
    `expected ${describeEl(a)} to precede ${describeEl(b)}`,
  ).toBe(true)
}

function describeEl(el: Element): string {
  const testId = el.getAttribute('data-testid')
  return testId ? `[data-testid="${testId}"]` : el.tagName.toLowerCase()
}

describe('IssueDetailPage reading-flow — attention-ordered block sequence', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
    window.dispatchEvent(new Event('resize'))
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: FULL_DIFF_DATA })
    mockUseIssueCommits.mockReturnValue({ data: FULL_COMMITS_DATA })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
  })

  it('orders workflow progress/outputs before changes/diff, commits, description, and comments', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        workflowRunId: 'wr-1',
        body: LONG_BODY,
        comments: [
          {
            id: 'c1',
            author: 'tester',
            body: 'A reviewer comment.',
            createdAt: '2026-01-04T00:00:00Z',
          },
        ],
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const workflowFrame = await waitFor(() => screen.getByTestId('workflow-view-frame'))
    const activeRunYamlTrigger = screen.getByTestId('active-run-yaml-trigger')
    const diffSummaryBanner = await waitFor(() => screen.getByTestId('diff-summary-banner'))
    const diffFiles = screen.getByTestId('diff-files-section')
    const commits = screen.getByTestId('commits-section')
    const description = screen.getByTestId('description-section')
    const comments = screen.getByTestId('comments-section')

    expect(readingFlow.contains(workflowFrame)).toBe(true)
    expect(readingFlow.contains(activeRunYamlTrigger)).toBe(true)
    expect(readingFlow.contains(diffSummaryBanner)).toBe(true)
    expect(readingFlow.contains(diffFiles)).toBe(true)
    expect(readingFlow.contains(commits)).toBe(true)
    expect(readingFlow.contains(description)).toBe(true)
    expect(readingFlow.contains(comments)).toBe(true)

    expectPreceding(workflowFrame, diffSummaryBanner)
    expectPreceding(workflowFrame, activeRunYamlTrigger)
    expectPreceding(activeRunYamlTrigger, diffSummaryBanner)
    expectPreceding(activeRunYamlTrigger, diffFiles)
    expectPreceding(activeRunYamlTrigger, commits)
    expectPreceding(activeRunYamlTrigger, description)
    expectPreceding(activeRunYamlTrigger, comments)
    expectPreceding(workflowFrame, diffFiles)
    expectPreceding(workflowFrame, commits)
    expectPreceding(workflowFrame, description)
    expectPreceding(workflowFrame, comments)

    expectPreceding(diffSummaryBanner, diffFiles)
    expectPreceding(diffSummaryBanner, commits)
    expectPreceding(diffSummaryBanner, description)
    expectPreceding(diffSummaryBanner, comments)

    expectPreceding(diffFiles, commits)
    expectPreceding(diffFiles, description)
    expectPreceding(diffFiles, comments)

    expectPreceding(commits, description)
    expectPreceding(commits, comments)

    expectPreceding(description, comments)
  })

  it('places workflow progress ahead of description and comments even when changes/diff and commits are absent', async () => {
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        workflowRunId: 'wr-1',
        body: LONG_BODY,
        comments: [
          {
            id: 'c1',
            author: 'tester',
            body: 'A reviewer comment.',
            createdAt: '2026-01-04T00:00:00Z',
          },
        ],
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const workflowFrame = await waitFor(() => screen.getByTestId('workflow-view-frame'))
    const description = screen.getByTestId('description-section')
    const comments = screen.getByTestId('comments-section')

    expect(screen.queryByTestId('diff-summary-banner')).toBeNull()
    expect(screen.queryByTestId('diff-files-section')).toBeNull()
    expect(screen.queryByTestId('commits-section')).toBeNull()

    expectPreceding(workflowFrame, description)
    expectPreceding(description, comments)
  })
})

describe('IssueDetailPage reading-flow — maximum-width body column', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
    window.dispatchEvent(new Event('resize'))
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the reading-flow as the lg:col-span-2 column of lg:grid-cols-3, wider than the rail', async () => {
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

    renderPage()

    const grid = await waitFor(() => screen.getByTestId('issue-detail-content-grid'))
    expect(grid.className).toMatch(/grid-cols-1/)
    expect(grid.className).toMatch(/lg:grid-cols-3/)

    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.className).toMatch(/lg:col-span-2/)
    expect(readingFlow.className).not.toMatch(/lg:col-span-1\b/)

    const spanTwoMatch = readingFlow.className.match(/lg:col-span-(\d)/)
    const spanTwo = spanTwoMatch ? Number(spanTwoMatch[1]) : 0
    expect(spanTwo).toBe(2)

    const railSpanMatch = referenceRail.className.match(/lg:col-span-(\d)/)
    const railSpan = railSpanMatch ? Number(railSpanMatch[1]) : 1
    expect(railSpan).toBeLessThan(spanTwo)
  })
})

describe('IssueDetailPage reading-flow — lightest chrome', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
    window.dispatchEvent(new Event('resize'))
    mockUseIssueDiff.mockReturnValue({ data: FULL_DIFF_DATA })
    mockUseIssueCommits.mockReturnValue({ data: FULL_COMMITS_DATA })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('does not wrap purely-content blocks (description, comments) in heavy bordered/filled card chrome', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
        body: LONG_BODY,
        comments: [
          {
            id: 'c1',
            author: 'tester',
            body: 'A reviewer comment.',
            createdAt: '2026-01-04T00:00:00Z',
          },
        ],
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(description.className).not.toContain('rounded-lg')
    expect(description.className).not.toContain('border-l-2')
    expect(description.className).not.toMatch(/\bbg-card\b/)

    const comments = screen.getByTestId('comments-section')
    expect(comments.className).not.toContain('rounded-lg')
    expect(comments.className).not.toContain('border-l-2')
    expect(comments.className).not.toMatch(/\bbg-card\b/)

    const commits = screen.getByTestId('commits-section')
    expect(commits.className).not.toContain('rounded-lg')
    expect(commits.className).not.toContain('border-l-2')
    expect(commits.className).not.toMatch(/\bbg-card\b/)
  })

  it('does not give the diff-files section heavier chrome than the reference-rail CardSection substrate', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const diffFiles = await waitFor(() => screen.getByTestId('diff-files-section'))
    expect(diffFiles.className).not.toMatch(/\bbg-card\b/)
    expect(diffFiles.className).not.toContain('rounded-lg')

    const details = screen.getByTestId('issue-detail-details-metadata')
    const detailsSection = details.closest('section')
    expect(detailsSection).toBeTruthy()
    expect(detailsSection!.className).toContain('rounded-lg')
  })
})

describe('IssueDetailPage reading-flow — medium visual-weight tier', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
    window.dispatchEvent(new Event('resize'))
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('marks the three tiers with stable data-tier-weight values and orders them by attention weight', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    expect(headline.dataset.tierWeight).toBe('status-header')
    expect(readingFlow.dataset.tierWeight).toBe('reading-flow')
    expect(referenceRail.dataset.tierWeight).toBe('reference-rail')

    const tierOrder = { 'status-header': 3, 'reading-flow': 2, 'reference-rail': 1 } as const
    const headlineWeight = tierOrder[headline.dataset.tierWeight as keyof typeof tierOrder]
    const flowWeight = tierOrder[readingFlow.dataset.tierWeight as keyof typeof tierOrder]
    const railWeight = tierOrder[referenceRail.dataset.tierWeight as keyof typeof tierOrder]
    expect(headlineWeight).toBeGreaterThan(flowWeight)
    expect(flowWeight).toBeGreaterThan(railWeight)

    expect(headline.dataset.sticky).toBe('true')
    expect(headline.className).toMatch(/bg-(info|warning|danger|success)-subtle/)
    expect(referenceRail.querySelector('[data-sticky="true"]')).toBeNull()
    expect(readingFlow.querySelector('[data-sticky="true"]')).toBeNull()
  })
})

describe('IssueDetailPage reading-flow — collapsible key-signal preservation', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
    window.dispatchEvent(new Event('resize'))
    mockUseIssueDiff.mockReturnValue({ data: FULL_DIFF_DATA })
    mockUseIssueCommits.mockReturnValue({ data: FULL_COMMITS_DATA })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('keeps a presence signal and a leading-text hint for a long description', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
        body: LONG_BODY,
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    const heading = within(description).getByRole('heading', { name: /Description/ })
    expect(heading).toBeTruthy()

    const hint = within(description).getByTestId('description-preview-hint')
    expect(hint).toBeTruthy()
    expect(hint.dataset.collapsedHint).toBe('true')
    expect(hint.textContent ?? '').not.toEqual('')
    expect(hint.textContent ?? '').toContain('Reading Flow Specification')
    expect((hint.textContent ?? '').length).toBeLessThan(LONG_BODY.length)
  })

  it('renders the description in collapsible mode so the body can be expanded on demand', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
        body: LONG_BODY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const reader = await waitFor(() => screen.getByTestId('markdown-reader'))
    expect(reader.dataset.mode).toBe('collapsible')
  })

  it('keeps the change-list file/addition/deletion counts visible so the scale of the change is always readable', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const diffFiles = await waitFor(() => screen.getByTestId('diff-files-section'))
    const summary = within(diffFiles).getByTestId('diff-files-summary')
    const scale = within(diffFiles).getByTestId('diff-files-scale')

    expect(diffFiles.dataset.collapsed).toBe('true')
    expect(summary).toBeTruthy()
    expect(scale).toBeTruthy()
    expect(scale.textContent ?? '').toContain('7')
    expect(scale.textContent ?? '').toContain('files changed')
    expect(scale.textContent ?? '').toContain('+142')
    expect(scale.textContent ?? '').toContain('−38')
  })

  it('keeps the change-list scale counts visible when no files have been changed yet', async () => {
    mockUseIssueDiff.mockReturnValue({
      data: {
        available: true as const,
        reason: null,
        head: 'feature/empty',
        base: 'master',
        mergeBase: 'abc',
        ahead: 0,
        behind: 0,
        canFastForward: true,
        comparison: 'merge-base' as const,
        summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
        files: [],
      },
    })
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowRunId: 'wr-1',
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const diffFiles = await waitFor(() => screen.getByTestId('diff-files-section'))
    const scale = within(diffFiles).getByTestId('diff-files-scale')
    expect(scale.textContent ?? '').toMatch(/No files changed yet/i)
  })
})

describe('IssueDetailPage reading-flow — decision surface and rail content excluded', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 })
    window.dispatchEvent(new Event('resize'))
    mockUseIssueDiff.mockReturnValue({ data: FULL_DIFF_DATA })
    mockUseIssueCommits.mockReturnValue({ data: FULL_COMMITS_DATA })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('does not place the runtime decision/action surface inside the reading flow', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'active',
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop', 'retry'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const readingFlow = screen.getByTestId('reading-flow')
    const headerTier = screen.getByTestId('status-header-tier')

    expect(readingFlow.contains(surface)).toBe(false)
    expect(headerTier.contains(surface)).toBe(true)

    for (const kind of ['start', 'stop', 'retry', 'resume', 'rerun', 'approve', 'send-back']) {
      const action = screen.getByTestId('issue-detail-page-container').querySelector(`[data-testid="runtime-action-${kind}"]`)
      if (action) {
        expect(readingFlow.contains(action)).toBe(false)
        expect(headerTier.contains(action)).toBe(true)
      }
    }
  })

  it('does not place metadata, model, profile, prerequisites, drift, or convergence blocks inside the reading flow', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        status: 'in_progress',
        workflowStage: 'build',
        workflowStatus: 'running',
        health: 'blocked',
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        prerequisites: [
          { number: 9, title: 'Prerequisite issue', completed: true },
        ],
        drift: {
          drifted: true,
          detectedAt: '2026-01-05T00:00:00Z',
          decision: 'needs-attention',
        },
        convergence: {
          blockingItemCount: 1,
          directlyRepairedCount: 0,
          reactionAttempts: 0,
          attemptedItemIds: [],
          resolvedItemIds: [],
          unresolvedItemIds: ['cb-1'],
          newBlockingItemIds: [],
          nonBlockingItemIds: [],
          blockedReason: 'A blocking check failed.',
        },
        recovery: {
          currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
          latestAttemptState: 'running',
          workflowSummaryState: 'running',
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')

    const detailsSection = screen.getByTestId('issue-detail-details-metadata').closest('section')
    expect(detailsSection).toBeTruthy()
    expect(referenceRail.contains(detailsSection!)).toBe(true)
    expect(readingFlow.contains(detailsSection!)).toBe(false)

    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
    expect(readingFlow.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(false)
    expect(referenceRail.contains(screen.getByTestId('workflow-profile-editor-frame'))).toBe(true)
    expect(readingFlow.contains(screen.getByTestId('workflow-profile-editor-frame'))).toBe(false)

    const configurationSection = screen.getByTestId('reference-rail-configuration')
    expect(referenceRail.contains(configurationSection)).toBe(true)
    expect(readingFlow.contains(configurationSection)).toBe(false)

    const prereqSection = screen.getByTestId('reference-rail-prerequisites')
    expect(referenceRail.contains(prereqSection)).toBe(true)
    expect(readingFlow.contains(prereqSection)).toBe(false)

    const driftToggle = screen.getByTestId('reference-rail-drift-toggle')
    const driftSection = driftToggle.closest('section')
    expect(driftSection).toBeTruthy()
    expect(referenceRail.contains(driftSection!)).toBe(true)
    expect(readingFlow.contains(driftSection!)).toBe(false)

    const convergenceToggle = screen.getByTestId('reference-rail-convergence-toggle')
    const convergenceSection = convergenceToggle.closest('section')
    expect(convergenceSection).toBeTruthy()
    expect(referenceRail.contains(convergenceSection!)).toBe(true)
    expect(readingFlow.contains(convergenceSection!)).toBe(false)

    const actionsSection = screen.getByTestId('reference-rail-actions')
    expect(referenceRail.contains(actionsSection)).toBe(true)
    expect(readingFlow.contains(actionsSection)).toBe(false)
  })
})
