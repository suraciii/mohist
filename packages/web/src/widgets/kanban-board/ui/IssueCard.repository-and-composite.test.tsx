import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { IssueCard } from './IssueCard'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import type { AgentStatus } from '../../../entities/agent'

const mockAgentStatus: AgentStatus = {
  running: false,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 501,
    title: 'Composite parent',
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

describe('IssueCard - repository label identifies the persisted target repository', () => {
  it('renders the resolved repository.name on every card', () => {
    const issue = makeIssue({
      repository: { name: 'web', gitUrl: 'git@x/web.git', baseBranch: 'main' },
    })
    renderCard(issue)

    const chip = screen.getByTestId('issue-card-repository')
    expect(chip).toHaveTextContent('web')
    expect(chip).toHaveAttribute('data-repository', 'web')
  })

  it('falls back to the persisted repositoryName when repository is unresolved', () => {
    const issue = makeIssue({ repository: null, repositoryName: 'server' })
    renderCard(issue)

    const chip = screen.getByTestId('issue-card-repository')
    expect(chip).toHaveTextContent('server')
    expect(chip).toHaveAttribute('data-repository', 'server')
  })

  it('prefers the resolved repository.name over the persisted repositoryName when both are present', () => {
    const issue = makeIssue({
      repository: { name: 'web', gitUrl: 'git@x/web.git', baseBranch: 'main' },
      repositoryName: 'legacy-name',
    })
    renderCard(issue)

    const chip = screen.getByTestId('issue-card-repository')
    expect(chip).toHaveTextContent('web')
    expect(chip).toHaveAttribute('data-repository', 'web')
  })

  it('renders the repository chip for default-assigned single-repository projects', () => {
    const issue = makeIssue({ repositoryName: 'main' })
    renderCard(issue)

    expect(screen.getByTestId('issue-card-repository')).toHaveTextContent('main')
  })

  it('does not render the repository chip when the issue has no persisted repository', () => {
    const issue = makeIssue({ repository: null, repositoryName: null })
    renderCard(issue)

    expect(screen.queryByTestId('issue-card-repository')).not.toBeInTheDocument()
  })
})

describe('IssueCard - parent composite progress and blocked-child indicators', () => {
  it('does not render the parent progress badge on an ordinary issue without children', () => {
    const issue = makeIssue()
    renderCard(issue)

    expect(screen.queryByTestId('parent-progress-badge')).not.toBeInTheDocument()
  })

  it('renders "X/Y done" using childIssuesSummary.doneCount and total count (cancelled included in denominator)', () => {
    const issue = makeIssue({
      childIssuesSummary: {
        hasChildren: true,
        count: 4,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 2,
        cancelledCount: 1,
        blockedCount: 0,
      },
    })
    renderCard(issue)

    const badge = screen.getByTestId('parent-progress-badge')
    expect(badge).toHaveTextContent('2/4 done')
    expect(badge).toHaveAttribute('data-done', '2')
    expect(badge).toHaveAttribute('data-total', '4')
    expect(badge).toHaveAttribute('data-completed', 'false')
  })

  it('marks the parent progress badge as completed when all children are done', () => {
    const issue = makeIssue({
      childIssuesSummary: {
        hasChildren: true,
        count: 3,
        backlogCount: 0,
        inProgressCount: 0,
        doneCount: 3,
        cancelledCount: 0,
        blockedCount: 0,
      },
    })
    renderCard(issue)

    const badge = screen.getByTestId('parent-progress-badge')
    expect(badge).toHaveTextContent('3/3 done')
    expect(badge).toHaveAttribute('data-completed', 'true')
  })

  it('does not increment doneCount for cancelled children', () => {
    const issue = makeIssue({
      childIssuesSummary: {
        hasChildren: true,
        count: 5,
        backlogCount: 0,
        inProgressCount: 0,
        doneCount: 2,
        cancelledCount: 3,
        blockedCount: 0,
      },
    })
    renderCard(issue)

    const badge = screen.getByTestId('parent-progress-badge')
    expect(badge).toHaveTextContent('2/5 done')
    expect(badge).toHaveAttribute('data-done', '2')
    expect(badge).toHaveAttribute('data-total', '5')
  })

  it('renders the blocked-child attention indicator independently of the parent health', () => {
    const issue = makeIssue({
      health: IssueHealth.Active,
      childIssuesSummary: {
        hasChildren: true,
        count: 3,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 2,
      },
    })
    renderCard(issue)

    const indicator = screen.getByTestId('blocked-children-indicator')
    expect(indicator).toHaveAttribute('data-blocked-count', '2')
    expect(indicator).toHaveTextContent('2 blocked')
  })

  it('still renders the blocked-child indicator when the parent itself is blocked (independent)', () => {
    const issue = makeIssue({
      health: IssueHealth.Blocked,
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 0,
        cancelledCount: 0,
        blockedCount: 1,
      },
    })
    renderCard(issue)

    expect(screen.getByTestId('blocked-children-indicator')).toHaveTextContent('1 blocked')
  })

  it('does not render the blocked-child indicator when blockedCount is zero', () => {
    const issue = makeIssue({
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 0,
      },
    })
    renderCard(issue)

    expect(screen.queryByTestId('blocked-children-indicator')).not.toBeInTheDocument()
  })

  it('does not render either composite indicator for an ordinary issue even with all fields present-but-empty', () => {
    const issue = makeIssue({
      childIssuesSummary: {
        hasChildren: false,
        count: 0,
        backlogCount: 0,
        inProgressCount: 0,
        doneCount: 0,
        cancelledCount: 0,
        blockedCount: 0,
      },
    })
    renderCard(issue)

    expect(screen.queryByTestId('parent-progress-badge')).not.toBeInTheDocument()
    expect(screen.queryByTestId('blocked-children-indicator')).not.toBeInTheDocument()
  })

  it('groups repository, progress, and blocked indicator in a single metadata row', () => {
    const issue = makeIssue({
      repositoryName: 'web',
      childIssuesSummary: {
        hasChildren: true,
        count: 4,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 2,
        cancelledCount: 1,
        blockedCount: 1,
      },
    })
    renderCard(issue)

    const row = screen.getByTestId('issue-card-metadata-row')
    expect(row).toContainElement(screen.getByTestId('issue-card-repository'))
    expect(row).toContainElement(screen.getByTestId('parent-progress-badge'))
    expect(row).toContainElement(screen.getByTestId('blocked-children-indicator'))
  })
})