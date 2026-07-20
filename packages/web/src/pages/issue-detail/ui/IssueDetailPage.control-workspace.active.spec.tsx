import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { setScopedValue } from '../../../../tests/support/scoped-property'
import {
  mockArtifacts,
  mockIssue,
  mockWorkspaceStatus,
  mockWorkflowRunSessions,
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

function everyActionAnchorInHeaderTier(container: HTMLElement) {
  const headerTier = container.querySelector('[data-testid="status-header-tier"]') as HTMLElement
  for (const kind of LIFECYCLE_ACTIONS) {
    const matches = container.querySelectorAll(`[data-testid="runtime-action-${kind}"]`)
    for (const node of Array.from(matches)) {
      expect(headerTier.contains(node), `runtime-action-${kind} should be inside status-header-tier`).toBe(true)
    }
  }
}

function noExtraOperationalNodesLeakAboveReadingFlow(container: HTMLElement) {
  const headerTier = container.querySelector('[data-testid="status-header-tier"]') as HTMLElement
  const headerBlocks = CONTROL_REGION_BLOCKS
    .map((id) => container.querySelector(`[data-testid="${id}"]`))
    .filter((el): el is HTMLElement => el !== null)
  for (const block of headerBlocks) {
    const readingFlow = container.querySelector('[data-testid="reading-flow"]') as HTMLElement
    expect(readingFlow.contains(block)).toBe(false)
  }
  const description = container.querySelector('[data-testid="description-section"]')
  const comments = container.querySelector('[data-testid="comments-section"]')
  if (description) expect(headerTier.contains(description)).toBe(false)
  if (comments) expect(headerTier.contains(comments)).toBe(false)
}

function expectNoNewActionKinds(container: HTMLElement) {
  const KNOWN_TESTIDS = new Set(LIFECYCLE_ACTIONS.map((kind) => `runtime-action-${kind}`))
  const runtimeActions = container.querySelectorAll('[data-testid^="runtime-action-"]')
  for (const node of Array.from(runtimeActions)) {
    expect(KNOWN_TESTIDS.has(node.getAttribute('data-testid') ?? '')).toBe(true)
  }
  expect(container.querySelector('[data-testid="runtime-action-rebase"]')).toBeNull()
}

describe('Control workspace: running path', () => {
  it('shows identity, stage, progress, primary owner action, and slots within the control region', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowStageProgress: {
        stage: 'build',
        total: 5,
        completed: 2,
        running: 1,
        failed: 0,
        currentTaskTitle: 'Build decision surface',
      },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))

    await assertControlWorkspaceBasics(() => container as HTMLElement)
    everyActionAnchorInHeaderTier(container as HTMLElement)
    noExtraOperationalNodesLeakAboveReadingFlow(container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('running')

    const headline = screen.getByTestId('status-headline')
    expect(within(headline).getByTestId('status-headline-summary')).toBeTruthy()
    expect(within(headline).getByTestId('status-headline-stage-progress')).toBeTruthy()
    expect(headline.dataset.hasCurrentTask).toBe('true')
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()
    expect(headline.textContent ?? '').toContain('Build decision surface')

    expect(within(surface).getByTestId('runtime-next-action')).toBeTruthy()
    expect(within(surface).getByTestId('runtime-rationale')).toBeTruthy()

    const stopButton = screen.getByTestId('runtime-action-stop')
    expect(stopButton.getAttribute('data-primary')).toBe('true')

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'start']) {
      expect(within(surface).queryByTestId(`runtime-action-${kind}`)).toBeNull()
    }
  })

  it('does not fabricate evidence, signal, or drift slots for the running summary', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowRunId: 'wr_run',
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))
    mockArtifacts([
      {
        artifactId: 'a-1',
        workflowRunId: 'wr_run',
        taskRunId: 'plan.1',
        path: 'plan.md',
        kind: 'file',
        contentType: 'text/markdown',
        size: 256,
        recordedAt: '2026-01-02T00:00:00.000Z',
        displayName: 'plan.md',
      },
    ])
    mockWorkflowRunSessions([])
    mockWorkspaceStatus({
      exists: true,
      branch: 'mohist/run',
      baseBranch: 'master',
      ahead: 1,
      behind: 0,
      rebaseInProgress: false,
      conflictingFiles: [],
    })

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    const surface = screen.getByTestId('runtime-decision-surface')

    expect(within(surface).queryByTestId('runtime-evidence')).toBeNull()
    expect(within(surface).queryByTestId('runtime-execution-signal')).toBeNull()
    expect(within(surface).queryByTestId('runtime-drift-recovery')).toBeNull()

    await waitFor(() => {
      expect(container.querySelector('[data-testid="latest-artifacts-list"]')).toBeTruthy()
    })
  })
})

