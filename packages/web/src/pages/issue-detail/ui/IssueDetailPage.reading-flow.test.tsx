import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import {
  mockArtifactContent,
  mockArtifacts,
  mockArtifactsError,
  mockIssue,
  mockIssueCommits,
  mockIssueDiff,
  mockIssueDiffError,
  mockIssueDiffPending,
  mountIssueDetail,
} from './_issueDetailMsw'
import { setScopedValue } from '../../../../tests/support/scoped-property'


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
            <Route path="/:project/issues/:number/files" element={<div data-testid="changed-files-destination" />} />
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

mountIssueDetail({ issue: makeIssue() })

beforeEach(() => {
  setScopedValue(window, 'innerWidth', 1280)
  window.dispatchEvent(new Event('resize'))
})

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage reading-flow — attention-ordered block sequence', () => {
  beforeEach(() => {
    mockIssueDiff(FULL_DIFF_DATA)
    mockIssueCommits(FULL_COMMITS_DATA)
  })

  it('orders one artifacts and one changes section before commits, description, and comments', async () => {
    mockIssue(makeIssue({
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
    }))

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const workflowFrame = await waitFor(() => screen.getByTestId('workflow-view-frame'))
    const activeRunYamlTrigger = screen.getByTestId('active-run-yaml-trigger')
    const artifacts = await waitFor(() => screen.getByTestId('latest-artifacts-panel'))
    const diffFiles = screen.getByTestId('diff-files-section')
    const commits = screen.getByTestId('commits-section')
    const description = screen.getByTestId('description-section')
    const comments = screen.getByTestId('comments-section')

    expect(readingFlow.contains(workflowFrame)).toBe(true)
    expect(readingFlow.contains(activeRunYamlTrigger)).toBe(true)
    expect(readingFlow.contains(artifacts)).toBe(true)
    expect(readingFlow.contains(diffFiles)).toBe(true)
    expect(readingFlow.contains(commits)).toBe(true)
    expect(readingFlow.contains(description)).toBe(true)
    expect(readingFlow.contains(comments)).toBe(true)

    expectPreceding(workflowFrame, activeRunYamlTrigger)
    expectPreceding(workflowFrame, artifacts)
    expectPreceding(artifacts, activeRunYamlTrigger)
    expectPreceding(activeRunYamlTrigger, diffFiles)
    expectPreceding(activeRunYamlTrigger, commits)
    expectPreceding(activeRunYamlTrigger, description)
    expectPreceding(activeRunYamlTrigger, comments)
    expectPreceding(workflowFrame, diffFiles)
    expectPreceding(workflowFrame, commits)
    expectPreceding(workflowFrame, description)
    expectPreceding(workflowFrame, comments)

    expectPreceding(diffFiles, commits)
    expectPreceding(diffFiles, description)
    expectPreceding(diffFiles, comments)

    expectPreceding(commits, description)
    expectPreceding(commits, comments)

    expectPreceding(description, comments)
    expect(screen.getAllByRole('heading', { name: 'Changes' })).toHaveLength(1)
    expect(screen.getAllByRole('heading', { name: 'Artifacts' })).toHaveLength(1)
    expect(screen.queryByTestId('diff-summary-banner')).toBeNull()
    expect(screen.queryByTestId('runtime-evidence-list')).toBeNull()
  })

  it('keeps explicit Changes and Artifacts boundaries when comparison data and a workflow run are absent', async () => {
    mockIssueDiff(null)
    mockIssueCommits(null)
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
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
    }))

    renderPage()

    const workflowFrame = await waitFor(() => screen.getByTestId('workflow-view-frame'))
    const description = screen.getByTestId('description-section')
    const comments = screen.getByTestId('comments-section')
    await screen.findByText(/Changed files and diff cannot be inspected/)

    const changes = await screen.findByTestId('diff-files-section')
    const artifacts = screen.getByTestId('latest-artifacts-panel')
    expect(within(changes).getByText(/Changed files and diff cannot be inspected/)).toBeTruthy()
    expect(within(artifacts).getByText('No workflow run or recorded artifacts yet.')).toBeTruthy()
    expect(screen.queryByTestId('diff-summary-banner')).toBeNull()
    expect(screen.queryByTestId('commits-section')).toBeNull()

    expectPreceding(workflowFrame, artifacts)
    expectPreceding(artifacts, changes)
    expectPreceding(changes, description)
    expectPreceding(description, comments)
  })
})

