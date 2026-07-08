// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueCard } from './IssueCard'
import { IssueStatus, IssueHealth, WorkflowStage, type Issue } from '../../../entities/issue'
import { getPriorityStripColor } from '../../../shared/lib/label-colors'
import type { AgentStatus } from '../../../entities/agent'

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    archiveIssue: vi.fn(),
    rerunIssue: vi.fn(),
    resumeIssue: vi.fn(),
  }
})

const mockAgentStatus: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-card-1',
    number: 201,
    title: 'Implement feature',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    priority: 'p2',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

function renderCard(issue: Issue) {
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <IssueCard issue={issue} agentStatus={mockAgentStatus} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('IssueCard - draft indicator and de-emphasis', () => {
  it('renders a Draft pill on the backlog card when isDraft is true', () => {
    const draft = makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } })
    renderCard(draft)
    expect(screen.getByTestId('draft-pill')).toBeInTheDocument()
    expect(screen.getByTestId('draft-pill')).toHaveTextContent('Draft')
  })

  it('marks the card with data-draft="true" when isDraft is true', () => {
    const draft = makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } })
    renderCard(draft)
    const card = screen.getByTestId('issue-card')
    expect(card).toHaveAttribute('data-draft', 'true')
  })

  it('visually de-emphasizes the draft card relative to ready backlog issues', () => {
    const draft = makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } })
    renderCard(draft)
    const card = screen.getByTestId('issue-card')
    expect(card.className).toMatch(/opacity-60/)
    expect(card.className).toMatch(/border-dashed/)
  })

  it('does not visually de-emphasize the ready backlog card', () => {
    const ready = makeIssue({ isDraft: false, canStart: true, blocker: null })
    renderCard(ready)
    const card = screen.getByTestId('issue-card')
    expect(card.className).not.toMatch(/opacity-60/)
    expect(card.className).not.toMatch(/border-dashed/)
  })

  it('does not render a Draft pill when isDraft is false', () => {
    const ready = makeIssue({ isDraft: false, canStart: true, blocker: null })
    renderCard(ready)
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
  })

  it('reads draft state from isDraft, not from labels or title', () => {
    const readyWithDraftishLabel = makeIssue({
      isDraft: false,
      canStart: true,
      blocker: null,
      labels: { draft: 'true', stage: 'wip' },
      title: 'Draft idea placeholder',
    })
    renderCard(readyWithDraftishLabel)
    expect(screen.queryByTestId('draft-pill')).not.toBeInTheDocument()
    expect(screen.getByTestId('issue-card')).not.toHaveAttribute('data-draft', 'true')

    cleanup()

    const draftWithReadyishLabel = makeIssue({
      isDraft: true,
      canStart: false,
      blocker: { kind: 'draft' },
      labels: { status: 'ready-to-go' },
      title: 'Ready to ship',
    })
    renderCard(draftWithReadyishLabel)
    expect(screen.getByTestId('draft-pill')).toBeInTheDocument()
  })
})

describe('IssueCard - blocker rendering for waiting-for issues', () => {
  it('renders a Waiting for #N reason when blocker.kind is waiting-for', () => {
    const waiting = makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    })
    renderCard(waiting)
    const reason = screen.getByTestId('blocker-reason')
    expect(reason).toHaveTextContent('Waiting for #200')
  })

  it('does not render a Waiting reason when blocker is null', () => {
    const ready = makeIssue({ isDraft: false, canStart: true, blocker: null })
    renderCard(ready)
    expect(screen.queryByTestId('blocker-reason')).not.toBeInTheDocument()
  })

  it('does not render a Waiting reason for a draft card even when blocker would otherwise indicate waiting', () => {
    const draft = makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } })
    renderCard(draft)
    expect(screen.queryByTestId('blocker-reason')).not.toBeInTheDocument()
    expect(screen.getByTestId('draft-pill')).toBeInTheDocument()
  })

  it('still hides the Waiting reason when the issue is cancelled', () => {
    const waitingButCancelled = makeIssue({
      status: IssueStatus.Cancelled,
      health: IssueHealth.Cancelled,
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    })
    renderCard(waitingButCancelled)
    expect(screen.queryByTestId('blocker-reason')).not.toBeInTheDocument()
  })

  it('routes blocked reason text through the danger token and no raw red palette', () => {
    const blocked = makeIssue({
      health: IssueHealth.Blocked,
      blockedReason: 'Waiting on external dependency',
    })
    renderCard(blocked)
    const reason = screen.getByTestId('blocked-reason')
    expect(reason.className).toContain('text-danger')
    expect(reason.className).not.toContain('text-red-')
  })

  it('routes waiting-for blocker text through the warning token and no raw amber palette', () => {
    const waiting = makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    })
    renderCard(waiting)
    const reason = screen.getByTestId('blocker-reason')
    expect(reason.className).toContain('text-warning')
    expect(reason.className).not.toContain('text-amber-')
  })
})

