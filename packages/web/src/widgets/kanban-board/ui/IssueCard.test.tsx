// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueCard } from './IssueCard'
import { IssueStatus, IssueHealth, type Issue } from '../../../entities/issue'
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

describe('IssueCard - workflow profile chip', () => {
  it('renders the workflow profile chip when the read model carries a selection', () => {
    const issue = makeIssue({ workflowProfileId: 'mohist/github-pr' })
    renderCard(issue)

    const chip = screen.getByTestId('issue-card-workflow-profile')
    expect(chip).toBeInTheDocument()
    expect(chip).toHaveTextContent('mohist/github-pr')
    expect(chip.dataset.workflowProfile).toBe('mohist/github-pr')
  })

  it('renders the inherited default profile when the read model has no selection', () => {
    const issue = makeIssue({ workflowProfileId: null })
    renderCard(issue)

    const chip = screen.getByTestId('issue-card-workflow-profile')
    expect(chip).toBeInTheDocument()
    expect(chip).toHaveTextContent('mohist/default')
    expect(chip.dataset.workflowProfile).toBe('mohist/default')
  })

})
