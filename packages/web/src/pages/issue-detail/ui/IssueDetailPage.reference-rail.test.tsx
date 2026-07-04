// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'

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
    patchIssueWorkflowDefinitionVar: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
    patchIssueWorkflowStageDefinitionVar: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
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
      data-enabled={enabledString(props.enabled)}
    />
  )),
}))

function enabledString(enabled: boolean | undefined): string {
  return enabled === undefined ? '' : String(enabled)
}

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

function mockMatchMedia(narrow: boolean) {
  let matches = narrow
  const listeners = new Set<(event: MediaQueryListEvent) => void>()
  const mql = {
    get matches() {
      return matches
    },
    media: '(max-width: 1023.98px)',
    addEventListener: vi.fn((_event: string, listener: (event: MediaQueryListEvent) => void) => {
      listeners.add(listener)
    }),
    removeEventListener: vi.fn((_event: string, listener: (event: MediaQueryListEvent) => void) => {
      listeners.delete(listener)
    }),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
    onchange: null,
  }
  vi.stubGlobal('matchMedia', vi.fn(() => mql))
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: narrow ? 375 : 1280 })
  return {
    setNarrow(next: boolean) {
      matches = next
      Object.defineProperty(window, 'innerWidth', { configurable: true, value: next ? 375 : 1280 })
      const event = { matches, media: mql.media } as MediaQueryListEvent
      for (const listener of listeners) listener(event)
    },
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

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'issue-1',
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'in_progress',
    workflowStage: 'build',
    workflowStatus: 'running',
    health: 'active',
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

const DEFAULT_RECOVERY = {
  currentWorkItem: { type: 'task', id: 't1', title: 'Build decision surface' },
  latestAttemptState: 'running',
  workflowSummaryState: 'running',
  allowedActions: ['stop'],
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

describe('IssueDetailPage reference-rail — metadata and configuration only', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('exposes metadata, model, workflow-profile control, and prerequisites in the rail', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        prerequisites: [
          { number: 9, title: 'Prerequisite issue', completed: true },
        ],
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('desktop')

    expect(referenceRail.contains(screen.getByTestId('issue-detail-details-metadata'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('workflow-profile-editor-frame'))).toBe(true)

    const detailsToggle = screen.getByTestId('reference-rail-details-toggle')
    const profileToggle = screen.getByTestId('reference-rail-workflow-profile-toggle')
    const configurationToggle = screen.getByTestId('reference-rail-configuration-toggle')
    expect(referenceRail.contains(detailsToggle)).toBe(true)
    expect(referenceRail.contains(profileToggle)).toBe(true)
    expect(referenceRail.contains(configurationToggle)).toBe(true)
  })

  it('exposes the non-runtime IssueActionsCard in the rail and excludes the runtime decision surface', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        health: 'blocked',
        blockedReason: 'Blocked by runtime execution.',
        recovery: {
          ...DEFAULT_RECOVERY,
          latestAttemptState: 'interrupted',
          allowedActions: ['stop', 'retry', 'resume', 'rerun'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const actionsToggle = screen.getByTestId('reference-rail-actions-toggle')
    expect(referenceRail.contains(actionsToggle)).toBe(true)

    expect(referenceRail.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
    expect(referenceRail.textContent ?? '').not.toContain('Current:')
    expect(referenceRail.textContent ?? '').not.toContain('Build decision surface')
    expect(referenceRail.textContent ?? '').not.toContain('Blocked by runtime execution.')

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'stop', 'start']) {
      const action = referenceRail.querySelector(`[data-testid="runtime-action-${kind}"]`)
      expect(action).toBeNull()
    }
  })

  it('does not place workflow progress, outputs, changes/diff, commits, description, or comments in the rail', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        body: 'A description body that should not appear in the rail at all.',
        comments: [
          {
            id: 'c1',
            author: 'tester',
            body: 'A reviewer comment.',
            createdAt: '2026-01-04T00:00:00Z',
          },
        ],
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })
    mockUseIssueDiff.mockReturnValue({
      data: {
        available: true,
        reason: null,
        head: 'feature/issue-14',
        base: 'master',
        mergeBase: 'abc',
        ahead: 1,
        behind: 0,
        canFastForward: true,
        comparison: 'merge-base',
        summary: { filesChanged: 1, commits: 1, additions: 4, deletions: 1 },
        files: [],
      },
    })
    mockUseIssueCommits.mockReturnValue({
      data: {
        available: true,
        reason: null,
        head: 'feature/issue-14',
        base: 'master',
        mergeBase: 'abc',
        ahead: 1,
        behind: 0,
        canFastForward: true,
        comparison: 'merge-base',
        summary: { filesChanged: 1, commits: 1, additions: 4, deletions: 1 },
        commits: [],
      },
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.querySelector('[data-testid="workflow-view-frame"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="runtime-evidence-frame"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="diff-summary-banner"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="diff-files-section"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="commits-section"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="description-section"]')).toBeNull()
    expect(referenceRail.querySelector('[data-testid="comments-section"]')).toBeNull()
  })

  it('does not render the workflow profile editor in the reading flow', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')
    const editorFrame = screen.getByTestId('workflow-profile-editor-frame')

    expect(referenceRail.contains(editorFrame)).toBe(true)
    expect(readingFlow.contains(editorFrame)).toBe(false)
  })
})