describe('IssueDetailPage Changes ownership', () => {
  it('keeps the sole Changes section mounted while diff data loads', async () => {
    const resolveDiff = mockIssueDiffPending()
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build' }))

    renderPage()

    const changes = await screen.findByTestId('diff-files-section')
    expect(within(changes).getByText('Loading changes...')).toBeTruthy()
    expect(screen.getAllByRole('heading', { name: 'Changes' })).toHaveLength(1)

    resolveDiff({ available: false, reason: 'not_started', message: 'Workspace unavailable' })
    expect(await within(changes).findByTestId('changes-unavailable')).toBeTruthy()
  })

  it('shows branches and scale once while preserving changed-files navigation and available commits', async () => {
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build', workflowRunId: 'wr-1' }))
    mockIssueDiff(FULL_DIFF_DATA)
    mockIssueCommits(FULL_COMMITS_DATA)

    renderPage()

    const changes = await screen.findByTestId('diff-files-section')
    await within(changes).findByTestId('changes-head')
    expect(screen.getAllByRole('heading', { name: 'Changes' })).toHaveLength(1)
    expect(within(changes).getByTestId('changes-head')).toHaveTextContent(FULL_DIFF_DATA.head)
    expect(within(changes).getByTestId('changes-base')).toHaveTextContent(FULL_DIFF_DATA.base)
    expect(within(changes).getByTestId('diff-files-scale')).toHaveTextContent('7 files changed · +142 −38')
    expect(screen.getByTestId('commits-section')).toHaveTextContent('Commits (3)')
    fireEvent.click(within(changes).getByRole('button', { name: 'View files' }))
    expect(screen.getByTestId('changed-files-destination')).toBeTruthy()
    expect(screen.queryByTestId('diff-summary-banner')).toBeNull()
  })

  it('keeps zero-change and empty-commit states visible', async () => {
    const emptyComparison = {
      ...FULL_DIFF_DATA,
      ahead: 0,
      summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
      files: [],
    }
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build' }))
    mockIssueDiff(emptyComparison)
    mockIssueCommits({ ...emptyComparison, commits: [] })

    renderPage()

    const changes = await screen.findByTestId('diff-files-section')
    await within(changes).findByText('No files changed yet')
    expect(within(changes).getByText('No files changed yet')).toBeTruthy()
    expect(screen.getByTestId('commits-section')).toHaveTextContent('No commits yet.')
  })

  it('renders one consequence-oriented workspace message and no commits failure', async () => {
    const unavailable = { available: false, reason: 'workspace_removed', message: 'Workspace unavailable' }
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build' }))
    mockIssueDiff(unavailable)
    mockIssueCommits(unavailable)

    renderPage()

    const changes = await screen.findByTestId('diff-files-section')
    await within(changes).findByTestId('changes-unavailable')
    expect(within(changes).getByTestId('changes-unavailable')).toHaveTextContent(
      'Workspace unavailable. Changed files and diff cannot be inspected, and commits cannot be inspected.',
    )
    expect(screen.getAllByText(/Workspace unavailable/)).toHaveLength(1)
    expect(screen.queryByText(/Workspace unavailable \/ Workspace unavailable/)).toBeNull()
    expect(screen.queryByTestId('commits-section')).toBeNull()
  })

  it('distinguishes a diff transport failure from an available:false workspace response', async () => {
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build' }))
    mockIssueDiffError()
    mockIssueCommits({ available: false, reason: 'runner_unavailable', message: 'Workspace unavailable' })

    renderPage()

    const changes = await screen.findByTestId('diff-files-section')
    await within(changes).findByText('Changes could not be loaded. Changed files and diff cannot be inspected.')
    expect(within(changes).getByText('Changes could not be loaded. Changed files and diff cannot be inspected.')).toBeTruthy()
    expect(within(changes).queryByTestId('changes-unavailable')).toBeNull()
    expect(screen.queryByText('Workspace unavailable')).toBeNull()
  })
})

