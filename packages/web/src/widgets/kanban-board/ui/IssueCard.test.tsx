import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueCard } from './IssueCard'
import { IssueStatus, IssueHealth, WorkflowStage, type Issue } from '../../../entities/issue'
import { getPriorityStripColor } from '../../../shared/lib/label-colors'
import type { AgentStatus } from '../../../entities/agent'

const mockAgentStatus: AgentStatus = {
  running: false,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
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

function hexToRgbString(hex: string): string {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim())
  if (!m) throw new Error(`Invalid hex: ${hex}`)
  const v = m[1]!
  const r = parseInt(v.slice(0, 2), 16)
  const g = parseInt(v.slice(2, 4), 16)
  const b = parseInt(v.slice(4, 6), 16)
  return `rgb(${r}, ${g}, ${b})`
}

describe('IssueCard - left color strip is priority-driven', () => {
  it.each(['p0', 'p1', 'p2', 'p3', 'p4'] as const)(
    'uses getPriorityStripColor(priority) for the left strip border (priority=%s)',
    (priority) => {
      const issue = makeIssue({ priority })
      renderCard(issue)

      const card = screen.getByTestId('issue-card')
      const style = (card as HTMLElement).style
      const expected = hexToRgbString(getPriorityStripColor(priority))
      expect(style.borderLeftColor).toBe(expected)
    },
  )

  it('falls back to the gray priority strip color when priority is null', () => {
    const issue = makeIssue({ priority: null })
    renderCard(issue)

    const card = screen.getByTestId('issue-card')
    const style = (card as HTMLElement).style
    expect(style.borderLeftColor).toBe(hexToRgbString(getPriorityStripColor(null)))
  })

  it('renders distinct strip colors for distinct priorities', () => {
    const rendered: Record<string, string> = {}
    for (const priority of ['p0', 'p1', 'p2', 'p3', 'p4'] as const) {
      const issue = makeIssue({ priority, number: 200 + parseInt(priority.slice(1), 10) })
      const { unmount } = renderCard(issue)
      const card = screen.getByTestId('issue-card') as HTMLElement
      rendered[priority] = card.style.borderLeftColor
      unmount()
      cleanup()
    }
    const uniqueStripColors = new Set(Object.values(rendered))
    expect(uniqueStripColors.size).toBe(5)
  })

  it('does not derive the strip color from labels (no type labels still produces a colored strip)', () => {
    const issue = makeIssue({ priority: 'p1', labels: {} })
    renderCard(issue)

    const card = screen.getByTestId('issue-card') as HTMLElement
    const strip = card.style.borderLeftColor
    const expectedP1 = hexToRgbString(getPriorityStripColor('p1'))
    const expectedP4 = hexToRgbString(getPriorityStripColor('p4'))
    expect(strip).toBe(expectedP1)
    expect(strip).not.toBe(expectedP4)
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
    issueNumber,
    activeAgents: [{ issueNumber, projectId: 'proj-1' }],
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

  it('blocked fold: keeps issue-number, title, and priority-chip visible alongside the folded status pill', () => {
    const issue = makeIssue({
      priority: 'p1',
      health: IssueHealth.Blocked,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: { stage: 'build', total: 5, completed: 2, running: 0, failed: 0 },
      blockedReason: 'CI failure on main',
    })
    renderCard(issue)

    expect(screen.getByTestId('status-pill')).toBeInTheDocument()
    expect(screen.getByTestId('status-pill')).toHaveTextContent('Blocked · Build 2/5')
    expect(screen.queryByTestId('workflow-stage-badge')).not.toBeInTheDocument()
    expect(screen.queryByTestId('workflow-stage-progress')).not.toBeInTheDocument()
    expect(screen.getByTestId('issue-number')).toBeInTheDocument()
    expect(screen.getByTestId('priority-chip')).toHaveTextContent('P1')
    const card = screen.getByTestId('issue-card')
    expect(card.querySelector('h3')).toBeTruthy()
    expect(card.querySelector('h3')!.textContent).toBe('Implement feature')
  })

  it('approval-awaiting fold: keeps issue-number, title, and priority-chip visible alongside the folded status pill', () => {
    const issue = makeIssue({
      priority: 'p2',
      health: IssueHealth.Active,
      workflowStage: WorkflowStage.Plan,
      workflowStageProgress: { stage: 'plan', total: 5, completed: 2, running: 0, failed: 0 },
      approvalState: {
        stage: WorkflowStage.Plan,
        status: 'awaiting',
        requestedAt: '2026-01-01T00:00:00Z',
      },
    })
    renderCard(issue)

    expect(screen.getByTestId('status-pill')).toBeInTheDocument()
    expect(screen.getByTestId('status-pill')).toHaveTextContent('Approval · Plan 2/5')
    expect(screen.queryByTestId('workflow-stage-badge')).not.toBeInTheDocument()
    expect(screen.queryByTestId('workflow-stage-progress')).not.toBeInTheDocument()
    expect(screen.getByTestId('issue-number')).toBeInTheDocument()
    expect(screen.getByTestId('priority-chip')).toHaveTextContent('P2')
    const card = screen.getByTestId('issue-card')
    expect(card.querySelector('h3')!.textContent).toBe('Implement feature')
  })
})

describe('IssueCard - six-dimension card-density invariant', () => {
  function getTitleElement(card: HTMLElement): HTMLElement {
    const title = card.querySelector('h3')
    expect(title).toBeTruthy()
    return title as HTMLElement
  }

  it('exposes all six dimensions when the card has priority, workflow stage, and stage progress', () => {
    const issue = makeIssue({
      priority: 'p2',
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: { stage: 'build', total: 5, completed: 2, running: 0, failed: 0 },
    })
    renderCard(issue)

    const card = screen.getByTestId('issue-card')

    expect(screen.getByTestId('issue-number')).toHaveTextContent('#201')

    const title = getTitleElement(card)
    expect(title.textContent).toBe('Implement feature')

    expect(screen.getByTestId('priority-chip')).toHaveTextContent('P2')

    expect(screen.queryByTestId('status-pill')).not.toBeInTheDocument()
    expect(screen.getByTestId('workflow-stage-badge')).toHaveTextContent('Build')
    expect(screen.getByTestId('workflow-stage-progress')).toHaveTextContent('2/5')

    expect(screen.getByTestId('rerun-button')).toBeInTheDocument()
  })

  it('exposes all six dimensions for an active issue where the standalone workflow-stage-badge carries the stage and no status pill applies', () => {
    const issue = makeIssue({
      priority: 'p2',
      status: IssueStatus.InProgress,
      workflowStage: WorkflowStage.Plan,
      health: IssueHealth.Active,
    })
    renderCard(issue)

    const card = screen.getByTestId('issue-card')

    expect(screen.getByTestId('issue-number')).toHaveTextContent('#201')
    expect(getTitleElement(card).textContent).toBe('Implement feature')
    expect(screen.getByTestId('priority-chip')).toHaveTextContent('P2')

    expect(screen.queryByTestId('status-pill')).not.toBeInTheDocument()
    expect(screen.getByTestId('workflow-stage-badge')).toHaveTextContent('Plan')
  })

  it('clamps the title to a bounded number of lines via WebkitLineClamp', () => {
    const issue = makeIssue({
      priority: 'p2',
      title:
        'A long title that would otherwise push the card vertically past the column if the card did not clamp it to a bounded number of lines so the column density stays compact for owner scanning',
    })
    renderCard(issue)

    const card = screen.getByTestId('issue-card')
    const title = getTitleElement(card)

    expect((title.style as CSSStyleDeclaration).webkitLineClamp).toBe('2')
    expect((title.style as CSSStyleDeclaration).webkitBoxOrient).toBe('vertical')
    expect((title.style as CSSStyleDeclaration).overflow).toBe('hidden')
    expect((title.style as CSSStyleDeclaration).display).toBe('-webkit-box')
  })

  it('keeps the title clamped for every card when multiple sibling cards are rendered in a column', () => {
    const longTitle =
      'A long title that would otherwise push the card vertically past the column if the card did not clamp it to a bounded number of lines so the column density stays compact for owner scanning'

    const issues: Issue[] = [
      makeIssue({ number: 201, title: longTitle }),
      makeIssue({ number: 202, title: longTitle }),
      makeIssue({ number: 203, title: longTitle }),
      makeIssue({ number: 204, title: longTitle }),
    ]
    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <div data-testid="column-mock">
            {issues.map((issue) => (
              <IssueCard key={issue.number} issue={issue} agentStatus={mockAgentStatus} />
            ))}
          </div>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    const cards = screen.getAllByTestId('issue-card')
    expect(cards).toHaveLength(4)

    for (const card of cards) {
      const title = getTitleElement(card)
      expect((title.style as CSSStyleDeclaration).webkitLineClamp).toBe('2')
      expect((title.style as CSSStyleDeclaration).webkitBoxOrient).toBe('vertical')
      expect((title.style as CSSStyleDeclaration).overflow).toBe('hidden')
      expect((title.style as CSSStyleDeclaration).display).toBe('-webkit-box')
    }

    const columnTitles = Array.from(
      screen.getByTestId('column-mock').querySelectorAll('h3'),
    )
    expect(columnTitles).toHaveLength(4)
    for (const t of columnTitles) {
      expect(t.textContent).toBe(longTitle)
    }
  })

  it('preserves the issue-number and priority-chip for non-active cards that fold stage into the status pill', () => {
    const blocked = makeIssue({
      number: 211,
      priority: 'p1',
      health: IssueHealth.Blocked,
      workflowStage: WorkflowStage.Build,
      workflowStageProgress: { stage: 'build', total: 3, completed: 1, running: 0, failed: 0 },
    })
    renderCard(blocked)

    expect(screen.getByTestId('issue-number')).toHaveTextContent('#211')
    expect(screen.getByTestId('priority-chip')).toHaveTextContent('P1')
    expect(screen.getByTestId('status-pill')).toHaveTextContent('Blocked · Build 1/3')
    expect(screen.queryByTestId('workflow-stage-badge')).not.toBeInTheDocument()
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