describe('IssueDetailPage reference-rail — desktop right column', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('marks the rail as desktop mode and lays it out as a right column narrower than the reading flow', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('desktop')

    const readingFlow = screen.getByTestId('reading-flow')
    expectPreceding(readingFlow, referenceRail)

    expect(referenceRail.className).toMatch(/lg:col-span-1\b/)

    const railSpanMatch = referenceRail.className.match(/lg:col-span-(\d)/)
    const railSpan = railSpanMatch ? Number(railSpanMatch[1]) : 1
    const flowSpanMatch = readingFlow.className.match(/lg:col-span-(\d)/)
    const flowSpan = flowSpanMatch ? Number(flowSpanMatch[1]) : 0
    expect(railSpan).toBeLessThan(flowSpan)

    const grid = screen.getByTestId('issue-detail-content-grid')
    expect(grid.className).toMatch(/lg:grid-cols-3/)
  })
})

describe('IssueDetailPage reference-rail — narrow-screen collapsed sections', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(true)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('marks the rail as narrow mode and does not occupy a right column beside the reading flow', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('narrow')
    expect(referenceRail.className).not.toMatch(/lg:col-span-1\b/)

    fireEvent.click(screen.getByTestId('reference-rail-details-toggle'))
    expect(referenceRail.contains(screen.getByTestId('issue-detail-details-metadata'))).toBe(true)

    fireEvent.click(screen.getByTestId('reference-rail-workflow-profile-toggle'))
    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
  })

  it('renders all rail items as collapsed sections on a narrow viewport', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
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
        prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('narrow')

    const railItems = [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-actions',
      'reference-rail-prerequisites',
    ]
    for (const testId of railItems) {
      const card = screen.getByTestId(testId)
      expect(card.dataset.collapsed).toBe('true')
      expect(referenceRail.contains(card)).toBe(true)
    }
  })

  it('collapses expanded rail cards when the viewport changes from desktop to narrow', async () => {
    const viewport = mockMatchMedia(false)
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
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
        prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('desktop')
    expect(screen.getByTestId('reference-rail-details').dataset.collapsed).toBe('false')
    expect(screen.getByTestId('reference-rail-actions').dataset.collapsed).toBe('false')

    act(() => {
      viewport.setNarrow(true)
    })

    await waitFor(() => {
      expect(referenceRail.dataset.railMode).toBe('narrow')
    })

    for (const testId of [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-actions',
      'reference-rail-prerequisites',
    ]) {
      expect(screen.getByTestId(testId).dataset.collapsed).toBe('true')
    }
  })

  it('stacks rail items beneath the reading flow on a narrow viewport', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.compareDocumentPosition(referenceRail) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})

describe('IssueDetailPage reference-rail — low-frequency items collapsed by default', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('keeps the drift panel collapsed by default with its body absent', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const driftCard = await waitFor(() => screen.getByTestId('reference-rail-drift'))
    expect(driftCard.dataset.collapsed).toBe('true')
    expect(driftCard.querySelector('[data-testid="reference-rail-drift-body"]')).toBeNull()
    expect(screen.queryByRole('heading', { name: /Base Drift Detected/ })).toBeNull()
  })

  it('expands the drift panel only on a deliberate click', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const driftCard = await waitFor(() => screen.getByTestId('reference-rail-drift'))
    expect(driftCard.dataset.collapsed).toBe('true')

    const toggle = screen.getByTestId('reference-rail-drift-toggle')
    fireEvent.click(toggle)

    await waitFor(() => {
      expect(driftCard.dataset.collapsed).toBe('false')
    })
    expect(driftCard.querySelector('[data-testid="reference-rail-drift-body"]')).not.toBeNull()
    expect(within(driftCard).getByText('Needs Attention')).toBeTruthy()
  })

  it('keeps the convergence panel collapsed by default with its body absent', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        health: 'blocked',
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
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
    expect(screen.queryByText('Workflow Blocked')).toBeNull()
  })

  it('does not render an empty convergence rail card for blocked issues without convergence content', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        health: 'blocked',
        blockedReason: 'Runtime blocked without convergence payload.',
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('blocked')
    expect(screen.getByTestId('runtime-rationale').textContent ?? '').toContain('Runtime blocked without convergence payload.')
    expect(screen.queryByTestId('reference-rail-convergence')).toBeNull()
  })

  it('expands the convergence panel only on a deliberate click', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        health: 'blocked',
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
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')

    const toggle = screen.getByTestId('reference-rail-convergence-toggle')
    fireEvent.click(toggle)

    await waitFor(() => {
      expect(convergenceCard.dataset.collapsed).toBe('false')
    })
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).not.toBeNull()
  })

  it('keeps the drift panel collapsed on a narrow viewport until a deliberate click', async () => {
    mockMatchMedia(true)
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const driftCard = await waitFor(() => screen.getByTestId('reference-rail-drift'))
    expect(driftCard.dataset.collapsed).toBe('true')

    fireEvent.click(screen.getByTestId('reference-rail-drift-toggle'))

    await waitFor(() => {
      expect(driftCard.dataset.collapsed).toBe('false')
    })
  })
})