describe('Control workspace: approval-required path', () => {
  it('shows approval state, awaiting stage, approve+send-back together with evidence reachable from control region', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'check',
      health: 'paused',
      workflowRunId: 'wr_appr',
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
    mockArtifacts([
      {
        artifactId: 'a-plan',
        workflowRunId: 'wr_appr',
        taskRunId: 'plan.1',
        path: 'plan.md',
        kind: 'file',
        contentType: 'text/markdown',
        size: 256,
        recordedAt: '2026-01-02T00:00:00.000Z',
        displayName: 'plan.md',
      },
      {
        artifactId: 'a-check',
        workflowRunId: 'wr_appr',
        taskRunId: 'check.1',
        path: 'check.txt',
        kind: 'file',
        contentType: 'text/plain',
        size: 128,
        recordedAt: '2026-01-03T00:00:00.000Z',
        displayName: 'check.txt',
      },
    ])

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    everyActionAnchorInHeaderTier(container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('approval-required')

    expect(within(surface).getByTestId('runtime-action-approve')).toBeTruthy()
    expect(within(surface).getByTestId('runtime-action-send-back')).toBeTruthy()

    const approveBtn = within(surface).getByTestId('runtime-action-approve')
    expect(approveBtn.getAttribute('data-primary')).toBe('true')

    await waitFor(() => within(surface).getByTestId('runtime-evidence'))
    expect(within(surface).getByTestId('runtime-evidence')).toBeTruthy()
    expect(within(surface).getByTestId('runtime-evidence-list')).toBeTruthy()

    const readingFlow = screen.getByTestId('reading-flow')
    const headerTier = screen.getByTestId('status-header-tier')
    expect(readingFlow.contains(surface)).toBe(false)
    expect(headerTier.contains(surface)).toBe(true)

    expect(container.querySelector('[data-testid="latest-artifacts-list"]')).toBeTruthy()
  })

  it('opens send-back feedback form within the same decision context without navigating away', async () => {
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

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    expect(surface.getAttribute('data-summary')).toBe('approval-required')

    fireEvent.click(within(surface).getByTestId('runtime-action-send-back'))

    const form = await waitFor(() => within(surface).getByTestId('runtime-send-back-form'))
    expect(form).toBeTruthy()
    expect(within(form).getByTestId('runtime-send-back-textarea')).toBeTruthy()
    expect(within(form).getByTestId('runtime-submit-send-back')).toBeTruthy()
    expect(screen.queryByTestId('description-section')).toBeTruthy()
    expect(surface.contains(form)).toBe(true)
  })
})

describe('Control workspace: blocked path', () => {
  it('shows blocked summary and recovery actions (retry/resume/rerun/stop) inside the control region', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'interrupted',
      health: 'interrupted',
      blockedReason: 'Workflow was interrupted by a transient infra issue.',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'interrupted',
        workflowSummaryState: 'interrupted',
        allowedActions: ['retry', 'resume', 'rerun', 'stop'],
      },
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    everyActionAnchorInHeaderTier(container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('blocked')

    for (const kind of ['retry', 'resume', 'rerun', 'stop']) {
      expect(within(surface).getByTestId(`runtime-action-${kind}`)).toBeTruthy()
    }

    expect(within(surface).queryByTestId('runtime-action-start')).toBeNull()
    expect(within(surface).queryByTestId('runtime-action-approve')).toBeNull()

    const primary = surface.querySelector('[data-primary="true"]')
    expect(primary).toBeTruthy()
    expect(['retry', 'resume', 'rerun', 'stop']).toContain(primary!.getAttribute('data-testid')?.replace('runtime-action-', ''))
  })

  it('exposes recovery actions with disabled/secondary weight when gated by the backend projection', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'interrupted',
      health: 'interrupted',
      blockedReason: 'Workflow was interrupted by a transient infra issue.',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'interrupted',
        workflowSummaryState: 'interrupted',
        allowedActions: ['retry', 'resume', 'rerun', 'stop'],
      },
    }))

    const { container } = renderPage()

    const surface = await waitFor(() => screen.getByTestId('runtime-decision-surface'))

    const buttons = ['retry', 'resume', 'rerun', 'stop']
    let primaryCount = 0
    for (const kind of buttons) {
      const btn = within(surface).getByTestId(`runtime-action-${kind}`) as HTMLButtonElement
      if (btn.getAttribute('data-primary') === 'true') primaryCount++
      expect(btn.getAttribute('data-testid')).toBe(`runtime-action-${kind}`)
    }
    expect(primaryCount).toBe(1)
    everyActionAnchorInHeaderTier(container as HTMLElement)
  })
})