describe('IssueDetailPage Artifacts ownership', () => {
  const artifact = {
    artifactId: 'artifact-review',
    workflowRunId: 'wr-1',
    taskRunId: 'build.1',
    path: 'review.md',
    displayName: 'review.md',
    kind: 'file',
    contentType: 'text/markdown',
    size: 12,
    recordedAt: '2026-01-02T00:00:00Z',
  }

  it('renders one ordinary collection and opens recorded content from it', async () => {
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build', workflowRunId: 'wr-1' }))
    mockArtifacts([artifact])
    mockArtifactContent(artifact.artifactId, '# Review\n\nPASS')

    renderPage()

    const panel = await screen.findByTestId('latest-artifacts-panel')
    await within(panel).findByText('review.md')
    expect(screen.getAllByRole('heading', { name: 'Artifacts' })).toHaveLength(1)
    expect(screen.getAllByTestId('latest-artifacts-panel')).toHaveLength(1)
    fireEvent.click(within(panel).getByText('review.md'))
    expect(await screen.findByRole('heading', { level: 2, name: 'Review' })).toBeTruthy()
    expect(screen.getByText('PASS')).toBeTruthy()
    expect(screen.queryByTestId('runtime-evidence-list')).toBeNull()
  })

  it('renders artifact transport failure inside the ordinary section', async () => {
    mockIssue(makeIssue({ status: 'in_progress', workflowStage: 'build', workflowRunId: 'wr-1' }))
    mockArtifactsError()

    renderPage()

    const panel = await screen.findByTestId('latest-artifacts-panel')
    expect(await within(panel).findByText('Failed to load artifacts')).toBeTruthy()
    expect(screen.getAllByRole('heading', { name: 'Artifacts' })).toHaveLength(1)
  })

  it('omits the ordinary collection during approval while retaining inline evidence', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'check',
      workflowRunId: 'wr-1',
      health: 'paused',
      approvalState: { status: 'awaiting', stage: 'check', requestedAt: '2026-01-01T00:00:00Z' },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))
    mockArtifacts([artifact])
    mockArtifactContent(artifact.artifactId, '# Review\n\nPASS')

    renderPage()

    expect(await screen.findByTestId('approval-review-evidence')).toBeTruthy()
    expect(screen.getByTestId('approval-artifact-review.md')).toBeTruthy()
    expect(screen.queryByTestId('latest-artifacts-panel')).toBeNull()
    expect(screen.queryByTestId('runtime-evidence-list')).toBeNull()
  })
})

describe('IssueDetailPage reading-flow — maximum-width body column', () => {
  it('renders the reading-flow as the lg:col-span-2 column of lg:grid-cols-3, wider than the rail', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
    }))

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
    mockIssueDiff(FULL_DIFF_DATA)
    mockIssueCommits(FULL_COMMITS_DATA)
  })

  it('does not wrap purely-content blocks (description, comments) in heavy bordered/filled card chrome', async () => {
    mockIssue(makeIssue({
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
    }))

    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(description.className).not.toContain('rounded-lg')
    expect(description.className).not.toContain('border-l-2')
    expect(description.className).not.toMatch(/\bbg-card\b/)

    const comments = await waitFor(() => screen.getByTestId('comments-section'))
    expect(comments.className).not.toContain('rounded-lg')
    expect(comments.className).not.toContain('border-l-2')
    expect(comments.className).not.toMatch(/\bbg-card\b/)

    const commits = await waitFor(() => screen.getByTestId('commits-section'))
    expect(commits.className).not.toContain('rounded-lg')
    expect(commits.className).not.toContain('border-l-2')
    expect(commits.className).not.toMatch(/\bbg-card\b/)
  })

  it('does not give the diff-files section heavier chrome than the reference-rail CardSection substrate', async () => {
    mockIssue(makeIssue({
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
    }))

    renderPage()

    const diffFiles = await screen.findByTestId('diff-files-section')
    await within(diffFiles).findByTestId('diff-files-summary')
    expect(diffFiles.className).not.toMatch(/\bbg-card\b/)
    expect(diffFiles.className).not.toContain('rounded-lg')

    const details = screen.getByTestId('issue-detail-details-metadata')
    const detailsSection = details.closest('section')
    expect(detailsSection).toBeTruthy()
    expect(detailsSection!.className).toContain('rounded-lg')
  })
})