describe('IssueDetailPage reference-rail — lightest visual weight', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('ranks rail below reading flow and reading flow below status headline by data-tier-weight', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    const tierOrder = { 'status-header': 3, 'reading-flow': 2, 'reference-rail': 1 } as const
    const headlineWeight = tierOrder[headline.dataset.tierWeight as keyof typeof tierOrder]
    const flowWeight = tierOrder[readingFlow.dataset.tierWeight as keyof typeof tierOrder]
    const railWeight = tierOrder[referenceRail.dataset.tierWeight as keyof typeof tierOrder]
    expect(headlineWeight).toBeGreaterThan(flowWeight)
    expect(flowWeight).toBeGreaterThan(railWeight)
  })

  it('does not place sticky or heavy-fill chrome on the reference rail', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.querySelector('[data-sticky="true"]')).toBeNull()
    expect(referenceRail.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)
    expect(referenceRail.className).not.toMatch(/\bsticky\b/)
  })

  it('does not nest same-name CardSection chrome inside expanded rail cards', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const railCards = Array.from(referenceRail.querySelectorAll('[data-rail-card="collapsible"]'))
    expect(railCards.length).toBeGreaterThan(0)

    for (const card of railCards) {
      const body = card.querySelector('[data-testid$="-body"]')
      if (!body) continue
      const nestedSections = body.querySelectorAll('section.rounded-lg.border')
      expect(nestedSections).toHaveLength(0)
    }

    expect(within(screen.getByTestId('reference-rail-details')).queryByRole('heading', { name: 'Details' })).toBeNull()
    expect(within(screen.getByTestId('reference-rail-workflow-profile')).queryAllByText('Workflow Profile')).toHaveLength(1)
    expect(within(screen.getByTestId('reference-rail-actions')).queryByRole('heading', { name: 'Actions' })).toBeNull()
  })
})

const RAIL_CARD_TESTIDS = [
  'reference-rail-details',
  'reference-rail-workflow-profile',
  'reference-rail-drift',
  'reference-rail-convergence',
  'reference-rail-configuration',
  'reference-rail-actions',
  'reference-rail-prerequisites',
  'reference-rail-readiness',
] as const

const READING_FLOW_LAST_TESTIDS = [
  'comments-section',
  'description-section',
  'commits-section',
  'diff-files-section',
] as const

describe('IssueDetailPage reference-rail — document-order audit (narrow)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(true)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('places every rail card after every last reading-flow item in document order on a narrow viewport', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
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
        prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.dataset.railMode).toBe('narrow')

    const lastReadingFlowElement = READING_FLOW_LAST_TESTIDS
      .map((id) => screen.queryByTestId(id))
      .find((el): el is HTMLElement => el !== null)

    if (lastReadingFlowElement) {
      const referenceRailPos = lastReadingFlowElement.compareDocumentPosition(referenceRail)
      expect(referenceRailPos & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
    }

    for (const railTestId of RAIL_CARD_TESTIDS) {
      const railCard = screen.queryByTestId(railTestId)
      if (!railCard) continue
      for (const readingTestId of READING_FLOW_LAST_TESTIDS) {
        const readingEl = screen.queryByTestId(readingTestId)
        if (!readingEl) continue
        const relationship = readingEl.compareDocumentPosition(railCard)
        expect(
          relationship & Node.DOCUMENT_POSITION_FOLLOWING,
          `expected ${railTestId} to follow ${readingTestId} in document order on narrow viewport`,
        ).not.toBe(0)
      }
    }
  })

  it('places the rail container after the reading-flow container on narrow, not interleaved', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const readingFlow = await waitFor(() => screen.getByTestId('reading-flow'))
    const referenceRail = screen.getByTestId('reference-rail')

    expect(readingFlow.compareDocumentPosition(referenceRail) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0)
  })
})