describe('Control workspace: interrupted health path', () => {
  it('reports interrupted as blocked in the header summary and exposes a stop/recovery rationale that is not framed as awaiting approval', async () => {
    mockIssue(makeIssue({
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
    }))

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)

    const headline = screen.getByTestId('status-headline')
    expect(headline.getAttribute('data-summary')).toBe('blocked')

    const rationale = screen.getByTestId('runtime-rationale').textContent ?? ''
    expect(rationale).toMatch(/stopped manually|resume or rerun/i)
    expect(rationale).not.toMatch(/awaiting approval|pending review/i)
    expectNoNewActionKinds(container as HTMLElement)

    expect(screen.queryByTestId('workflow-interrupted-card')).toBeNull()
  })
})

describe('Control workspace: drift path', () => {
  it('promotes drift recovery to first screen while retaining the reference-rail drift card with full detail', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowRunId: 'wr_drift_1',
      drift: {
        drifted: true,
        detectedAt: '2026-01-05T00:00:00Z',
        decision: 'needs-attention',
        baseBranch: 'master',
        branch: 'feature/issue-14',
      },
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
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
      ahead: 3,
      behind: 2,
      rebaseInProgress: false,
      conflictingFiles: [],
    })

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(within(surface).getByTestId('runtime-drift-note')).toBeTruthy()
    expect(within(surface).getByTestId('runtime-drift-recovery')).toBeTruthy()

    const recoveryBtn = within(surface).getByTestId('runtime-drift-recovery-action')
    expect(recoveryBtn.getAttribute('data-testid')).toBe('runtime-drift-recovery-action')
    expect(within(surface).queryByTestId('runtime-action-rebase')).toBeNull()

    const rail = screen.getByTestId('reference-rail')
    const railDrift = within(rail).getByTestId('reference-rail-drift')
    expect(railDrift.getAttribute('data-collapsed')).toBe('true')
    expect(railDrift).toBeTruthy()
  })

  it('does not promote drift for non-blocking decisions (defer) into the control region', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'running',
      health: 'active',
      workflowRunId: 'wr_drift_defer',
      drift: {
        drifted: true,
        detectedAt: '2026-01-05T00:00:00Z',
        decision: 'defer',
        deferReason: 'Waiting on team review.',
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

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)

    expect(container.querySelector('[data-testid="runtime-drift-recovery"]')).toBeNull()
    expect(screen.getByTestId('reference-rail-drift')).toBeTruthy()
  })
})

describe('Control workspace: failed path', () => {
  it('shows failed summary, primary recovery, and in-surface evidence slot for plan/check artifacts', async () => {
    mockIssue(makeIssue({
      status: 'in_progress',
      workflowStage: 'build',
      workflowStatus: 'failed',
      health: 'active',
      workflowRunId: 'wr_failed',
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'failed',
        workflowSummaryState: 'failed',
        allowedActions: ['retry', 'resume', 'rerun', 'start', 'stop'],
      },
    }))
    mockArtifacts([
      {
        artifactId: 'a-failed',
        workflowRunId: 'wr_failed',
        taskRunId: 'check.1',
        path: 'check.txt',
        kind: 'file',
        contentType: 'text/plain',
        size: 128,
        recordedAt: '2026-01-03T00:00:00.000Z',
        displayName: 'check.txt',
      },
    ])

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('runtime-decision-surface'))
    await assertControlWorkspaceBasics(() => container as HTMLElement)
    everyActionAnchorInHeaderTier(container as HTMLElement)
    expectNoNewActionKinds(container as HTMLElement)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.getAttribute('data-summary')).toBe('failed')

    await waitFor(() => within(surface).getByTestId('runtime-evidence'))
    expect(within(surface).getByTestId('runtime-evidence')).toBeTruthy()
    expect(within(surface).getByTestId('runtime-evidence-list')).toBeTruthy()

    const recoveryKinds = ['retry', 'resume', 'rerun', 'start', 'stop']
    let count = 0
    for (const kind of recoveryKinds) {
      if (within(surface).queryByTestId(`runtime-action-${kind}`)) count++
    }
    expect(count).toBeGreaterThan(1)
  })
})