describe('IssueCard - no legacy startEligibility fields rendered', () => {
  it('does not render anything with startEligibility or waitingForDelivery in the DOM', () => {
    const draft = makeIssue({ isDraft: true, canStart: false, blocker: { kind: 'draft' } })
    const waiting = makeIssue({
      isDraft: false,
      canStart: false,
      blocker: { kind: 'waiting-for', issue: { number: 200, title: 'Foundational work' } },
    })
    const ready = makeIssue({ isDraft: false, canStart: true, blocker: null })

    const { container } = renderCard(draft)
    renderCard(waiting)
    renderCard(ready)

    const html = container.innerHTML
    expect(html).not.toMatch(/startEligibility/i)
    expect(html).not.toMatch(/waitingForDelivery/i)
    expect(screen.queryAllByTestId('issue-card').length).toBeGreaterThanOrEqual(3)
  })
})

describe('IssueCard - workflow profile is hover-only with inspectable data hook', () => {
  it('exposes the workflow profile via a data hook on a screen-reader-only node', () => {
    const issue = makeIssue({ workflowProfileId: 'mohist/github-pr' })
    renderCard(issue)

    const hook = screen.getByTestId('issue-card-workflow-profile')
    expect(hook).toBeInTheDocument()
    expect(hook.dataset.workflowProfile).toBe('mohist/github-pr')
  })

  it('falls back to the inherited default workflow profile in the data hook when the read model is null', () => {
    const issue = makeIssue({ workflowProfileId: null })
    renderCard(issue)

    const hook = screen.getByTestId('issue-card-workflow-profile')
    expect(hook.dataset.workflowProfile).toBe('mohist/local')
  })

  it('does not render the workflow profile string as visible text in the default top row', () => {
    const issue = makeIssue({ workflowProfileId: 'mohist/github-pr' })
    const { container } = renderCard(issue)

    const visibleNodesWithProfile = Array.from(container.querySelectorAll('span')).filter(
      (node) => !node.classList.contains('sr-only') && node.textContent?.includes('mohist/github-pr'),
    )
    expect(visibleNodesWithProfile).toHaveLength(0)
  })

  it('makes the workflow profile value available as a hover hint via the issue-number title', () => {
    const issue = makeIssue({ workflowProfileId: 'mohist/github-pr' })
    renderCard(issue)

    const number = screen.getByTestId('issue-number')
    expect(number).toHaveAttribute('title', 'Workflow profile: mohist/github-pr')
  })
})

function classSet(className: string): Set<string> {
  return new Set(className.split(/\s+/).filter(Boolean))
}

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4'] as const