describe('IssueDetailPage reference-rail — desktop restoration excludes mobile-only chrome', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('does not render MobileActionBar or ConfirmationDrawer in the DOM at desktop', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))

    expect(container.querySelector('[data-testid="mobile-action-bar"]')).toBeNull()
    expect(container.querySelector('[data-testid="confirmation-drawer"]')).toBeNull()
  })

  it('does not render MobileActionBar or ConfirmationDrawer on desktop even with a primary action', async () => {
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
          allowedActions: ['stop'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    const { container } = renderPage()

    await waitFor(() => screen.getByTestId('reference-rail'))

    expect(container.querySelector('[data-testid="mobile-action-bar"]')).toBeNull()
    expect(container.querySelector('[data-testid="confirmation-drawer"]')).toBeNull()
  })
})

describe('IssueDetailPage reference-rail — convergence panel collapsed on every viewport', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  function issueWithConvergence() {
    return makeIssue({
      health: 'blocked',
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
      recovery: DEFAULT_RECOVERY,
    })
  }

  it('keeps convergence collapsed by default on desktop', async () => {
    mockMatchMedia(false)
    mockUseIssue.mockReturnValue({ data: issueWithConvergence(), isLoading: false, isError: false })

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
  })

  it('keeps convergence collapsed by default on narrow', async () => {
    mockMatchMedia(true)
    mockUseIssue.mockReturnValue({ data: issueWithConvergence(), isLoading: false, isError: false })

    renderPage()

    const convergenceCard = await waitFor(() => screen.getByTestId('reference-rail-convergence'))
    expect(convergenceCard.dataset.collapsed).toBe('true')
    expect(convergenceCard.querySelector('[data-testid="reference-rail-convergence-body"]')).toBeNull()
  })
})

describe('IssueDetailPage reference-rail — rail contents exclusivity (full set of conditional cards)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('only renders rail cards from the allowed metadata/config/non-runtime action set', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
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
        prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    const expectedRailCards = [
      'reference-rail-details',
      'reference-rail-workflow-profile',
      'reference-rail-drift',
      'reference-rail-convergence',
      'reference-rail-configuration',
      'reference-rail-actions',
      'reference-rail-prerequisites',
    ]
    for (const testId of expectedRailCards) {
      expect(referenceRail.contains(screen.getByTestId(testId))).toBe(true)
    }
  })

  it('does not render rail cards outside the allowed metadata/config/non-runtime action set', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        drift: { drifted: true, detectedAt: '2026-01-05T00:00:00Z', decision: 'needs-attention' },
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
        prerequisites: [{ number: 9, title: 'Prerequisite issue', completed: false }],
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    const forbiddenTestIds = [
      'workflow-view-frame',
      'runtime-evidence-frame',
      'diff-files-section',
      'commits-section',
      'description-section',
      'comments-section',
      'runtime-decision-surface',
      'latest-artifacts-panel',
      'diff-summary-banner',
    ]
    for (const testId of forbiddenTestIds) {
      expect(
        referenceRail.querySelector(`[data-testid="${testId}"]`),
        `expected ${testId} not to be present in the reference rail`,
      ).toBeNull()
    }
  })

  it('renders only metadata, configuration, workflow-profile, and non-runtime actions on the rail (no runtime surface)', async () => {
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
          allowedActions: ['stop', 'retry', 'resume', 'rerun'],
        },
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))

    expect(referenceRail.contains(screen.getByTestId('issue-detail-details-metadata'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('issue-workflow-profile-control-frame'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('workflow-profile-editor-frame'))).toBe(true)
    expect(referenceRail.contains(screen.getByTestId('reference-rail-actions-toggle'))).toBe(true)

    for (const kind of ['approve', 'send-back', 'retry', 'resume', 'rerun', 'stop', 'start']) {
      expect(referenceRail.querySelector(`[data-testid="runtime-action-${kind}"]`)).toBeNull()
    }
    expect(referenceRail.querySelector('[data-testid="runtime-decision-surface"]')).toBeNull()
  })
})

describe('IssueDetailPage reference-rail — lightest visual weight', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('does not apply heavy-fill or shadow chrome to the rail container or its cards', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.className).not.toMatch(/shadow/)
    expect(referenceRail.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)

    const railCards = Array.from(referenceRail.querySelectorAll('[data-rail-card="collapsible"]'))
    expect(railCards.length).toBeGreaterThan(0)
    for (const card of railCards) {
      expect(card.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)
      expect(card.className).not.toMatch(/shadow/)
    }
  })

  it('uses muted text color on rail toggle buttons (lighter than the headline and reading flow)', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const detailsToggle = screen.getByTestId('reference-rail-details-toggle')
    expect(detailsToggle.className).toMatch(/text-muted-foreground/)
    expect(detailsToggle.className).not.toMatch(/text-foreground(\b|[^/])/)
    expect(referenceRail.contains(detailsToggle)).toBe(true)
  })
})
