import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import {
  mockAgentStatus,
  mockIssue,
  mockWorkspaceStatus,
  mountIssueDetail,
} from './_issueDetailMsw'


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
    title: 'Control workspace test',
    body: 'Long body that should remain in reading-flow below the control region.',
    status: 'in_progress',
    workflowStage: 'build',
    workflowStatus: 'running',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [
      {
        id: 'c1',
        author: 'tester',
        body: 'A reviewer comment that must remain below the control region.',
        createdAt: '2026-01-02T00:00:00Z',
      },
    ],
    isDraft: false,
    canStart: true,
    blocker: null,
    model: 'sonnet',
    prerequisites: [
      { number: 9, title: 'Prerequisite issue', completed: true, status: 'done', health: 'done' },
    ],
    repository: {
      name: 'master',
      baseBranch: 'master',
      gitUrl: 'https://github.com/suraciii/mohist.git',
    },
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

function mockMatchMedia(narrow: boolean) {
  const mql = {
    matches: narrow,
    media: '(max-width: 1023.98px)',
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
    onchange: null,
  }
  vi.stubGlobal('matchMedia', vi.fn(() => mql))
  setScopedValue(window, 'innerWidth', narrow ? 375 : 1280)
}

mountIssueDetail({ issue: makeIssue() })