describe('IssueCard - left color strip is priority-driven', () => {
  it.each(['p0', 'p1', 'p2', 'p3', 'p4'] as const)(
    'applies getPriorityStripColor(priority) as the left strip class (priority=%s)',
    (priority) => {
      const issue = makeIssue({ priority })
      renderCard(issue)

      const card = screen.getByTestId('issue-card')
      const expected = getPriorityStripColor(priority).split(/\s+/)
      const actual = classSet(card.className)
      for (const cls of expected) {
        expect(actual.has(cls)).toBe(true)
      }
    },
  )

  it('falls back to the gray priority strip class when priority is null', () => {
    const issue = makeIssue({ priority: null })
    renderCard(issue)

    const card = screen.getByTestId('issue-card')
    const expected = getPriorityStripColor(null).split(/\s+/)
    const actual = classSet(card.className)
    for (const cls of expected) {
      expect(actual.has(cls)).toBe(true)
    }
  })

  it('does not apply inline hex color or inline style on the priority strip', () => {
    const issue = makeIssue({ priority: 'p1' })
    renderCard(issue)

    const card = screen.getByTestId('issue-card') as HTMLElement
    expect(card.style.borderLeftColor).toBe('')
    expect(card.getAttribute('style') ?? '').not.toMatch(/#[0-9a-f]{3,8}/i)
  })

  it('renders distinct strip class sets for distinct priorities', () => {
    const rendered: Record<string, string> = {}
    for (const priority of ['p0', 'p1', 'p2', 'p3', 'p4'] as const) {
      const issue = makeIssue({ priority, number: 200 + parseInt(priority.slice(1), 10) })
      const { unmount } = renderCard(issue)
      const card = screen.getByTestId('issue-card')
      rendered[priority] = card.className
      unmount()
      cleanup()
    }
    const stripParts = (className: string) =>
      className
        .split(/\s+/)
        .filter((c) => c.startsWith('border-l-') || c.startsWith('dark:border-l-'))
        .join('|')
    const uniqueStripColors = new Set(PRIORITIES.map((p) => stripParts(rendered[p])))
    expect(uniqueStripColors.size).toBe(5)
  })

  it('does not derive the strip class from labels (no type labels still produces a colored strip)', () => {
    const issue = makeIssue({ priority: 'p1', labels: {} })
    renderCard(issue)

    const card = screen.getByTestId('issue-card')
    const stripClasses = card.className
      .split(/\s+/)
      .filter((c) => c.startsWith('border-l-') || c.startsWith('dark:border-l-'))
    const expectedP1 = getPriorityStripColor('p1').split(/\s+/)
    const expectedP4 = getPriorityStripColor('p4').split(/\s+/)
    for (const cls of expectedP1) {
      expect(stripClasses).toContain(cls)
    }
    for (const cls of expectedP4) {
      expect(stripClasses).not.toContain(cls)
    }
  })
})

describe('IssueCard - default top row keeps only essential elements', () => {
  it('renders the issue number, priority chip, and at most one status pill in the top row', () => {
    const issue = makeIssue({ priority: 'p2' })
    const { container } = renderCard(issue)

    expect(screen.getByTestId('issue-number')).toBeInTheDocument()
    expect(screen.getByTestId('priority-chip')).toBeInTheDocument()

    const topRow = container.querySelector('[data-testid="issue-card"] > div > div')
    expect(topRow).toBeTruthy()
    const visibleStatusPills = topRow!.querySelectorAll('[data-testid="status-pill"]')
    expect(visibleStatusPills.length).toBeLessThanOrEqual(1)
  })

  it('does not render the workflow profile as visible text inside the issue card', () => {
    const issue = makeIssue({ workflowProfileId: 'mohist/github-pr', priority: 'p1' })
    const { container } = renderCard(issue)

    const visibleNodesWithProfile = Array.from(container.querySelectorAll('span')).filter(
      (node) => !node.classList.contains('sr-only') && node.textContent?.includes('mohist/github-pr'),
    )
    expect(visibleNodesWithProfile).toHaveLength(0)
  })
})

function renderCardWithAgent(issue: Issue, agentStatus: AgentStatus) {
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <IssueCard issue={issue} agentStatus={agentStatus} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function runningAgentStatusFor(issueNumber: number): AgentStatus {
  return {
    running: true,
    issueId: null,
    issueNumber,
    activeAgents: [{ issueId: 'i1', issueNumber, projectId: 'proj-1' }],
    capacity: { active: 1, max: 2 },
  }
}

describe('IssueCard - stage folds into StatusPill instead of stacking', () => {
  it('does not render an independent stage badge when a running status pill is present', () => {
    const runningIssue = makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: { stage: 'build', total: 5, completed: 2, running: 1, failed: 0 },
    })
    renderCardWithAgent(runningIssue, runningAgentStatusFor(runningIssue.number))

    expect(screen.getByTestId('status-pill')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-stage-badge')).not.toBeInTheDocument()
  })

  it('folds the stage label and progress into the status pill (Running · Build 2/5)', () => {
    const runningIssue = makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: { stage: 'build', total: 5, completed: 2, running: 1, failed: 0 },
    })
    renderCardWithAgent(runningIssue, runningAgentStatusFor(runningIssue.number))

    const pill = screen.getByTestId('status-pill')
    expect(pill).toHaveTextContent('Running · Build 2/5')
  })

  it('does not render a standalone progress indicator when folded into the status pill', () => {
    const runningIssue = makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: { stage: 'build', total: 5, completed: 2, running: 1, failed: 0 },
    })
    renderCardWithAgent(runningIssue, runningAgentStatusFor(runningIssue.number))

    expect(screen.queryByTestId('workflow-stage-progress')).not.toBeInTheDocument()
  })

  it('still renders the stage pill standalone when no status pill applies', () => {
    const standing = makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Plan,
      health: IssueHealth.Active,
    })
    renderCard(standing)

    expect(screen.queryByTestId('status-pill')).not.toBeInTheDocument()
    expect(screen.getByTestId('workflow-stage-badge')).toBeInTheDocument()
  })

  it.each([
    [
      'blocked',
      IssueHealth.Blocked,
      WorkflowStage.Build,
      'Blocked · Build 2/5',
    ] as const,
    [
      'approval',
      IssueHealth.Active,
      WorkflowStage.Plan,
      'Approval · Plan 2/5',
    ] as const,
    [
      'drift',
      IssueHealth.Active,
      WorkflowStage.Check,
      'Drift · Check 2/5',
    ] as const,
    [
      'waiting',
      IssueHealth.Active,
      WorkflowStage.Integrate,
      'Waiting · Integrate 2/5',
    ] as const,
  ])(
    'folds the stage label into the %s status pill (%s)',
    (variant, health, stage, expected) => {
      const issue: Issue = makeIssue({
        health,
        workflowStage: stage,
        workflowStageProgress: { stage: stage, total: 5, completed: 2, running: 0, failed: 0 },
      })
      if (variant === 'approval') {
        issue.approvalState = { stage: WorkflowStage.Plan, status: 'awaiting', requestedAt: '2026-01-01T00:00:00Z' }
      } else if (variant === 'waiting') {
        issue.blocker = { kind: 'waiting-for', issue: { number: 200, title: 'Other' } }
      } else if (variant === 'drift') {
        issue.drift = {
          drifted: true,
          decision: 'needs-attention',
          safeWindow: null,
          deferReason: null,
          observedBaseSha: null,
          currentBaseSha: null,
          candidateHeadSha: null,
          mergeBaseSha: null,
          conflicts: null,
          nextAction: null,
        }
      }
      renderCard(issue)
      expect(screen.getByTestId('status-pill')).toHaveTextContent(expected)
    },
  )

  it('omits the progress numeric when stage progress is absent', () => {
    const issue = makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: null,
    })
    renderCardWithAgent(issue, runningAgentStatusFor(issue.number))

    const pill = screen.getByTestId('status-pill')
    expect(pill).toHaveTextContent('Running · Build')
    expect(pill.textContent).not.toMatch(/\d+\/\d+/)
  })
})

describe('IssueCard - per-card text meets WCAG AA contrast', () => {
  it('renders the issue number with full-opacity muted-foreground text', () => {
    const issue = makeIssue({ priority: 'p2' })
    renderCard(issue)

    const number = screen.getByTestId('issue-number')
    expect(number.className).not.toMatch(/text-muted-foreground\/70/)
    expect(number.className).toMatch(/text-muted-foreground\b/)
  })

  it('renders the timestamp with full-opacity muted-foreground text when present', () => {
    const issue = makeIssue({ updatedAt: '2026-06-01T00:00:00Z' })
    const { container } = renderCard(issue)

    const timestamp = container.querySelector('span.text-\\[10px\\].text-muted-foreground')
    expect(timestamp).toBeTruthy()
    expect(timestamp!.className).not.toMatch(/text-muted-foreground\/70/)
    expect(timestamp!.className).toMatch(/text-muted-foreground\b/)
  })
})