describe('IssueDetailPage reading-flow — medium visual-weight tier', () => {
  it('marks the three tiers with stable data-tier-weight values and orders them by attention weight', async () => {
    mockIssue(makeIssue({
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
    }))

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
    mockIssueDiff(FULL_DIFF_DATA)
    mockIssueCommits(FULL_COMMITS_DATA)
  })

  it('keeps a presence signal and a leading-text hint for a long description', async () => {
    mockIssue(makeIssue({
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
    }))

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
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowRunId: 'wr-1',
      body: LONG_BODY,
    }))

    renderPage()

    const reader = await waitFor(() => screen.getByTestId('markdown-reader'))
    expect(reader.dataset.mode).toBe('collapsible')
  })

  it('keeps the change-list file/addition/deletion counts visible so the scale of the change is always readable', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowRunId: 'wr-1',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build it' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    const diffFiles = await screen.findByTestId('diff-files-section')
    await within(diffFiles).findByTestId('diff-files-scale')
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
    mockIssueDiff({
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
    })
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowRunId: 'wr-1',
    }))

    renderPage()

    const diffFiles = await screen.findByTestId('diff-files-section')
    const scale = await within(diffFiles).findByTestId('diff-files-scale')
    expect(scale.textContent ?? '').toMatch(/No files changed yet/i)
  })
})

describe('IssueDetailPage issue body metadata', () => {
  it('renders sanitized description and recommendation metadata without overriding current state', async () => {
    mockIssue(makeIssue({
      workflowProfileId: 'selected/profile',
      risk: 'high',
      body: [
        '---',
        'recommended_workflow: recommended/profile',
        'recommended_workflow_reason: "Template recommendation"',
        'risk: low',
        '---',
        'Visible description content',
      ].join('\n'),
    }))

    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(within(description).getByText('Visible description content')).toBeTruthy()
    expect(within(description).queryByText(/recommended_workflow/)).toBeNull()
    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).getByText('Recommended workflow')).toBeTruthy()
    expect(within(details).getByText('recommended/profile')).toBeTruthy()
    expect(within(details).getByText('Template recommendation')).toBeTruthy()
    expect(within(details).getAllByText('Risk')).toHaveLength(1)
    expect(within(details).getByText('high')).toBeTruthy()
    expect(within(details).queryByText('low')).toBeNull()
    expect(screen.getByTestId('reference-rail-workflow-profile')).toHaveTextContent('selected/profile')
  })

  it('shows recognized metadata but no Description for an envelope-only body', async () => {
    mockIssue(makeIssue({ body: ['---', 'risk: medium', '---'].join('\n') }))

    renderPage()

    const details = await waitFor(() => screen.getByTestId('issue-detail-details-metadata'))
    expect(within(details).getByText('medium')).toBeTruthy()
    expect(screen.queryByTestId('description-section')).toBeNull()
    expect(screen.queryByText('risk: medium')).toBeNull()
  })

  it('keeps bounded malformed metadata hidden while retaining its description', async () => {
    mockIssue(makeIssue({
      body: ['---', 'malformed line', 'risk: low', '---', 'Retained description'].join('\n'),
    }))

    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(within(description).getByText('Retained description')).toBeTruthy()
    expect(screen.queryByText('malformed line')).toBeNull()
    expect(screen.queryByTestId('risk-metadata-row')).toBeNull()
  })

  it('hides an unclosed envelope from description, preview, and editor', async () => {
    mockIssue(makeIssue({ body: ['---', 'risk: medium', 'raw internal text'].join('\n') }))

    renderPage()

    await waitFor(() => expect(screen.getByRole('button', { name: 'Edit issue' })).toBeTruthy())
    expect(screen.queryByTestId('description-section')).toBeNull()
    expect(screen.queryByTestId('description-preview-hint')).toBeNull()
    expect(screen.queryByText('raw internal text')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Edit issue' }))
    expect((await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement).value).toBe('')
  })
})

describe('IssueDetailPage reading-flow — decision surface and rail content excluded', () => {
  beforeEach(() => {
    mockIssueDiff(FULL_DIFF_DATA)
    mockIssueCommits(FULL_COMMITS_DATA)
  })

  it('does not place the runtime decision/action surface inside the reading flow', async () => {
    mockIssue(makeIssue({
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
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const readingFlow = screen.getByTestId('reading-flow')
    const headerTier = screen.getByTestId('status-header-tier')

    expect(readingFlow.contains(surface)).toBe(false)
    expect(headerTier.contains(surface)).toBe(true)

    for (const kind of ['start', 'stop', 'retry', 'resume', 'rerun', 'approve', 'send-back']) {
      const action = screen.getByTestId('issue-detail-page-container').querySelector(`[data-testid="decision-action-${kind}"]`)
      if (action) {
        expect(readingFlow.contains(action)).toBe(false)
        expect(headerTier.contains(action)).toBe(true)
      }
    }
  })

  it('does not place metadata, model, profile, prerequisites, drift, or convergence blocks inside the reading flow', async () => {
    mockIssue(makeIssue({
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
    }))

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
  })
})