beforeEach(() => {
  mockMatchMedia(false)
  setScopedValue(window, 'innerWidth', 1280)
  window.dispatchEvent(new Event('resize'))
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const CONTROL_REGION_BLOCKS = [
  'status-headline',
  'issue-detail-header',
  'runtime-decision-surface',
] as const

const READING_FLOW_BLOCKS = [
  'workflow-view-frame',
  'description-section',
  'comments-section',
  'diff-files-section',
  'commits-section',
] as const

const REFERENCE_RAIL_BLOCKS = [
  'reference-rail-details',
  'reference-rail-workflow-profile',
  'reference-rail-configuration',
  'reference-rail-actions',
  'reference-rail-prerequisites',
] as const

const LIFECYCLE_ACTIONS = [
  'approve',
  'send-back',
  'retry',
  'resume',
  'rerun',
  'stop',
  'start',
  'inspect',
] as const

async function assertControlWorkspaceBasics(getContainer: () => HTMLElement) {
  const page = getContainer()
  const headerTier = page.querySelector('[data-testid="status-header-tier"]') as HTMLElement
  const readingFlow = page.querySelector('[data-testid="reading-flow"]') as HTMLElement
  const referenceRail = page.querySelector('[data-testid="reference-rail"]') as HTMLElement

  expect(headerTier).toBeTruthy()
  expect(readingFlow).toBeTruthy()
  expect(referenceRail).toBeTruthy()

  const tierWeight = { 'status-header': 3, 'reading-flow': 2, 'reference-rail': 1 } as const
  const headline = page.querySelector('[data-testid="status-headline"]') as HTMLElement
  expect(headline).toBeTruthy()
  expect(headline.getAttribute('data-tier-weight')).toBe('status-header')
  expect(readingFlow.getAttribute('data-tier-weight')).toBe('reading-flow')
  expect(referenceRail.getAttribute('data-tier-weight')).toBe('reference-rail')

  const headlineWeight = tierWeight[headline.getAttribute('data-tier-weight') as keyof typeof tierWeight]
  const flowWeight = tierWeight[readingFlow.getAttribute('data-tier-weight') as keyof typeof tierWeight]
  const railWeight = tierWeight[referenceRail.getAttribute('data-tier-weight') as keyof typeof tierWeight]

  expect(headlineWeight).toBeGreaterThan(flowWeight)
  expect(flowWeight).toBeGreaterThan(railWeight)

  expect(headline.classList.contains('sticky')).toBe(true)
  expect(headline.getAttribute('data-sticky')).toBe('true')

  const allSticky = page.querySelectorAll('[data-sticky="true"]')
  expect(allSticky).toHaveLength(1)
  expect(allSticky[0]).toBe(headline)

  const grid = page.querySelector('[data-testid="issue-detail-content-grid"]') as HTMLElement
  expect(grid.className).toMatch(/lg:grid-cols-3/)
  expect(readingFlow.className).toMatch(/lg:col-span-2/)
  expect(referenceRail.className).toMatch(/lg:col-span-1/)

  for (const block of CONTROL_REGION_BLOCKS) {
    const el = page.querySelector(`[data-testid="${block}"]`)
    if (!el) continue
    expect(headerTier.contains(el)).toBe(true)
    expect(readingFlow.contains(el)).toBe(false)
    expect(referenceRail.contains(el)).toBe(false)
  }

  for (const block of READING_FLOW_BLOCKS) {
    const el = page.querySelector(`[data-testid="${block}"]`)
    if (!el) continue
    expect(readingFlow.contains(el)).toBe(true)
    expect(headerTier.contains(el)).toBe(false)
  }

  for (const block of REFERENCE_RAIL_BLOCKS) {
    const el = page.querySelector(`[data-testid="${block}"]`)
    if (!el) continue
    expect(referenceRail.contains(el)).toBe(true)
    expect(headerTier.contains(el)).toBe(false)
    expect(readingFlow.contains(el)).toBe(false)
  }
}

function expectNoNewActionKinds(container: HTMLElement) {
  const KNOWN_TESTIDS = new Set(LIFECYCLE_ACTIONS.map((kind) => `runtime-action-${kind}`))
  const runtimeActions = container.querySelectorAll('[data-testid^="runtime-action-"]')
  for (const node of Array.from(runtimeActions)) {
    expect(KNOWN_TESTIDS.has(node.getAttribute('data-testid') ?? '')).toBe(true)
  }
  expect(container.querySelector('[data-testid="runtime-action-rebase"]')).toBeNull()
}
describe('Control workspace: queued/backlog path', () => {
  it('shows identity and queued situation without fabricated stage, progress, or runtime actions', async () => {
    mockIssue(makeIssue({
      number: 14,
      title: 'Backlog draft test',
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      isDraft: true,
      canStart: false,
      blocker: { kind: 'draft' },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('status-headline'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const headline = screen.getByTestId('status-headline')
    expect(headline.getAttribute('data-summary')).toBe('queued')
    expect(within(headline).queryByTestId('status-headline-stage-progress')).toBeNull()
    expect(within(headline).queryByTestId('status-headline-current-task')).toBeNull()

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('queued')
    expect(within(surface).queryByTestId('runtime-action-approve')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-stop')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-retry')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-resume')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-rerun')).toBeNull()

    const startBtn = within(surface).getByTestId('runtime-action-start')
    expect(startBtn).toBeTruthy()
    expect((startBtn as HTMLButtonElement).disabled).toBe(true)

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(screen.getByTestId('draft-pill'))).toBe(true)
  })

  it('shows runner-unavailable / capacity-full as the in-surface runner gating signal in the control region', async () => {
    mockIssue(makeIssue({
      status: 'backlog',
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: 'active',
      canStart: true,
      blocker: null,
    }))
    mockAgentStatus({
      activeAgents: [],
      capacity: { active: 2, max: 2 },
      runnerAvailable: true,
    })

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))

    const surface = screen.getByTestId('runtime-decision-surface')
    const signal = within(surface).getByTestId('runtime-execution-signal')
    const runner = within(signal).getByTestId('runtime-execution-signal-runner')
    expect(runner.getAttribute('data-gating-kind')).toBe('capacity-full')

    const startBtn = within(surface).getByTestId('runtime-action-start')
    expect((startBtn as HTMLButtonElement).disabled).toBe(true)
    expect(startBtn.getAttribute('title') ?? '').toMatch(/capacity is full/i)

    await assertControlWorkspaceBasics(() => container as HTMLElement)
  })
})

describe('Control workspace: done path (non-archived)', () => {
  it('shows the terminal Done state with no start/stop/approve/retry/resume/rerun actions offered', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      health: 'done',
      workflowRunId: 'wr_done_1',
      archivedAt: undefined,
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: ['inspect'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('status-headline'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const headline = screen.getByTestId('status-headline')
    expect(headline.getAttribute('data-summary')).toBe('done')

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('done')

    expect(within(surface).queryByTestId('runtime-action-start')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-stop')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-approve')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-retry')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-resume')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-rerun')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-send-back')).toBeNull()
  })
})

describe('Control workspace: archived done path', () => {
  it('shows archived banner, identity, terminal Done state, and no active workflow controls', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      health: 'done',
      workflowRunId: 'wr_arch_1',
      archivedAt: '2026-06-25T10:00:00Z',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: ['inspect'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('status-headline'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(screen.getByTestId('archived-banner'))).toBe(true)
    expect(headerTier.contains(screen.getByTestId('archived-pill'))).toBe(true)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('done')

    expect(screen.queryByTestId('runtime-action-start')).toBeNull()
    expect(screen.queryByTestId('runtime-action-stop')).toBeNull()
    expect(screen.queryByTestId('runtime-action-approve')).toBeNull()
    expect(screen.queryByTestId('runtime-action-retry')).toBeNull()
    expect(screen.queryByTestId('runtime-action-resume')).toBeNull()
    expect(screen.queryByTestId('runtime-action-rerun')).toBeNull()

    expect(screen.getByTestId('back-to-archived')).toBeTruthy()
    expect(screen.queryByTestId('back-to-board')).toBeNull()
  })

  it('preserves archived description and comments in reading-flow even when no runtime actions are offered', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: 'done',
      workflowStage: 'done',
      workflowStatus: 'completed',
      health: 'done',
      workflowRunId: 'wr_arch_2',
      archivedAt: '2026-06-25T10:00:00Z',
      body: 'Archived body content the operator must still be able to read.',
      comments: [
        {
          id: 'c-arch-1',
          author: 'tester',
          body: 'Archived reviewer comment.',
          createdAt: '2026-06-25T11:00:00Z',
        },
      ],
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'completed',
        workflowSummaryState: 'completed',
        allowedActions: ['inspect'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('status-headline'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)

    const description = screen.getByTestId('description-section')
    const comments = screen.getByTestId('comments-section')

    expect(description.textContent ?? '').toContain('Archived body content')
    expect(comments.textContent ?? '').toContain('Archived reviewer comment')

    const headerTier = screen.getByTestId('status-header-tier')
    expect(headerTier.contains(description)).toBe(false)
    expect(headerTier.contains(comments)).toBe(false)
  })
})

describe('Control workspace: secondary content placement', () => {
  it('keeps description, comments, model selection, and prerequisites below the control region', async () => {
    mockIssue(makeIssue({
      body: 'A long descriptive body for the test.',
      model: 'sonnet',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      prerequisites: [
        { number: 9, title: 'Prerequisite issue', completed: true, status: 'done', health: 'done' },
      ],
      comments: [
        {
          id: 'c-1',
          author: 'tester',
          body: 'A reviewer comment.',
          createdAt: '2026-01-02T00:00:00Z',
        },
      ],
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('status-headline'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)

    const headerTier = screen.getByTestId('status-header-tier')
    const rail = screen.getByTestId('reference-rail')

    const description = screen.getByTestId('description-section')
    expect(description.closest('[data-testid="status-header-tier"]')).toBeNull()
    expect(headerTier.contains(description)).toBe(false)
    expect(container.contains(description)).toBe(true)

    const comments = screen.getByTestId('comments-section')
    expect(headerTier.contains(comments)).toBe(false)
    expect(container.contains(comments)).toBe(true)

    const configCard = screen.getByTestId('reference-rail-configuration')
    expect(rail.contains(configCard)).toBe(true)
    expect(headerTier.contains(configCard)).toBe(false)

    const prereqCard = screen.getByTestId('reference-rail-prerequisites')
    expect(rail.contains(prereqCard)).toBe(true)
    expect(headerTier.contains(prereqCard)).toBe(false)

    const modelRow = container.querySelector('[data-testid="issue-detail-details-metadata"]')
    if (modelRow) {
      expect(rail.contains(modelRow)).toBe(true)
      expect(headerTier.contains(modelRow)).toBe(false)
    }
  })

  it('renders description and comments after the runtime-decision-surface in document order', async () => {
    mockIssue(makeIssue({
      body: 'A long descriptive body for the test.',
      comments: [
        {
          id: 'c-1',
          author: 'tester',
          body: 'A reviewer comment.',
          createdAt: '2026-01-02T00:00:00Z',
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

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const description = await waitFor(() => screen.getByTestId('description-section'))
    const comments = await waitFor(() => screen.getByTestId('comments-section'))

    const surfaceToDescription = surface.compareDocumentPosition(description)
    expect(surfaceToDescription & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)

    const descriptionToComments = description.compareDocumentPosition(comments)
    expect(descriptionToComments & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})

describe('Control workspace: lifecycle action preservation', () => {
  it('does not introduce new action kinds outside the existing set (start, approve, send-back, retry, resume, rerun, stop, inspect)', async () => {
    const scenarios: Array<{ label: string; overrides: Record<string, unknown> }> = [
      {
        label: 'running build',
        overrides: {
          status: 'in_progress',
          workflowStage: 'build',
          workflowStatus: 'running',
          health: 'active',
          recovery: {
            currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
            latestAttemptState: 'running',
            workflowSummaryState: 'running',
            allowedActions: ['stop'],
          },
        },
      },
      {
        label: 'approval-required',
        overrides: {
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
        },
      },
      {
        label: 'blocked',
        overrides: {
          status: 'in_progress',
          workflowStage: 'build',
          workflowStatus: 'interrupted',
          health: 'interrupted',
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'interrupted',
            workflowSummaryState: 'interrupted',
            allowedActions: ['retry', 'resume', 'rerun', 'stop'],
          },
        },
      },
      {
        label: 'failed',
        overrides: {
          status: 'in_progress',
          workflowStage: 'build',
          workflowStatus: 'failed',
          health: 'active',
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'failed',
            workflowSummaryState: 'failed',
            allowedActions: ['retry', 'resume', 'rerun', 'start', 'stop'],
          },
        },
      },
      {
        label: 'queued backlog',
        overrides: {
          status: 'backlog',
          workflowStage: null,
          workflowStatus: null,
          workflowRunId: null,
          canStart: true,
          blocker: null,
          recovery: undefined,
        },
      },
      {
        label: 'done',
        overrides: {
          status: 'done',
          workflowStage: 'done',
          workflowStatus: 'completed',
          health: 'done',
          recovery: {
            currentWorkItem: null,
            latestAttemptState: 'completed',
            workflowSummaryState: 'completed',
            allowedActions: ['inspect'],
          },
        },
      },
    ]

    for (const scenario of scenarios) {
      mockIssue(makeIssue(scenario.overrides))
      const { container } = renderPage()

      await waitFor(() => screen.getByTestId('runtime-decision-surface'))

      const actions = container.querySelectorAll('[data-testid^="runtime-action-"]')
      const knownActions = new Set(
        LIFECYCLE_ACTIONS.map((kind) => `runtime-action-${kind}`),
      )
      for (const node of Array.from(actions)) {
        expect(
          knownActions.has(node.getAttribute('data-testid') ?? ''),
          `scenario "${scenario.label}" produced an unknown runtime action test-id: ${node.getAttribute('data-testid')}`,
        ).toBe(true)
      }

      expect(container.querySelector('[data-testid="runtime-action-rebase"]')).toBeNull()
      expect(container.querySelector('[data-testid="runtime-action-close"]')).toBeNull()
      expect(container.querySelector('[data-testid="runtime-action-archive"]')).toBeNull()
      expect(container.querySelector('[data-testid="runtime-action-mark-ready"]')).toBeNull()

      cleanup()
      vi.unstubAllGlobals()
    }
  })

  it('preserves the close/archive/mark-ready actions in the reference-rail Actions card without leaking them into runtime-decision-surface', async () => {
    mockIssue(makeIssue({
      isDraft: true,
      canStart: false,
      blocker: { kind: 'draft' },
      recovery: undefined,
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))

    const rail = screen.getByTestId('reference-rail')
    expect(rail.contains(screen.getByTestId('reference-rail-actions'))).toBe(true)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.querySelector('[data-testid="mark-ready-button"]')).toBeNull()
    expect(surface.querySelector('[data-testid^="runtime-action-close"]')).toBeNull()
    expect(surface.querySelector('[data-testid^="runtime-action-archive"]')).toBeNull()
    expect(surface.querySelector('[data-testid="runtime-action-mark-ready"]')).toBeNull()

    expect(container.querySelector('[data-testid="mark-ready-button"]')).toBeTruthy()
  })

  it('keeps rebase surfaced through the dedicated drift-recovery slot rather than a runtime-action button', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowRunId: 'wr_drift_rebase',
      drift: {
        drifted: true,
        detectedAt: '2026-01-05T00:00:00Z',
        decision: 'needs-attention',
        baseBranch: 'master',
        branch: 'feature/issue-14',
      },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockWorkspaceStatus({
      exists: true,
      branch: 'feature/issue-14',
      baseBranch: 'master',
      ahead: 2,
      behind: 1,
      rebaseInProgress: false,
      conflictingFiles: [],
    })

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).getByTestId('runtime-drift-recovery')).toBeTruthy()

    const actionsRow = within(surface).getByTestId('runtime-actions')
    expect(actionsRow.querySelector('[data-testid="runtime-action-rebase"]')).toBeNull()

    expect(container.querySelector('[data-testid="runtime-action-rebase"]')).toBeNull()
  })
})

describe('Control workspace: stop confirmation', () => {
  it('shows the stop-confirmation-copy inside the surface after a stop click; preserves recovery actions afterwards', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowRunId: 'wr_stop',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop', 'force-stop'],
      },
    }))

    renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const stop = within(surface).getByTestId('runtime-action-stop')

    fireEvent.click(stop)

    const confirmation = await waitFor(() => within(surface).getByTestId('runtime-stop-confirmation-copy'))
    expect(confirmation.textContent ?? '').toMatch(/preserve progress/i)

    expect(within(surface).getByTestId('runtime-action-stop')).toBeTruthy()
  })
})

describe('Control workspace: action emphasis', () => {
  it('makes the primary valid action carry default-variant emphasis while secondary actions use outline variant', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'check',
      health: 'paused',
      workflowRunId: 'wr_emphasis',
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

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const approveBtn = within(surface).getByTestId('runtime-action-approve')
    const sendBackBtn = within(surface).getByTestId('runtime-action-send-back')

    expect(approveBtn.getAttribute('data-primary')).toBe('true')
    expect(approveBtn.className).toMatch(/bg-primary/)

    expect(sendBackBtn.getAttribute('data-primary')).toBe('false')
  })
})
